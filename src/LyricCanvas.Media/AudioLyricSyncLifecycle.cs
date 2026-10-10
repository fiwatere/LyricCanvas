using System.Diagnostics;

namespace LyricCanvas.Media;

/// <summary>用于隔离旧识别任务结果的单次校准运行身份。</summary>
public readonly record struct AudioLyricSyncRun(long Generation, string TrackKey);

/// <summary>
/// 校准任务生命周期与延迟重试策略。状态对象不依赖 WPF、播放器或真实时钟，便于离线验证。
/// </summary>
public sealed class AudioLyricSyncLifecycle
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(16),
        TimeSpan.FromSeconds(30),
    ];

    private long _generation;
    private string _trackKey = string.Empty;
    private bool _isRunning;
    private int _consecutiveFailures;
    private DateTimeOffset _retryAfterUtc = DateTimeOffset.MinValue;

    public bool IsRunning => _isRunning;

    public AudioLyricSyncRun? TryStart(string trackKey, bool isPlaying, DateTimeOffset nowUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(trackKey);
        if (!isPlaying || _isRunning)
        {
            return null;
        }

        if (!string.Equals(_trackKey, trackKey, StringComparison.Ordinal))
        {
            _trackKey = trackKey;
            _consecutiveFailures = 0;
            _retryAfterUtc = DateTimeOffset.MinValue;
        }

        if (nowUtc < _retryAfterUtc)
        {
            return null;
        }

        _isRunning = true;
        var generation = ++_generation;
        return new AudioLyricSyncRun(generation, trackKey);
    }

    public bool IsCurrent(AudioLyricSyncRun run)
        => _isRunning
           && run.Generation == _generation
           && string.Equals(run.TrackKey, _trackKey, StringComparison.Ordinal);

    /// <summary>模型与采集均已启动，之前的启动失败不再影响后续重试间隔。</summary>
    public bool MarkStarted(AudioLyricSyncRun run)
    {
        if (!IsCurrent(run))
        {
            return false;
        }

        _consecutiveFailures = 0;
        _retryAfterUtc = DateTimeOffset.MinValue;
        return true;
    }

    /// <summary>
    /// 结束当前运行。返回 null 表示这是已失效运行；取消返回零时长；异常结束返回下次重试等待时长。
    /// </summary>
    public TimeSpan? Complete(AudioLyricSyncRun run, bool wasCancelled, DateTimeOffset nowUtc)
    {
        if (!IsCurrent(run))
        {
            return null;
        }

        _isRunning = false;
        ++_generation;
        if (wasCancelled)
        {
            return TimeSpan.Zero;
        }

        var delayIndex = Math.Min(_consecutiveFailures, RetryDelays.Length - 1);
        var delay = RetryDelays[delayIndex];
        _consecutiveFailures++;
        _retryAfterUtc = nowUtc + delay;
        return delay;
    }

    /// <summary>暂停、拖动或切歌时立即使运行身份失效，防止已经排队的结果回写。</summary>
    public void Invalidate()
    {
        _isRunning = false;
        ++_generation;
    }

    /// <summary>使用单调时钟补偿识别完成后、UI 应用结果前经过的播放时间。</summary>
    public static TimeSpan CompensateRecognitionDelay(
        TimeSpan positionAtCapture,
        TimeSpan elapsedSinceCapture,
        bool isPlaying)
    {
        if (!isPlaying || elapsedSinceCapture <= TimeSpan.Zero)
        {
            return positionAtCapture;
        }

        return positionAtCapture + elapsedSinceCapture;
    }

    public static TimeSpan GetElapsedSince(long capturedAtTimestamp)
        => capturedAtTimestamp <= 0
            ? TimeSpan.Zero
            : Stopwatch.GetElapsedTime(capturedAtTimestamp);
}
