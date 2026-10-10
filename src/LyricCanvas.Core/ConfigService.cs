using System.Text.Json;
using System.Text.Json.Serialization;
using System.Diagnostics;

namespace LyricCanvas.Core;

/// <summary>
/// 配置的加载、保存与变更通知。
///
/// 默认保存位置：<c>%APPDATA%\LyricCanvas\config.json</c>；标准用户目录不可写时，
/// 依次尝试 LocalAppData 和程序目录旁的便携配置文件。
///
/// 为什么放 APPDATA 而不是程序目录：程序目录可能在 Program Files 下没有写权限，
/// 而且重装/更新时不该把用户配置一起删掉。
///
/// 写入策略：先写临时文件再原子替换。滑块拖动会连续触发保存，
/// 如果直接往原文件上写，正好断电或崩溃就会留下一个半截的配置文件，
/// 下次启动直接解析失败、用户配置全丢。
/// </summary>
public sealed class ConfigService : IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,

        // 下面三个都是为了让"手改配置文件"尽量不容易出错。
        // 实测过：System.Text.Json 默认区分大小写，配置文件里把 "FontSize" 写成
        // "fontSize" 会被静默忽略、退回默认值，而且不报任何错——这种坑太难发现了。
        PropertyNameCaseInsensitive = true,

        // 允许在 JSON 里写 // 注释和尾随逗号，手改时更自在
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private string _filePath;
    private readonly string[] _fallbackFilePaths;
    private readonly string[] _legacyReadFilePaths;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly TimeSpan _saveDelay;
    private readonly bool _autoSave;

    private AppConfig _config;
    private Timer? _saveTimer;
    private bool _disposed;
    private bool _isDirty;
    private bool _hasUnpersistedUserChanges;
    private long _changeVersion;

    /// <summary>
    /// 启动时是否成功读到了配置。
    ///
    /// 用途：如果配置文件是坏的（JSON 语法错误等），绝不能往外写。
    /// 否则程序一退出就把用户原来的文件覆盖成默认值，那份坏文件就永久消失了，
    /// 用户再也没机会看到自己到底写错了什么。
    /// </summary>
    private bool _loadedSuccessfully = true;

    /// <summary>读取失败的原因。读取失败时不覆盖原文件。</summary>
    private string? _loadError;

    /// <summary>最近一次保存失败原因；成功保存后清空。</summary>
    private string? _lastSaveError;
    private int _saveFailureGeneration;
    private int _reportedSaveFailureGeneration;

    /// <summary>旧版配置需要写回新结构，确保迁移结果不会只存在于内存。</summary>
    private bool _migrationSaveNeeded;

    /// <summary>配置发生变化时触发。可能在任意线程触发。</summary>
    public event EventHandler<AppConfig>? Changed;

    /// <summary>配置文件的实际路径。</summary>
    public string FilePath => Volatile.Read(ref _filePath);

    /// <summary>是否有尚未成功落盘的配置修改。</summary>
    public bool IsDirty => Volatile.Read(ref _isDirty);

    /// <summary>是否有用户已提交但尚未成功落盘的修改。</summary>
    public bool HasUnpersistedUserChanges => Volatile.Read(ref _hasUnpersistedUserChanges);

    /// <summary>最近一次保存失败的原因，成功保存后为 null。</summary>
    public string? LastSaveError => Volatile.Read(ref _lastSaveError);

    // Controlled pause point for a deterministic regression test of the save/update race.
    // Production callers never set this hook.
    internal Action? AfterSnapshotSavedForTests { get; set; }

    /// <summary>为同一轮持续保存失败只提示一次；成功后再次失败会开启新一轮提示。</summary>
    public bool TryMarkSaveFailureReported()
    {
        var generation = Volatile.Read(ref _saveFailureGeneration);
        if (generation == 0)
        {
            return false;
        }

        while (true)
        {
            var reported = Volatile.Read(ref _reportedSaveFailureGeneration);
            if (reported >= generation)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _reportedSaveFailureGeneration, generation, reported) == reported)
            {
                return true;
            }
        }
    }

    /// <summary>当前配置。返回的是内部实例的副本，改它不会自动生效，请用 <see cref="Update"/>。</summary>
    public AppConfig Current
    {
        get
        {
            lock (_gate)
            {
                return _config.Clone();
            }
        }
    }

    /// <summary>
    /// 用默认位置（%APPDATA%\LyricCanvas\config.json）创建，并在写入失败时使用回退位置。
    /// </summary>
    public ConfigService()
        : this(GetDefaultConfigPaths())
    {
    }

    private ConfigService(string[] defaultPaths)
        : this(
            defaultPaths[0],
            autoSave: true,
            saveDelay: null,
            fallbackFilePaths: defaultPaths.Skip(1).ToArray(),
            legacyReadFilePaths: GetLegacyConfigPaths())
    {
    }

    /// <summary>
    /// 按命令行参数创建：支持 <c>--config=&lt;文件路径&gt;</c>。
    ///
    /// 这个开关是给自动化验证用的：脚本可以用一份临时配置跑程序，
    /// 既不会污染用户真实的配置，也不必担心测试之间互相干扰。
    /// 也顺便让"带不同配置跑多个实例"成为可能。
    /// </summary>
    public static ConfigService CreateFromCommandLine(string[]? args = null)
    {
        args ??= Environment.GetCommandLineArgs();

        foreach (var arg in args)
        {
            if (arg.StartsWith("--config=", StringComparison.OrdinalIgnoreCase))
            {
                var path = arg["--config=".Length..].Trim('"');
                if (path.Length > 0)
                {
                    return new ConfigService(path);
                }
            }
        }

        return new ConfigService();
    }

    /// <param name="filePath">配置文件路径。传 null 或空表示只用内存，不落盘（单元测试用）。</param>
    /// <param name="autoSave">是否在变更后自动延迟保存。</param>
    /// <param name="saveDelay">自动保存的防抖延迟。</param>
    public ConfigService(string? filePath, bool autoSave = true, TimeSpan? saveDelay = null)
        : this(filePath, autoSave, saveDelay, fallbackFilePaths: null, legacyReadFilePaths: null)
    {
    }

    internal ConfigService(
        string? filePath,
        bool autoSave,
        TimeSpan? saveDelay,
        string[]? fallbackFilePaths,
        string[]? legacyReadFilePaths)
    {
        _filePath = filePath ?? string.Empty;
        _fallbackFilePaths = fallbackFilePaths ?? Array.Empty<string>();
        _legacyReadFilePaths = legacyReadFilePaths ?? Array.Empty<string>();
        _autoSave = autoSave && !string.IsNullOrWhiteSpace(filePath);
        _saveDelay = saveDelay ?? TimeSpan.FromMilliseconds(400);
        _config = Load();
        _isDirty = _migrationSaveNeeded;

        if (_autoSave && _loadedSuccessfully && _migrationSaveNeeded)
        {
            SaveNow();
        }
    }

    /// <summary>默认配置文件路径。</summary>
    public static string GetDefaultFilePath()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return Path.Combine(appData, "LyricCanvas", "config.json");
    }

    private static string GetLocalFallbackFilePath()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "LyricCanvas", "config.json");
    }

    private static string GetPortableFallbackFilePath()
    {
        return Path.Combine(AppContext.BaseDirectory, "LyricCanvas.user.json");
    }

    private static string[] GetDefaultConfigPaths()
    {
        return new[]
        {
            GetDefaultFilePath(),
            GetLocalFallbackFilePath(),
            GetPortableFallbackFilePath(),
        };
    }

    private static string[] GetLegacyConfigPaths()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var paths = new[]
        {
            Path.Combine(appData, "LyricDesktop", "config.json"),
            Path.Combine(localAppData, "LyricDesktop", "config.json"),
            Path.Combine(AppContext.BaseDirectory, "LyricDesktop.user.json"),
        };

        // 旧版曾在多个回退目录间切换；迁移时读取最后一次成功写入的副本。
        return paths
            .Concat(GetLegacyWorkspacePortableConfigPaths())
            .Where(File.Exists)
            .OrderByDescending(GetLastWriteTimeUtcOrMinimum)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IEnumerable<string> GetLegacyWorkspacePortableConfigPaths()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "LyricCanvas.sln")))
        {
            current = current.Parent;
        }

        var projectParent = current?.Parent;
        if (projectParent is null)
        {
            return Array.Empty<string>();
        }

        try
        {
            var legacyRoots = Directory.EnumerateDirectories(projectParent.FullName)
                .Where(path => !string.Equals(path, current!.FullName, StringComparison.OrdinalIgnoreCase))
                .Where(path => File.Exists(Path.Combine(path, "LyricDesktop.sln")));
            var portableConfigs = new List<string>();
            foreach (var legacyRoot in legacyRoots)
            {
                var appBin = Path.Combine(legacyRoot, "src", "LyricDesktop.App", "bin");
                if (!Directory.Exists(appBin))
                {
                    continue;
                }

                portableConfigs.AddRange(
                    Directory.EnumerateFiles(appBin, "LyricDesktop.user.json", SearchOption.AllDirectories));
            }

            return portableConfigs;
        }
        catch (IOException)
        {
            return Array.Empty<string>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
        catch (System.Security.SecurityException)
        {
            return Array.Empty<string>();
        }
    }

    private static DateTime GetLastWriteTimeUtcOrMinimum(string path)
    {
        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (IOException)
        {
            return DateTime.MinValue;
        }
        catch (UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// 修改配置：传入一个改动函数，改完自动保存并通知订阅者。
    /// </summary>
    /// <example>
    /// <code>
    /// config.Update(c => c.Opacity = 0.6);
    /// </code>
    /// </example>
    public void Update(Action<AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppConfig snapshot;
        lock (_gate)
        {
            var previousLayoutId = _config.ActiveLayoutId;
            change(_config);

            if (string.Equals(previousLayoutId, _config.ActiveLayoutId, StringComparison.Ordinal))
            {
                _config.SaveActiveFramesToLayout();
            }

            Normalize(_config);
            Volatile.Write(ref _isDirty, true);
            Volatile.Write(ref _hasUnpersistedUserChanges, true);
            Interlocked.Increment(ref _changeVersion);
            snapshot = _config.Clone();
        }

        ScheduleSave();
        Changed?.Invoke(this, snapshot);
    }

    /// <summary>把当前配置立即写盘。程序退出前应该调一次。</summary>
    public bool SaveNow()
    {
        if (!IsDirty)
        {
            return true;
        }

        var filePath = FilePath;
        if (string.IsNullOrWhiteSpace(filePath))
        {
            RecordSaveFailure("没有配置文件路径。");
            return false;
        }

        // Timer 自动保存、布局编辑器的即时保存和退出保存可能并发触发。
        // 必须让写盘串行，并在轮到当前写入时再取最新快照，避免旧快照最后覆盖新设置。
        lock (_saveGate)
        {
            if (!IsDirty)
            {
                return true;
            }

            // 启动时读到的是坏文件：保持原样不动，让用户能自己去检查
            if (!_loadedSuccessfully)
            {
                RecordSaveFailure(_loadError ?? "配置文件读取失败。为保护原文件，程序拒绝覆盖它。");
                return false;
            }

            AppConfig snapshot;
            long snapshotVersion;
            lock (_gate)
            {
                snapshot = _config.Clone();
                snapshotVersion = _changeVersion;
            }

            if (!TrySaveSnapshot(snapshot, filePath, out var savedPath, out var error))
            {
                RecordSaveFailure(error);
                return false;
            }

            AfterSnapshotSavedForTests?.Invoke();

            Volatile.Write(ref _filePath, savedPath);
            Volatile.Write(ref _lastSaveError, null);
            // Update changes the version and dirty flags while holding _gate. Compare and clear
            // under that same lock so an Update cannot set dirty between the comparison and clear.
            lock (_gate)
            {
                if (_changeVersion == snapshotVersion)
                {
                    Volatile.Write(ref _isDirty, false);
                    Volatile.Write(ref _hasUnpersistedUserChanges, false);
                }
            }

            return true;
        }
    }

    /// <summary>把一组设置作为一次提交写盘，只有写入成功后才替换当前配置。</summary>
    public bool UpdateAndSave(Action<AppConfig> change)
    {
        ArgumentNullException.ThrowIfNull(change);

        AppConfig? committed = null;
        lock (_saveGate)
        {
            if (string.IsNullOrWhiteSpace(FilePath))
            {
                RecordSaveFailure("没有配置文件路径。");
                return false;
            }

            if (!_loadedSuccessfully)
            {
                RecordSaveFailure(_loadError ?? "配置文件读取失败。为保护原文件，程序拒绝覆盖它。");
                return false;
            }

            string savedPath;
            string? error;
            lock (_gate)
            {
                var previousLayoutId = _config.ActiveLayoutId;
                var proposed = _config.Clone();
                change(proposed);
                if (string.Equals(previousLayoutId, proposed.ActiveLayoutId, StringComparison.Ordinal))
                {
                    proposed.SaveActiveFramesToLayout();
                }

                Normalize(proposed);
                if (!TrySaveSnapshot(proposed, _filePath, out savedPath, out error))
                {
                    RecordSaveFailure(error ?? "配置写入失败。");
                    return false;
                }

                _config = proposed;
                Volatile.Write(ref _isDirty, false);
                Volatile.Write(ref _hasUnpersistedUserChanges, false);
                Interlocked.Increment(ref _changeVersion);
                committed = _config.Clone();
            }

            Volatile.Write(ref _filePath, savedPath);
            Volatile.Write(ref _lastSaveError, null);
        }

        Changed?.Invoke(this, committed!);
        return true;
    }

    private bool TrySaveSnapshot(AppConfig snapshot, string preferredPath, out string savedPath, out string error)
    {
        var errors = new List<string>();
        var paths = new[] { preferredPath }
            .Concat(_fallbackFilePaths)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var path in paths)
        {
            try
            {
                SaveSnapshot(path, snapshot);
                savedPath = path;
                error = string.Empty;
                return true;
            }
            catch (IOException ex)
            {
                Trace.TraceError($"LyricCanvas 配置保存失败（{path}）：{ex}");
                errors.Add($"{path}：{ex.GetType().Name}: {ex.Message}");
            }
            catch (UnauthorizedAccessException ex)
            {
                Trace.TraceError($"LyricCanvas 配置保存失败（{path}）：{ex}");
                errors.Add($"{path}：{ex.GetType().Name}: {ex.Message}");
            }
            catch (System.Security.SecurityException ex)
            {
                Trace.TraceError($"LyricCanvas 配置保存失败（{path}）：{ex}");
                errors.Add($"{path}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        savedPath = string.Empty;
        error = string.Join(Environment.NewLine, errors);
        return false;
    }

    private static void SaveSnapshot(string filePath, AppConfig snapshot)
    {
        var directory = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // 唯一临时名避免不同保存入口争用同一个 .tmp 文件。
        var tempPath = $"{filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, snapshot, SerializerOptions);
                stream.Flush(flushToDisk: true);
            }

            // 原子替换：先完整写临时文件再替换，避免半截配置或旧快照覆盖新设置。
            File.Move(tempPath, filePath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(tempPath);
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

    private void RecordSaveFailure(string error)
    {
        var previous = Interlocked.Exchange(ref _lastSaveError, error);
        if (previous is null)
        {
            Interlocked.Increment(ref _saveFailureGeneration);
        }
    }

    private void ScheduleSave()
    {
        if (!_autoSave || _disposed)
        {
            return;
        }

        lock (_gate)
        {
            _saveTimer ??= new Timer(_ => SaveNow(), state: null, Timeout.Infinite, Timeout.Infinite);
            _saveTimer.Change(_saveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private AppConfig Load()
    {
        // 新版配置一旦存在就拥有优先权。旧 LyricDesktop 文件只用于首次迁移，
        // 不能因为它的时间戳更新而在之后启动时覆盖用户已经调整过的新版设置。
        var currentConfigPath = string.IsNullOrWhiteSpace(_filePath)
            ? null
            : new[] { _filePath }
                .Concat(_fallbackFilePaths)
                .Where(File.Exists)
                .OrderByDescending(GetLastWriteTimeUtcOrMinimum)
                .FirstOrDefault();
        var sourcePath = currentConfigPath ?? _legacyReadFilePaths
            .Where(File.Exists)
            .OrderByDescending(GetLastWriteTimeUtcOrMinimum)
            .FirstOrDefault();

        if (sourcePath is null)
        {
            _loadedSuccessfully = true;

            // 注意这里也要走一遍 Normalize：
            // "第一次运行、还没有配置文件"是最常见的路径，
            // 如果直接返回裸的 new AppConfig()，歌词框列表就是空的，
            // 程序启动后会一个框都不显示（这个坑是单元测试抓出来的）。
            var fresh = new AppConfig();
            Normalize(fresh);
            return fresh;
        }

        try
        {
            if (!string.Equals(sourcePath, _filePath, StringComparison.OrdinalIgnoreCase))
            {
                _migrationSaveNeeded = true;
            }

            var json = File.ReadAllText(sourcePath);
            var loaded = JsonSerializer.Deserialize<AppConfig>(json, SerializerOptions);
            if (loaded is null)
            {
                _loadedSuccessfully = false;
                _loadError = "配置文件没有有效内容。为保护原文件，程序拒绝覆盖它。";
                return CreateNormalizedDefaults();
            }

            _migrationSaveNeeded |= loaded.Version < 7
                || loaded.LayoutSchemes is null
                || loaded.LayoutSchemes.Count == 0
                || string.IsNullOrWhiteSpace(loaded.ActiveLayoutId);
            Normalize(loaded);
            _loadedSuccessfully = true;
            return loaded;
        }
        catch (JsonException ex)
        {
            // 配置文件坏了就当没有，用默认值启动。
            // 同时把"不许写盘"这个开关打开，保留原文件让用户自己看哪里写错了。
            _loadedSuccessfully = false;
            _loadError = $"配置文件不是有效 JSON：{ex.Message}。为保护原文件，程序拒绝覆盖它。";
            return CreateNormalizedDefaults();
        }
        catch (IOException ex)
        {
            _loadedSuccessfully = false;
            _loadError = $"读取配置文件失败：{ex.Message}";
            return CreateNormalizedDefaults();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException)
        {
            _loadedSuccessfully = false;
            _loadError = $"没有权限读取配置文件：{ex.Message}。为保护原文件，程序拒绝覆盖它。";
            return CreateNormalizedDefaults();
        }
    }

    private static AppConfig CreateNormalizedDefaults()
    {
        var defaults = new AppConfig();
        Normalize(defaults);
        return defaults;
    }

    /// <summary>把不合法的值拉回合理范围。手改配置文件写错了也不至于让界面崩掉。</summary>
    private static void Normalize(AppConfig config)
    {
        config.Frames ??= new FramesConfig();
        NormalizeLayouts(config);

        config.CustomText ??= string.Empty;
        config.LyricsDirectory ??= string.Empty;
        config.MediaAppAllowList ??= string.Empty;
        NormalizeSongLyricOffsets(config);

        // 热键主键必须有值，否则注册时会失败
        if (string.IsNullOrWhiteSpace(config.HotkeyKey))
        {
            config.HotkeyKey = "E";
        }

        var frames = config.Frames;

        if (!double.IsFinite(frames.Opacity))
        {
            frames.Opacity = 1.0;
        }

        frames.Opacity = Math.Clamp(frames.Opacity, 0.05, 1.0);

        if (!double.IsFinite(frames.FontSize))
        {
            frames.FontSize = 44;
        }

        frames.FontSize = Math.Clamp(frames.FontSize, 10, 200);

        if (string.IsNullOrWhiteSpace(frames.FontFamily))
        {
            frames.FontFamily = "Microsoft YaHei UI";
        }

        if (string.IsNullOrWhiteSpace(frames.TextColor))
        {
            frames.TextColor = "#FFFFD866";
        }

        frames.TextGradientColors ??= new List<string>();
        frames.TextGradientColors.RemoveAll(color => !IsHexColor(color));
        if (frames.TextGradientColors.Count > FramesConfig.MaxTextGradientColors)
        {
            frames.TextGradientColors.RemoveRange(FramesConfig.MaxTextGradientColors,
                frames.TextGradientColors.Count - FramesConfig.MaxTextGradientColors);
        }
        if (frames.PlayedTextColor is { } playedColor && !IsHexColor(playedColor))
        {
            frames.PlayedTextColor = null;
        }
        if (frames.UnplayedTextColor is { } unplayedColor && !IsHexColor(unplayedColor))
        {
            frames.UnplayedTextColor = null;
        }
        if (!Enum.IsDefined(frames.PlaybackEffect))
        {
            frames.PlaybackEffect = PlaybackEffectMode.Fade;
        }

        if (string.IsNullOrWhiteSpace(frames.GlowColor))
        {
            frames.GlowColor = "#FFFFB83D";
        }

        if (!double.IsFinite(frames.GlowRadius))
        {
            frames.GlowRadius = 16;
        }

        frames.GlowRadius = Math.Clamp(frames.GlowRadius, 0, 48);

        if (!double.IsFinite(frames.GlowOpacity))
        {
            frames.GlowOpacity = 0.85;
        }

        frames.GlowOpacity = Math.Clamp(frames.GlowOpacity, 0, 1);

        if (string.IsNullOrWhiteSpace(frames.SubTextColor))
        {
            frames.SubTextColor = "#B0FFFFFF";
        }

        if (!Enum.IsDefined(frames.LyricEffect))
        {
            frames.LyricEffect = LyricEffectMode.None;
        }

        if (!double.IsFinite(frames.EffectIntensity))
        {
            frames.EffectIntensity = 0.55;
        }

        frames.EffectIntensity = Math.Clamp(frames.EffectIntensity, 0.1, 1.0);

        if (!Enum.IsDefined(frames.MotionDirection))
        {
            frames.MotionDirection = LyricMotionDirection.Vertical;
        }

        // 旧版把“晃动/浮动”和方向分开存。现在方向由效果选项本身决定，
        // 用旧方向把原设置折叠到上下/左右模式；新界面会同步写这两个字段。
        if (frames.LyricEffect is LyricEffectMode.Shake or LyricEffectMode.Float)
        {
            frames.LyricEffect = frames.MotionDirection == LyricMotionDirection.Horizontal
                ? LyricEffectMode.Float
                : LyricEffectMode.Shake;
            frames.MotionDirection = frames.LyricEffect == LyricEffectMode.Float
                ? LyricMotionDirection.Horizontal
                : LyricMotionDirection.Vertical;
        }

        frames.MotionPeriodMs = Math.Clamp(frames.MotionPeriodMs, 200, 5000);

        if (!double.IsFinite(frames.ParticleSize))
        {
            frames.ParticleSize = 6;
        }

        frames.ParticleSize = Math.Clamp(frames.ParticleSize, 2, 18);
        frames.ParticleCount = Math.Clamp(frames.ParticleCount, 4, 32);

        if (!double.IsFinite(frames.ParticleSpread))
        {
            frames.ParticleSpread = 72;
        }

        frames.ParticleSpread = Math.Clamp(frames.ParticleSpread, 12, 220);
        frames.ParticleDurationMs = Math.Clamp(frames.ParticleDurationMs, 300, 5000);

        if (!double.IsFinite(frames.LetterSpacing))
        {
            frames.LetterSpacing = 0;
        }

        frames.LetterSpacing = Math.Clamp(frames.LetterSpacing, -5, 30);
        frames.FadeInMs = Math.Clamp(frames.FadeInMs, 0, 3000);
        frames.FadeOutMs = Math.Clamp(frames.FadeOutMs, 0, 3000);
        frames.HoldMs = Math.Clamp(frames.HoldMs, 0, 5000);
    }

    private static bool IsHexColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value[0] != '#')
        {
            return false;
        }

        if (value.Length is not (4 or 7 or 9))
        {
            return false;
        }

        foreach (var character in value.AsSpan(1))
        {
            if (!Uri.IsHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }

    private static void NormalizeLayouts(AppConfig config)
    {
        config.LayoutSchemes ??= new List<LayoutSchemeConfig>();
        config.LayoutSchemes.RemoveAll(layout => layout is null);

        if (config.LayoutSchemes.Count == 0)
        {
            NormalizeFrames(config.Frames);
            var initial = new LayoutSchemeConfig
            {
                Name = "默认方案",
                Frames = config.Frames.Frames.Select(frame => frame.Clone()).ToList(),
            };
            config.LayoutSchemes.Add(initial);
            config.ActiveLayoutId = initial.Id;
            config.Version = Math.Max(config.Version, 7);
            return;
        }

        if (config.LayoutSchemes.Count > 20)
        {
            config.LayoutSchemes.RemoveRange(20, config.LayoutSchemes.Count - 20);
        }

        var layoutIds = new HashSet<string>(StringComparer.Ordinal);
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < config.LayoutSchemes.Count; i++)
        {
            var layout = config.LayoutSchemes[i];
            if (string.IsNullOrWhiteSpace(layout.Id) || !layoutIds.Add(layout.Id))
            {
                layout.Id = Guid.NewGuid().ToString("N")[..8];
                layoutIds.Add(layout.Id);
            }

            layout.Name = string.IsNullOrWhiteSpace(layout.Name)
                ? $"布局方案 {i + 1}"
                : layout.Name.Trim();
            if (!usedNames.Add(layout.Name))
            {
                var baseName = layout.Name;
                var suffix = 2;
                while (!usedNames.Add($"{baseName} {suffix}"))
                {
                    suffix++;
                }

                layout.Name = $"{baseName} {suffix}";
            }

            var layoutFrames = new FramesConfig { Frames = layout.Frames ?? new List<FrameConfig>() };
            NormalizeFrames(layoutFrames);
            layout.Frames = layoutFrames.Frames;
        }

        if (!config.LayoutSchemes.Any(layout =>
                string.Equals(layout.Id, config.ActiveLayoutId, StringComparison.Ordinal)))
        {
            config.ActiveLayoutId = config.LayoutSchemes[0].Id;
        }

        var active = config.LayoutSchemes.First(layout =>
            string.Equals(layout.Id, config.ActiveLayoutId, StringComparison.Ordinal));
        config.Frames.Frames = active.Frames.Select(frame => frame.Clone()).ToList();
        NormalizeFrames(config.Frames);
        active.Frames = config.Frames.Frames.Select(frame => frame.Clone()).ToList();
        config.Version = Math.Max(config.Version, 7);
    }

    private static void NormalizeSongLyricOffsets(AppConfig config)
    {
        config.SongLyricOffsets ??= new List<SongLyricOffset>();

        if (config.SongLyricOffsets.Count > 1000)
        {
            config.SongLyricOffsets.RemoveRange(0, config.SongLyricOffsets.Count - 1000);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var i = config.SongLyricOffsets.Count - 1; i >= 0; i--)
        {
            var offset = config.SongLyricOffsets[i];
            if (offset is null)
            {
                config.SongLyricOffsets.RemoveAt(i);
                continue;
            }

            offset.Title ??= string.Empty;
            offset.Artist ??= string.Empty;
            if (SongLyricOffset.NormalizePart(offset.Title).Length == 0)
            {
                config.SongLyricOffsets.RemoveAt(i);
                continue;
            }

            var key = SongLyricOffset.CreateKey(offset.Title, offset.Artist);
            if (!seen.Add(key))
            {
                config.SongLyricOffsets.RemoveAt(i);
                continue;
            }

            offset.OffsetMs = Math.Clamp(offset.OffsetMs, -10_000, 10_000);
        }
    }

    /// <summary>
    /// 规整歌词框列表。
    ///
    /// 有两件事很关键：
    ///   1. **空列表要补上默认框**。用户第一次运行、或者手改配置把 frames 删空了，
    ///      都得有两个框可用，否则程序看起来"什么都没显示"。
    ///   2. **数量要限上限**。每个框是一个独立的透明窗口，
    ///      配置文件里写个 1000 会直接把机器拖死。
    /// </summary>
    private static void NormalizeFrames(FramesConfig frames)
    {
        frames.Frames ??= new List<FrameConfig>();

        // 去掉空条目：手写 JSON 可能写出 null
        frames.Frames.RemoveAll(f => f is null);

        if (frames.Frames.Count == 0)
        {
            frames.Frames.AddRange(FramesConfig.CreateDefaultFrames(1920, 1080));
        }

        if (frames.Frames.Count > FramesConfig.MaxFrameCount)
        {
            frames.Frames.RemoveRange(
                FramesConfig.MaxFrameCount,
                frames.Frames.Count - FramesConfig.MaxFrameCount);
        }

        var frameIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var frame in frames.Frames)
        {
            if (string.IsNullOrWhiteSpace(frame.Id) || !frameIds.Add(frame.Id))
            {
                frame.Id = Guid.NewGuid().ToString("N")[..8];
                frameIds.Add(frame.Id);
            }

            if (!double.IsFinite(frame.Left))
            {
                frame.Left = 0;
            }

            if (!double.IsFinite(frame.Top))
            {
                frame.Top = 0;
            }

            if (!double.IsFinite(frame.Width))
            {
                frame.Width = 600;
            }

            if (!double.IsFinite(frame.Height))
            {
                frame.Height = 80;
            }

            frame.Width = Math.Clamp(frame.Width, 60, 4000);
            frame.Height = Math.Clamp(frame.Height, 24, 1000);

            if (!double.IsFinite(frame.RotationDegrees))
            {
                frame.RotationDegrees = 0;
            }

            frame.RotationDegrees = Math.Clamp(frame.RotationDegrees, -180, 180);

            // 位置允许为负（多显示器时左侧屏幕就在负坐标），但不允许离谱到找不回来
            frame.Left = Math.Clamp(frame.Left, -10000, 10000);
            frame.Top = Math.Clamp(frame.Top, -10000, 10000);

            if (frame.FontSize is { } size)
            {
                frame.FontSize = double.IsFinite(size) ? Math.Clamp(size, 10, 200) : null;
            }

            if (frame.Opacity is { } opacity)
            {
                frame.Opacity = double.IsFinite(opacity) ? Math.Clamp(opacity, 0.05, 1.0) : null;
            }
        }

    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            _saveTimer?.Dispose();
            _saveTimer = null;
        }

        // 退出前把待保存的改动落盘
        SaveNow();
    }
}
