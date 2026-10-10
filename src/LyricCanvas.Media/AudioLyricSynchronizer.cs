using System.Buffers.Binary;
using System.Diagnostics;
using System.Text;
using LyricCanvas.Core;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace LyricCanvas.Media;

/// <summary>
/// Captures system playback audio in memory, transcribes short overlapping windows locally,
/// and uses sequential lyric matches to re-anchor an LRC timeline.
/// </summary>
public sealed class AudioLyricSynchronizer
{
    private const int TargetSampleRate = 16_000;
    private const int WindowSeconds = 14;
    private const int MinimumWindowSeconds = 4;
    private const int CapturePollSeconds = 1;
    private const int RecognitionCooldownSeconds = 4;
    private const int MaxWindowSamples = TargetSampleRate * WindowSeconds;
    private const double MaximumNoSpeechProbability = 0.45;
    private const double MinimumSpeechProbability = 0.22;
    private const double MinimumLyricMatchScore = 0.50;

    private string _status = "未启用";
    private int _runId;
    private readonly SemaphoreSlim _runGate = new(1, 1);

    public string Status => Volatile.Read(ref _status);

    public event Action<string>? StatusChanged;
    public event Action<long>? RunStarted;
    public event Action<AudioLyricAlignment>? AlignmentFound;

    public async Task RunAsync(
        string trackKey,
        long runGeneration,
        IReadOnlyList<LyricLineTiming> timings,
        string modelPath,
        Func<bool> shouldCaptureAudio,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(shouldCaptureAudio);
        var runId = Interlocked.Increment(ref _runId);
        var gateAcquired = false;
        Exception? captureError = null;

        try
        {
            await _runGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateAcquired = true;

            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                SetStatus(runId, "实时识别需要 Windows 11；当前仍使用播放器进度估算。");
                return;
            }

            if (timings.Count < 2)
            {
                SetStatus(runId, "歌词少于两句，无法做连续匹配校准。");
                return;
            }

            // 当前歌曲不变时匹配特征也不变，无需在每段识别结果上重建整份歌词的特征。
            var preparedLyrics = PrepareLyrics(timings);
            SetStatus(runId, "正在准备本地语音识别模型…");
            modelPath = await EnsureModelAsync(modelPath, cancellationToken).ConfigureAwait(false);
            var loadedModel = await LoadFactoryWithRecoveryAsync(runId, modelPath, cancellationToken)
                .ConfigureAwait(false);
            modelPath = loadedModel.ModelPath;

            using var factory = loadedModel.Factory;
            using var processor = factory.CreateBuilder()
                .WithLanguage("auto")
                .Build();

            // Dispose the capture before its linked token source. WASAPI may finish a
            // DataAvailable callback while StopRecording/Dispose is unwinding.
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var runToken = runCancellation.Token;
            using var capture = new WasapiLoopbackCapture();
            var captureFormat = NormalizeFormat(capture.WaveFormat);
            if (captureFormat.Encoding is not (WaveFormatEncoding.IeeeFloat or WaveFormatEncoding.Pcm))
            {
                SetStatus(runId, $"当前输出格式暂不支持：{captureFormat.Encoding}。");
                return;
            }

            var ring = new AudioSampleRing(MaxWindowSamples);
            capture.DataAvailable += (_, args) =>
            {
                if (runToken.IsCancellationRequested
                    || !shouldCaptureAudio())
                {
                    return;
                }

                try
                {
                    var samples = DecodeAndResample(args.Buffer, args.BytesRecorded, captureFormat);
                    if (samples.Length > 0)
                    {
                        ring.Append(samples, Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception ex)
                {
                    if (Interlocked.CompareExchange(ref captureError, ex, null) is null)
                    {
                        SetStatus(runId, $"读取系统音频失败：{ex.Message}");
                        runCancellation.Cancel();
                    }
                }
            };

            capture.StartRecording();
            RunStarted?.Invoke(runGeneration);
            SetStatus(runId, $"本地识别已就绪，等待播放器播放…（模型目录：{Path.GetDirectoryName(modelPath)}）");

            long lastProcessedSample = 0;
            long lastCandidateSample = -TargetSampleRate;
            MatchCandidate? previousMatch = null;

            while (!runToken.IsCancellationRequested)
            {
                if (!shouldCaptureAudio())
                {
                    // 暂停期间清掉识别窗口，避免恢复播放后把暂停前后的歌声拼成一段。
                    ring.Clear();
                    previousMatch = null;
                    SetStatus(runId, "本地识别已就绪，等待酷狗或其他播放器播放…");
                    await Task.Delay(TimeSpan.FromSeconds(CapturePollSeconds), runToken).ConfigureAwait(false);
                    continue;
                }

                var snapshot = ring.Snapshot(TargetSampleRate * MinimumWindowSeconds);
                if (snapshot.Samples.Length < TargetSampleRate * MinimumWindowSeconds)
                {
                    SetStatus(runId, "已检测到播放，正在采集系统输出音频…");
                    await Task.Delay(TimeSpan.FromSeconds(CapturePollSeconds), runToken).ConfigureAwait(false);
                    continue;
                }

                var rms = GetRms(snapshot.Samples);
                if (rms < 0.0015)
                {
                    SetStatus(runId, "已采集到音频，但电平很低或暂时没有明显人声；正在继续监听…");
                    await Task.Delay(TimeSpan.FromSeconds(CapturePollSeconds), runToken).ConfigureAwait(false);
                    continue;
                }

                if (snapshot.EndSample <= lastProcessedSample + TargetSampleRate * 2L)
                {
                    await Task.Delay(TimeSpan.FromSeconds(CapturePollSeconds), runToken).ConfigureAwait(false);
                    continue;
                }

                lastProcessedSample = snapshot.EndSample;
                var windowSeconds = snapshot.Samples.Length / (double)TargetSampleRate;
                SetStatus(runId,
                    $"正在识别系统音频并和歌词匹配…（音频窗口 {windowSeconds:0.0} 秒，电平 {rms:0.000}）");

                var segmentCount = 0;
                var emptySegmentCount = 0;
                var noSpeechSegmentCount = 0;
                var lowConfidenceSegmentCount = 0;
                var usableSegmentCount = 0;
                var latestTranscript = string.Empty;
                var latestSpeechProbability = 0d;
                var latestNoSpeechProbability = 0d;
                MatchCandidate? bestCandidate = null;
                var candidateStatus = string.Empty;
                var alignmentFound = false;
                // Whisper.net cancellation can stop its async iterator before the native
                // inference task has finished. Keep draining this one inference so the
                // processor and model are not disposed while ggml is still using them.
                await foreach (var segment in processor.ProcessAsync(snapshot.Samples, CancellationToken.None)
                                   .ConfigureAwait(false))
                {
                    segmentCount++;
                    if (runToken.IsCancellationRequested)
                    {
                        // Pause, seek, track change, or capture failure invalidated this
                        // sample. Drain the native result, but never use it for alignment.
                        continue;
                    }

                    var transcript = segment.Text?.Trim() ?? string.Empty;
                    if (transcript.Length == 0)
                    {
                        emptySegmentCount++;
                        continue;
                    }

                    latestTranscript = transcript;
                    latestSpeechProbability = segment.Probability;
                    latestNoSpeechProbability = segment.NoSpeechProbability;
                    if (segment.NoSpeechProbability > MaximumNoSpeechProbability)
                    {
                        noSpeechSegmentCount++;
                        continue;
                    }

                    if (segment.Probability < MinimumSpeechProbability)
                    {
                        lowConfidenceSegmentCount++;
                        continue;
                    }

                    usableSegmentCount++;
                    var allCandidates = FindLyricMatches(transcript, preparedLyrics);
                    var bestSegmentCandidate = allCandidates.FirstOrDefault();
                    if (bestSegmentCandidate is not null
                        && (bestCandidate is null || bestSegmentCandidate.Score > bestCandidate.Score))
                    {
                        bestCandidate = bestSegmentCandidate;
                    }

                    var candidates = allCandidates
                        .Where(candidate => candidate.Score >= MinimumLyricMatchScore)
                        .ToArray();
                    if (candidates.Length == 0)
                    {
                        continue;
                    }

                    var strongest = candidates[0];
                    var currentCandidates = candidates;
                    var uniqueLead = currentCandidates.Length == 1
                                     || strongest.Score - currentCandidates[1].Score >= 0.16;
                    candidateStatus = uniqueLead
                        ? $"识别到第 {strongest.Timing.Index + 1} 句候选（相似度 {strongest.Score:P0}），等待下一句确认。片段概率 {segment.Probability:P0}，无语音概率 {segment.NoSpeechProbability:P0}；文本：“{FormatSnippet(transcript)}”"
                        : $"识别到歌词候选但存在相似结果，等待下一句消歧。最佳候选：{FormatCandidate(strongest)}；片段概率 {segment.Probability:P0}，无语音概率 {segment.NoSpeechProbability:P0}；文本：“{FormatSnippet(transcript)}”";

                    var sample = snapshot.StartSample +
                                 (long)(segment.Start.TotalSeconds * TargetSampleRate);
                    if (sample <= lastCandidateSample + TargetSampleRate / 2)
                    {
                        continue;
                    }

                    lastCandidateSample = sample;
                    currentCandidates = currentCandidates
                        .Select(candidate => candidate with { AudioSample = sample })
                        .ToArray();
                    // Keep the sampled candidate as the next anchor. `strongest` above
                    // still has AudioSample=0 because FindLyricMatches creates candidates
                    // before their position in the captured audio window is known.
                    var strongestWithTimestamp = currentCandidates[0];
                    var priorMatch = previousMatch;
                    var confirmed = priorMatch is null
                        ? null
                        : currentCandidates
                            .Select(candidate => (Candidate: candidate, Coherence: GetSequenceCoherence(priorMatch, candidate)))
                            .Where(item => item.Coherence is not null)
                            // If the strongest text match is a different lyric, don't let
                            // a much weaker phrase win only because its timestamp happens
                            // to fit the previous candidate.
                            .Where(item => item.Candidate.Score >= strongestWithTimestamp.Score - 0.12)
                            .OrderBy(item => item.Coherence!.Value)
                            .ThenByDescending(item => item.Candidate.Score)
                            .Select(item => item.Candidate)
                            .FirstOrDefault();

                    // A single recognized phrase is only a candidate. Require a second
                    // lyric whose order and elapsed time agree before moving the overlay.
                    var alignedMatch = confirmed;
                    if (alignedMatch is not null)
                    {
                        alignmentFound = true;
                        var elapsedAfterMatch = TimeSpan.FromSeconds(
                            Math.Max(0, snapshot.EndSample - alignedMatch.AudioSample) /
                            (double)TargetSampleRate);
                        var alignment = new AudioLyricAlignment(
                            trackKey,
                            runGeneration,
                            snapshot.CapturedAtTimestamp,
                            alignedMatch.Timing.Start + elapsedAfterMatch,
                            alignedMatch.Score);
                        var sequenceError = GetSequenceCoherence(priorMatch!, alignedMatch) ?? TimeSpan.Zero;
                        SetStatus(runId,
                            $"连续匹配确认第 {priorMatch!.Timing.Index + 1}→{alignedMatch.Timing.Index + 1} 句（相似度 {priorMatch.Score:P0}/{alignedMatch.Score:P0}，间隔误差 {sequenceError.TotalSeconds:0.0} 秒）：“{FormatSnippet(alignedMatch.Timing.Text, 48)}”；正在应用位置校正。");
                        if (!runToken.IsCancellationRequested)
                        {
                            AlignmentFound?.Invoke(alignment);
                        }
                    }

                    previousMatch = alignedMatch ?? strongestWithTimestamp;
                }

                // Cancellation is intentionally checked only after ProcessAsync drains.
                // This prevents disposing Whisper's native context during inference.
                runToken.ThrowIfCancellationRequested();

                if (!alignmentFound)
                {
                    SetStatus(runId, $"{BuildRecognitionStatus(
                        windowSeconds,
                        rms,
                        segmentCount,
                        emptySegmentCount,
                        noSpeechSegmentCount,
                        lowConfidenceSegmentCount,
                        usableSegmentCount,
                        latestTranscript,
                        latestSpeechProbability,
                        latestNoSpeechProbability,
                        bestCandidate,
                        candidateStatus)} 本轮识别完成后等待 {RecognitionCooldownSeconds} 秒再采样。");
                }

                // The audio ring buffer keeps collecting cheaply, while the CPU-heavy
                // Whisper pass sleeps between cycles. Delay starts after inference ends,
                // so a slow inference cannot cause timer ticks to trigger back-to-back work.
                await Task.Delay(TimeSpan.FromSeconds(RecognitionCooldownSeconds), runToken)
                    .ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            SetStatus(runId, "实时歌词校准已关闭。");
        }
        catch (OperationCanceledException) when (captureError is not null)
        {
            SetStatus(runId, $"读取系统音频失败：{captureError.Message}");
        }
        catch (Exception ex)
        {
            SetStatus(runId, $"实时歌词校准不可用：{ex.Message}");
        }
        finally
        {
            if (gateAcquired)
            {
                _runGate.Release();
            }
        }
    }

    private static async Task<string> EnsureModelAsync(string modelPath, CancellationToken cancellationToken)
    {
        var modelFileName = Path.GetFileName(modelPath);
        _ = Path.GetDirectoryName(modelPath)
            ?? throw new InvalidOperationException("无法确定模型保存目录。");
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roamingAppData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var tempDirectory = Path.GetTempPath();
        var candidatePaths = new[]
            {
                modelPath,
                string.IsNullOrWhiteSpace(roamingAppData)
                    ? string.Empty
                    : Path.Combine(roamingAppData, "LyricCanvas", "models", modelFileName),
                string.IsNullOrWhiteSpace(userProfile)
                    ? string.Empty
                    : Path.Combine(userProfile, "LyricCanvas", "models", modelFileName),
                string.IsNullOrWhiteSpace(tempDirectory)
                    ? string.Empty
                    : Path.Combine(tempDirectory, "LyricCanvas", "models", modelFileName),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        // 先读取所有可用模型，再挑可写目录，避免模型在备用目录时仍重复下载。
        var legacyModelPaths = new[]
            {
                string.IsNullOrWhiteSpace(localAppData)
                    ? string.Empty
                    : Path.Combine(localAppData, "LyricDesktop", "models", modelFileName),
                string.IsNullOrWhiteSpace(roamingAppData)
                    ? string.Empty
                    : Path.Combine(roamingAppData, "LyricDesktop", "models", modelFileName),
            }
            .Where(path => !string.IsNullOrWhiteSpace(path));
        foreach (var existingPath in candidatePaths.Concat(legacyModelPaths))
        {
            try
            {
                if (File.Exists(existingPath) && new FileInfo(existingPath).Length > 1024 * 1024)
                {
                    return existingPath;
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        var writeErrors = new List<string>();
        string? writableModelPath = null;
        foreach (var candidatePath in candidatePaths)
        {
            var directory = Path.GetDirectoryName(candidatePath)
                            ?? throw new InvalidOperationException("无法确定模型保存目录。");
            var probePath = Path.Combine(directory, $".write-check-{Guid.NewGuid():N}.tmp");
            try
            {
                Directory.CreateDirectory(directory);
                using (new FileStream(probePath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                }

                File.Delete(probePath);
                writableModelPath = candidatePath;
                break;
            }
            catch (IOException ex)
            {
                writeErrors.Add($"{directory}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                writeErrors.Add($"{directory}: {ex.Message}");
            }
            catch (System.Security.SecurityException ex)
            {
                writeErrors.Add($"{directory}: {ex.Message}");
            }
            finally
            {
                try
                {
                    File.Delete(probePath);
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (System.Security.SecurityException)
                {
                }
            }
        }

        if (writableModelPath is null)
        {
            throw new UnauthorizedAccessException(
                "无法写入语音模型目录。已尝试：" + string.Join("；", writeErrors));
        }

        var temporaryPath = writableModelPath + ".download";

        try
        {
            using var modelStream = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                GgmlType.Tiny,
                QuantizationType.Q5_1,
                cancellationToken).ConfigureAwait(false);
            await using (var file = new FileStream(
                             temporaryPath,
                             FileMode.Create,
                             FileAccess.Write,
                             FileShare.None,
                             bufferSize: 81920,
                             useAsync: true))
            {
                await modelStream.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
                await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            if (new FileInfo(temporaryPath).Length <= 1024 * 1024)
            {
                throw new InvalidDataException("下载的语音模型文件不完整。");
            }

            File.Move(temporaryPath, writableModelPath, overwrite: true);
            return writableModelPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task<(WhisperFactory Factory, string ModelPath)> LoadFactoryWithRecoveryAsync(
        int runId,
        string modelPath,
        CancellationToken cancellationToken)
    {
        try
        {
            return (CreateValidatedFactory(modelPath), modelPath);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && IsLikelyModelContentError(ex))
        {
            var quarantinedPath = AudioLyricModelRecovery.TryQuarantine(
                modelPath,
                GetManagedModelDirectories(),
                DateTimeOffset.UtcNow);
            if (quarantinedPath is null)
            {
                throw new InvalidDataException(
                    $"语音模型无法加载，但词绘无法安全隔离该文件，原文件未修改：{modelPath}",
                    ex);
            }

            SetStatus(runId, $"检测到损坏模型，原文件已保留在 {quarantinedPath}；正在重新获取模型…");
            var recoveredPath = await EnsureModelAsync(modelPath, cancellationToken).ConfigureAwait(false);
            try
            {
                return (CreateValidatedFactory(recoveredPath), recoveredPath);
            }
            catch (Exception retryException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidDataException(
                    $"重新获取的语音模型仍无法加载。原文件已保留在：{quarantinedPath}",
                    retryException);
            }
        }
    }

    private static WhisperFactory CreateValidatedFactory(string modelPath)
    {
        var factory = WhisperFactory.FromPath(modelPath);
        try
        {
            using var validationProcessor = factory.CreateBuilder()
                .WithLanguage("auto")
                .Build();
            return factory;
        }
        catch
        {
            factory.Dispose();
            throw;
        }
    }

    private static string[] GetManagedModelDirectories()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Path.GetTempPath(),
        };

        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => Path.Combine(root, "LyricCanvas", "models"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static bool IsLikelyModelContentError(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is DllNotFoundException
                or EntryPointNotFoundException
                or PlatformNotSupportedException
                or OutOfMemoryException)
            {
                return false;
            }

            if (current is InvalidDataException or EndOfStreamException or FormatException)
            {
                return true;
            }

            if (current.GetType().Name.Contains("Whisper", StringComparison.OrdinalIgnoreCase)
                && current.Message.Contains("model", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private void SetStatus(int runId, string status)
    {
        if (runId != Volatile.Read(ref _runId))
        {
            return;
        }

        if (string.Equals(Interlocked.Exchange(ref _status, status), status, StringComparison.Ordinal))
        {
            return;
        }

        StatusChanged?.Invoke(status);
    }

    private static WaveFormat NormalizeFormat(WaveFormat format)
        => format is WaveFormatExtensible extensible
            ? extensible.ToStandardWaveFormat()
            : format;

    private static float[] DecodeAndResample(byte[] buffer, int bytesRecorded, WaveFormat format)
    {
        var bytesPerSample = format.BitsPerSample / 8;
        var bytesPerFrame = bytesPerSample * format.Channels;
        if (bytesPerSample is not (2 or 3 or 4) || bytesPerFrame <= 0)
        {
            throw new NotSupportedException($"不支持 {format.BitsPerSample} 位音频。");
        }

        var frames = bytesRecorded / bytesPerFrame;
        if (frames == 0)
        {
            return Array.Empty<float>();
        }

        var mono = new float[frames];
        var isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat && format.BitsPerSample == 32;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat && !isFloat)
        {
            throw new NotSupportedException("仅支持 32 位浮点输出。");
        }

        for (var frame = 0; frame < frames; frame++)
        {
            double sum = 0;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * bytesPerFrame + channel * bytesPerSample;
                sum += ReadSample(buffer, offset, format.BitsPerSample, isFloat);
            }

            mono[frame] = (float)Math.Clamp(sum / format.Channels, -1, 1);
        }

        if (format.SampleRate == TargetSampleRate)
        {
            return mono;
        }

        var outputCount = (int)Math.Floor(frames * (double)TargetSampleRate / format.SampleRate);
        var output = new float[outputCount];
        for (var index = 0; index < outputCount; index++)
        {
            var start = (int)Math.Floor(index * (double)format.SampleRate / TargetSampleRate);
            var end = (int)Math.Floor((index + 1) * (double)format.SampleRate / TargetSampleRate);
            end = Math.Clamp(end, start + 1, frames);

            double sum = 0;
            for (var sourceIndex = start; sourceIndex < end; sourceIndex++)
            {
                sum += mono[sourceIndex];
            }

            output[index] = (float)(sum / (end - start));
        }

        return output;
    }

    private static float ReadSample(byte[] buffer, int offset, int bitsPerSample, bool isFloat)
    {
        if (isFloat)
        {
            return BitConverter.ToSingle(buffer, offset);
        }

        return bitsPerSample switch
        {
            16 => BinaryPrimitives.ReadInt16LittleEndian(buffer.AsSpan(offset, 2)) / 32768f,
            24 => ReadInt24(buffer, offset) / 8388608f,
            32 => BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4)) / 2147483648f,
            _ => throw new NotSupportedException($"不支持 {bitsPerSample} 位 PCM 音频。"),
        };
    }

    private static int ReadInt24(byte[] buffer, int offset)
    {
        var value = buffer[offset] | buffer[offset + 1] << 8 | buffer[offset + 2] << 16;
        return (value & 0x800000) != 0 ? value | unchecked((int)0xFF000000) : value;
    }

    private static double GetRms(float[] samples)
    {
        if (samples.Length == 0)
        {
            return 0;
        }

        double sum = 0;
        foreach (var sample in samples)
        {
            sum += sample * sample;
        }

        return Math.Sqrt(sum / samples.Length);
    }

    private static IReadOnlyList<PreparedLyric> PrepareLyrics(IReadOnlyList<LyricLineTiming> timings)
    {
        var normalizedLines = timings.Select(timing => Normalize(timing.Text)).ToArray();
        var prepared = new PreparedLyric[timings.Count];
        for (var index = 0; index < timings.Count; index++)
        {
            var combined = new StringBuilder();
            var bigrams = new List<HashSet<string>>(3);
            for (var count = 1; count <= 3 && index + count <= timings.Count; count++)
            {
                combined.Append(normalizedLines[index + count - 1]);
                if (combined.Length >= 5)
                {
                    bigrams.Add(GetBigrams(combined.ToString()));
                }
            }

            prepared[index] = new PreparedLyric(timings[index], bigrams);
        }

        return prepared;
    }

    private static IReadOnlyList<MatchCandidate> FindLyricMatches(
        string transcription,
        IReadOnlyList<PreparedLyric> preparedLyrics)
    {
        var normalizedTranscript = Normalize(transcription);
        if (normalizedTranscript.Length < 5)
        {
            return Array.Empty<MatchCandidate>();
        }

        var transcriptBigrams = GetBigrams(normalizedTranscript);
        var candidates = new List<MatchCandidate>();

        foreach (var lyric in preparedLyrics)
        {
            double bestScore = 0;
            foreach (var bigrams in lyric.Bigrams)
            {
                bestScore = Math.Max(bestScore, Similarity(transcriptBigrams, bigrams));
            }

            if (bestScore > 0)
            {
                candidates.Add(new MatchCandidate(lyric.Timing, bestScore, 0));
            }
        }

        return candidates
            .OrderByDescending(candidate => candidate.Score)
            .ToArray();
    }

    private static string BuildRecognitionStatus(
        double windowSeconds,
        double rms,
        int segmentCount,
        int emptySegmentCount,
        int noSpeechSegmentCount,
        int lowConfidenceSegmentCount,
        int usableSegmentCount,
        string latestTranscript,
        double latestSpeechProbability,
        double latestNoSpeechProbability,
        MatchCandidate? bestCandidate,
        string candidateStatus)
    {
        if (candidateStatus.Length > 0)
        {
            return candidateStatus;
        }

        var audioSummary = $"窗口 {windowSeconds:0.0} 秒，电平 {rms:0.000}";
        if (segmentCount == 0)
        {
            return $"音频已送入识别模型（{audioSummary}），但本轮模型没有返回文本。";
        }

        if (usableSegmentCount == 0)
        {
            var transcriptSummary = latestTranscript.Length == 0
                ? "没有可显示的识别文本。"
                : $"最近文本：“{FormatSnippet(latestTranscript)}”；识别置信度 {latestSpeechProbability:P1}，疑似无语音概率 {latestNoSpeechProbability:P1}。";
            return $"模型返回 {segmentCount} 个片段，但没有片段通过识别筛选（空文本 {emptySegmentCount}，疑似无语音 {noSpeechSegmentCount}，低文本概率 {lowConfidenceSegmentCount}；{audioSummary}）。{transcriptSummary}";
        }

        if (bestCandidate is null)
        {
            return $"识别到文本，但没有可比较的歌词候选（{audioSummary}）。最近文本：“{FormatSnippet(latestTranscript)}”。";
        }

        return $"识别到文本，但最佳歌词相似度未达到 {MinimumLyricMatchScore:P0} 匹配门槛。最佳候选：{FormatCandidate(bestCandidate)}；识别文本：“{FormatSnippet(latestTranscript)}”。";
    }

    private static string FormatCandidate(MatchCandidate candidate)
        => $"第 {candidate.Timing.Index + 1} 句“{FormatSnippet(candidate.Timing.Text, 36)}”（{candidate.Score:P0}）";

    private static string FormatSnippet(string text, int maxLength = 72)
    {
        var compact = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return compact.Length <= maxLength
            ? compact
            : compact[..maxLength] + "…";
    }

    private static TimeSpan? GetSequenceCoherence(MatchCandidate previous, MatchCandidate current)
    {
        var lineDelta = current.Timing.Index - previous.Timing.Index;
        if (lineDelta is < 1 or > 10 || current.AudioSample <= previous.AudioSample)
        {
            return null;
        }

        var audioDelta = TimeSpan.FromSeconds(
            (current.AudioSample - previous.AudioSample) / (double)TargetSampleRate);
        var lyricDelta = current.Timing.Start - previous.Timing.Start;
        if (lyricDelta <= TimeSpan.Zero)
        {
            return null;
        }

        var difference = (audioDelta - lyricDelta).Duration();
        var tolerance = TimeSpan.FromSeconds(Math.Max(4, lyricDelta.TotalSeconds * 0.75));
        return difference <= tolerance ? difference : null;
    }

    private static string Normalize(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormKC).ToLowerInvariant();
        return string.Concat(normalized.Where(char.IsLetterOrDigit));
    }

    private static HashSet<string> GetBigrams(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index + 1 < text.Length; index++)
        {
            result.Add(text.Substring(index, 2));
        }

        if (result.Count == 0 && text.Length == 1)
        {
            result.Add(text);
        }

        return result;
    }

    private static double Similarity(HashSet<string> left, HashSet<string> right)
    {
        if (left.Count == 0 || right.Count == 0)
        {
            return 0;
        }

        var intersection = left.Count(right.Contains);
        var dice = 2d * intersection / (left.Count + right.Count);
        var containment = (double)intersection / Math.Min(left.Count, right.Count);
        return dice * 0.75 + containment * 0.25;
    }

    private sealed record MatchCandidate(LyricLineTiming Timing, double Score, long AudioSample);
    private sealed record PreparedLyric(LyricLineTiming Timing, IReadOnlyList<HashSet<string>> Bigrams);

    private sealed class AudioSampleRing(int capacity)
    {
        private readonly object _gate = new();
        private readonly Queue<float[]> _chunks = new();
        private int _sampleCount;
        private long _totalSamples;
        private long _lastAppendTimestamp;

        public void Append(float[] samples, long capturedAtTimestamp)
        {
            if (samples.Length == 0)
            {
                return;
            }

            lock (_gate)
            {
                _chunks.Enqueue(samples);
                _sampleCount += samples.Length;
                _totalSamples += samples.Length;
                _lastAppendTimestamp = capturedAtTimestamp;

                while (_sampleCount > capacity && _chunks.TryDequeue(out var first))
                {
                    _sampleCount -= first.Length;
                }
            }
        }

        public (float[] Samples, long StartSample, long EndSample, long CapturedAtTimestamp) Snapshot(int minimumSamples)
        {
            lock (_gate)
            {
                if (_sampleCount < minimumSamples)
                {
                    return (Array.Empty<float>(), _totalSamples, _totalSamples, _lastAppendTimestamp);
                }

                var result = new float[_sampleCount];
                var offset = 0;
                foreach (var chunk in _chunks)
                {
                    chunk.CopyTo(result, offset);
                    offset += chunk.Length;
                }

                var end = _totalSamples;
                return (result, end - _sampleCount, end, _lastAppendTimestamp);
            }
        }

        public void Clear()
        {
            lock (_gate)
            {
                _chunks.Clear();
                _sampleCount = 0;
                _lastAppendTimestamp = 0;
            }
        }
    }
}

public sealed record AudioLyricAlignment(
    string TrackKey,
    long RunGeneration,
    long CapturedAtTimestamp,
    TimeSpan Position,
    double TextSimilarity);
