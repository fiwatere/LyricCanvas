using System.Windows;
using System.IO;
using System.Windows.Threading;
using LyricCanvas.Core;
using LyricCanvas.Media;
using LyricCanvas.Media.Lyrics;

// 项目同时开了 UseWPF 和 UseWindowsForms，两边的隐式 using 会撞名，明确指定 WPF 版本
using Application = System.Windows.Application;

namespace LyricCanvas.App;

/// <summary>
/// 多窗口的中枢：管着所有歌词框，并且是**歌词状态的唯一真相源**。
///
/// 为什么必须集中管理：如果每个框各自去问播放器"现在第几句"，
/// 快速切歌或拖动进度条时各框算出来的结果会不一致——A 框显示第 3 句、
/// B 框还停在第 2 句。所以这里统一算一次，再推给所有框。
///
/// 职责划分：
///   WindowManager  读播放状态、解析歌词、算当前句、决定哪句交给哪个框
///   OverlayWindow  只管把文字淡入淡出画出来
/// </summary>
public sealed class WindowManager : IDisposable
{
    /// <summary>
    /// 界面节拍。100 毫秒足够跟上歌词切换（换行本身是几百毫秒到几秒一次），
    /// 而且比 200 毫秒更能抓住"这句结束了该淡出"的时刻。
    /// 淡入淡出本身由 WPF 动画驱动，不受这个节拍限制。
    /// </summary>
    private static readonly TimeSpan TickInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan AudioLyricCorrectionDeadband = TimeSpan.FromMilliseconds(650);
    private const double AudioLyricCorrectionSmoothing = 0.35;
    private const double MaximumAudioLyricCorrectionStepMs = 400;

    private readonly ConfigService _config;
    private readonly MediaSessionService _media;
    private readonly AudioLyricSynchronizer _audioLyricSynchronizer = new();
    private readonly AudioLyricSyncLifecycle _audioLyricSyncLifecycle = new();
    private readonly LyricsResolver _lyricsResolver;
    private readonly DispatcherTimer _timer;

    private readonly Dictionary<string, OverlayWindow> _windows = new(StringComparer.Ordinal);
    private readonly List<FrameConfig> _frameSnapshot = new();

    /// <summary>
    /// 上一次应用过的**全局样式**快照。
    /// 用来发现"共享样式变了"，比如设置界面里拖字号或透明度滑块。
    /// </summary>
    private FramesConfig? _styleSnapshot;

    /// <summary>未提交的歌词显示预览；仅覆盖显示字段，不进入配置服务或磁盘。</summary>
    private AppConfig? _lyricsPreview;

    private NowPlaying _nowPlaying = NowPlaying.Empty;

    private CancellationTokenSource? _audioLyricSyncCancellation;
    private string _audioLyricSyncKey = string.Empty;
    private AudioLyricSyncRun? _audioLyricSyncRun;
    private string? _audioLyricSyncStatusOverride;
    private TimeSpan _audioLyricOffset;
    private bool _hasAudioLyricCalibration;

    // Player timeline tracking: pause/resume is a state transition, while a large
    // position discontinuity means the user sought or the media session reset.
    private string _playbackTrackKey = string.Empty;
    private TimeSpan _lastPlayerPosition;
    private DateTimeOffset _lastPlayerPositionAtUtc;
    private bool _lastPlayerWasPlaying;
    private bool _hasPlaybackSnapshot;
    private bool _forceNextLyricsUpdate;

    private sealed class FramePresentationState
    {
        public int? VisibleLineIndex { get; set; }
        public int? RequestedLineIndex { get; set; }
        public TimeSpan? FadeInCompletedAtPosition { get; set; }
        public int Generation { get; set; }
        public int? SuppressedLineIndex { get; set; }
    }

    private readonly Dictionary<string, FramePresentationState> _framePresentations = new(StringComparer.Ordinal);

    // 当前这首歌的歌词状态
    private string _lyricsKey = string.Empty;
    private LyricsStatus _lyricsStatus = LyricsStatus.Idle;
    private IReadOnlyList<LyricLineTiming> _timings = Array.Empty<LyricLineTiming>();
    private CancellationTokenSource? _lyricsFetch;
    private LyricsResolution? _currentLyricsResolution;
    private readonly HashSet<string> _rejectedCandidateKeys = new(StringComparer.Ordinal);
    private string _rejectedCandidateTrackKey = string.Empty;

    private bool _disposed;

    private enum LyricsStatus
    {
        Idle,
        Searching,
        Done,
    }

    public WindowManager(ConfigService config)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _media = new MediaSessionService(() => _config.Current.MediaAppAllowList);
        _lyricsResolver = new LyricsResolver(BuildLyricsOptions);

        _media.Changed += OnMediaChanged;
        _audioLyricSynchronizer.StatusChanged += OnAudioLyricSyncStatusChanged;
        _audioLyricSynchronizer.RunStarted += OnAudioLyricSyncRunStarted;
        _audioLyricSynchronizer.AlignmentFound += OnAudioLyricAlignmentFound;

        _timer = new DispatcherTimer(DispatcherPriority.Normal, System.Windows.Threading.Dispatcher.CurrentDispatcher)
        {
            Interval = TickInterval,
        };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>布局发生变化（框被增删或移动）时触发，界面可以据此刷新。</summary>
    public event EventHandler? LayoutInvalidated;

    /// <summary>本地实时识别校准的状态，供设置页面显示。</summary>
    public event Action<string>? AudioLyricSyncStatusChanged;

    /// <summary>当前播放器歌曲或播放状态变化，供逐首歌词偏移设置刷新。</summary>
    public event Action<NowPlaying>? CurrentTrackChanged;

    /// <summary>当前歌曲歌词来源或匹配确认状态变化。</summary>
    public event Action<LyricsResolution?>? CurrentLyricsResolutionChanged;

    public string AudioLyricSyncStatus => _audioLyricSyncStatusOverride ?? _audioLyricSynchronizer.Status;

    public NowPlaying CurrentNowPlaying => ResolveNowPlaying();

    public LyricsResolution? CurrentLyricsResolution => _currentLyricsResolution;

    public MediaSourceDiagnostics MediaDiagnostics => _media.Diagnostics;

    public int CurrentSongLyricOffsetMs
    {
        get
        {
            var track = ResolveNowPlaying();
            return track.HasTrack ? _config.Current.GetSongLyricOffsetMs(track.Title, track.Artist) : 0;
        }
    }

    public bool AdjustCurrentSongLyricOffset(int deltaMs)
    {
        var track = ResolveNowPlaying();
        if (!track.HasTrack)
        {
            return false;
        }

        var currentOffset = _config.Current.GetSongLyricOffsetMs(track.Title, track.Artist);
        var updatedOffset = Math.Clamp(currentOffset + deltaMs, -10_000, 10_000);
        if (updatedOffset == currentOffset)
        {
            return true;
        }

        if (!_config.UpdateAndSave(config => config.SetSongLyricOffsetMs(track.Title, track.Artist, updatedOffset)))
        {
            return false;
        }

        Tick();
        return true;
    }

    public bool ClearCurrentSongLyricOffset()
    {
        var track = ResolveNowPlaying();
        if (!track.HasTrack)
        {
            return false;
        }

        if (CurrentSongLyricOffsetMs == 0)
        {
            return true;
        }

        if (!_config.UpdateAndSave(config => config.SetSongLyricOffsetMs(track.Title, track.Artist, 0)))
        {
            return false;
        }

        Tick();
        return true;
    }

    /// <summary>当前所有框窗口，供布局编辑器显示参考框。</summary>
    public IReadOnlyDictionary<string, OverlayWindow> Windows => _windows;

    /// <summary>应用设置页草稿作为即时预览；传 null 时恢复持久化配置。</summary>
    public void PreviewLyricsSettings(AppConfig? draft)
    {
        _lyricsPreview = draft?.Clone();
        Tick();
    }

    /// <summary>启动：读配置建窗口、开始监听播放。</summary>
    public void Start()
    {
        ClampFramesOnScreen();
        SyncFrames();
        _ = _media.StartAsync();
        _timer.Start();
        Tick();
    }

    /// <summary>
    /// 把落在屏幕外的框拉回可见区域。
    ///
    /// 为什么需要：配置文件可以手改，多显示器拔掉之后屏幕坐标也会变。
    /// 框一旦被摆到完全看不见的地方，用户就再也找不回来——歌词窗口不接受
    /// 鼠标拖动，没有"拖回来"这条路。
    ///
    /// 这里必须用**实际虚拟屏幕**范围来判断，所以放在 App 层而不是 Core：
    /// Core 拿不到屏幕信息，只能做很粗的数值钳制（±10000 那种），
    /// 挡不住"数值合法但完全在屏幕外"这种情况——这个漏洞是验证脚本抓出来的。
    /// </summary>
    private void ClampFramesOnScreen()
    {
        var screenLeft = SystemParameters.VirtualScreenLeft;
        var screenTop = SystemParameters.VirtualScreenTop;
        var screenRight = screenLeft + SystemParameters.VirtualScreenWidth;
        var screenBottom = screenTop + SystemParameters.VirtualScreenHeight;

        // 至少留这么多像素在可见区域内，用户才找得到这个框
        const double margin = 60;

        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                var bounds = OverlayWindow.CalculateSurfaceBounds(
                    frame.Left,
                    frame.Top,
                    frame.Width,
                    frame.Height,
                    frame.RotationDegrees,
                    c.Frames.GlowRadius);
                var minLeft = screenLeft - bounds.Width + margin;
                var maxLeft = screenRight - margin;
                var minTop = screenTop - bounds.Height + margin;
                var maxTop = screenBottom - margin;

                var surfaceLeft = Math.Clamp(bounds.Left, Math.Min(minLeft, maxLeft), Math.Max(minLeft, maxLeft));
                var surfaceTop = Math.Clamp(bounds.Top, Math.Min(minTop, maxTop), Math.Max(minTop, maxTop));
                var left = frame.Left + surfaceLeft - bounds.Left;
                var top = frame.Top + surfaceTop - bounds.Top;

                if (Math.Abs(left - frame.Left) > 0.5 || Math.Abs(top - frame.Top) > 0.5)
                {
                    frame.Left = left;
                    frame.Top = top;
                }
            }
        });
    }

    // ---------------------------------------------------------------
    // 框的创建与销毁
    // ---------------------------------------------------------------

    /// <summary>
    /// 按配置把窗口同步到应有的状态：缺的建、多的关、有的更新位置样式。
    ///
    /// 这个方法要能反复调用（用户增删框、改位置、改样式都会触发），
    /// 所以必须是幂等的——不能每次调用都把窗口重建一遍，
    /// 那会让正在播放的歌词不停闪烁。
    /// </summary>
    public void SyncFrames()
    {
        var config = GetEffectiveConfig();
        var frames = config.Frames.Frames;
        var enabled = frames.Where(f => f.Enabled).ToList();

        // 1) 关掉已经不需要的窗口（停用的、或配置里已删除的）
        var keepIds = enabled.Select(f => f.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var id in _windows.Keys.ToList())
        {
            if (!keepIds.Contains(id))
            {
                _windows[id].Close();
                _windows.Remove(id);
                _framePresentations.Remove(id);
            }
        }

        // 2) 建立缺的窗口
        foreach (var frame in enabled)
        {
            if (_windows.ContainsKey(frame.Id))
            {
                continue;
            }

            var window = new OverlayWindow(frame.Id);

            // 关键顺序：**先按配置摆好位置尺寸、再 Show()**。
            //
            // AllowsTransparency=true 的窗口是分层窗口，它的表面在第一次显示时
            // 按当时的尺寸创建。如果先 Show() 再改尺寸，那个分层表面可能不会跟着
            // 重建，结果就是窗口"存在但看不到"——查了很久的那个"拖不动"
            // 其实根因在这里（实测：屏幕上那个位置采不到窗口的任何像素）。
            window.ApplyFrame(frame, config.Frames);

            _windows[frame.Id] = window;
            window.Show();
        }

        // 3) 更新已有窗口的位置与样式
        foreach (var frame in enabled)
        {
            if (_windows.TryGetValue(frame.Id, out var window))
            {
                window.ApplyFrame(frame, config.Frames);
            }
        }

        // 记一份快照，后面每帧比对用（避免每 100 毫秒都去读配置对象）
        _frameSnapshot.Clear();
        _frameSnapshot.AddRange(enabled.Select(f => f.Clone()));
        _styleSnapshot = config.Frames.Clone();

        LayoutInvalidated?.Invoke(this, EventArgs.Empty);
    }


    /// <summary>
    /// 把所有框的位置立即写回配置。
    ///
    /// 位置只有一个来源——**全屏布局编辑器**。歌词窗口自己不接受鼠标拖动，
    /// 所以这里读回来的值就是编辑器（或配置文件）设定的值，
    /// 用途是退出前把最终状态固化下来。
    /// </summary>
    public void PersistLayout()
    {
        var frames = _config.Current.Frames.Frames;
        var hasChanges = frames.Any(frame =>
            _windows.TryGetValue(frame.Id, out var window)
            && (Math.Abs(frame.Left - window.FrameLeft) > 0.1
                || Math.Abs(frame.Top - window.FrameTop) > 0.1
                || Math.Abs(frame.Width - window.FrameWidth) > 0.1
                || Math.Abs(frame.Height - window.FrameHeight) > 0.1));
        if (!hasChanges)
        {
            return;
        }

        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                if (_windows.TryGetValue(frame.Id, out var window))
                {
                    frame.Left = window.FrameLeft;
                    frame.Top = window.FrameTop;
                    frame.Width = window.FrameWidth;
                    frame.Height = window.FrameHeight;
                }
            }
        });
    }

    // ---------------------------------------------------------------
    // 歌词来源
    // ---------------------------------------------------------------

    /// <summary>当前歌词目录：用户指定的，或程序目录下的 lyrics。</summary>
    public string GetLyricsDirectory()
    {
        return LyricsDirectoryPolicy.GetSearchDirectories(
            _config.Current.LyricsDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory)[0];
    }

    private LyricsOptions BuildLyricsOptions()
    {
        var config = _config.Current;
        var searchDirectories = LyricsDirectoryPolicy.GetSearchDirectories(
            config.LyricsDirectory,
            AppContext.BaseDirectory,
            Environment.CurrentDirectory);

        return new LyricsOptions
        {
            LyricsDirectory = searchDirectories[0],
            SearchDirectories = searchDirectories,
            FetchFromNetwork = config.FetchLyricsFromNetwork,
            UseCache = true,
            Sources = config.FetchLyricsFromNetwork
                ? new ILyricsSource[] { new KugouLyricsSource(), new LrclibLyricsSource() }
                : Array.Empty<ILyricsSource>(),
        };
    }


    // ---------------------------------------------------------------
    // 播放信息
    // ---------------------------------------------------------------

    private void OnMediaChanged(object? sender, NowPlaying nowPlaying)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _nowPlaying = _media.Current;
            CurrentTrackChanged?.Invoke(_nowPlaying);
            Tick();
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            _nowPlaying = _media.Current;
            CurrentTrackChanged?.Invoke(_nowPlaying);
            Tick();
        });
    }

    private NowPlaying ResolveNowPlaying()
    {
        var debug = DebugState.TryLoad();
        if (debug is null)
        {
            return _media.Current;
        }

        return new NowPlaying(
            AppId: "debug",
            Title: debug.Title,
            Artist: debug.Artist,
            AlbumTitle: string.Empty,
            Playback: debug.IsPlaying ? PlaybackState.Playing : PlaybackState.Paused,
            Position: TimeSpan.FromSeconds(debug.PositionSeconds),
            ReadAtUtc: DateTimeOffset.UtcNow,
            Duration: TimeSpan.FromSeconds(debug.DurationSeconds));
    }

    // ---------------------------------------------------------------
    // 主循环
    // ---------------------------------------------------------------

    private void Tick()
    {
        if (_disposed)
        {
            return;
        }

        var config = GetEffectiveConfig();

        // 配置里的框变了（增删、改位置、改样式）就同步一次窗口
        if (FrameLayoutChanged(config))
        {
            SyncFrames();
        }

        // ---- 自定义文本模式：所有框都显示同一段文字，不淡出 ----
        if (config.ContentMode == ContentMode.CustomText)
        {
            CancelLyricsFetch();
            ResetLyricsState();

            var text = string.IsNullOrWhiteSpace(config.CustomText)
                ? AppConfig.DefaultCustomText
                : config.CustomText;

            foreach (var (id, window) in _windows)
            {
                var frame = _frameSnapshot.FirstOrDefault(f => f.Id == id);
                window.SetTrackText(string.Empty);
                if (frame is not null)
                {
                    window.UpdatePlaybackProgress(null, TimeSpan.Zero, config.Frames, frame);
                }

                // 自定义文本是常驻内容，不做"说完消失"，直接淡入后停住
                window.ShowLine(text, fadeInMs: frame is null ? 200 : config.Frames.FadeInMs);
            }

            return;
        }

        // ---- 歌词模式 ----
        var nowPlaying = ResolveNowPlaying();
        _nowPlaying = nowPlaying;

        if (!nowPlaying.HasTrack)
        {
            CancelLyricsFetch();
            ResetLyricsState();

            foreach (var window in _windows.Values)
            {
                window.SetTrackText(string.Empty);
                var frame = config.Frames.Frames.FirstOrDefault(candidate => candidate.Id == window.FrameId);
                if (frame is not null)
                {
                    window.UpdatePlaybackProgress(null, TimeSpan.Zero, config.Frames, frame);
                }
                window.FadeOut(config.Frames.PlaybackEffect == PlaybackEffectMode.Fade
                    ? config.Frames.FadeOutMs
                    : 0);
            }

            return;
        }

        var key = nowPlaying.Title + "\u0000" + nowPlaying.Artist;
        var nowUtc = DateTimeOffset.UtcNow;
        var trackChanged = ObservePlayback(nowPlaying, nowUtc, out var positionWasSought, out var becamePaused);
        if (trackChanged || positionWasSought || becamePaused)
        {
            _forceNextLyricsUpdate = true;
            StopAudioLyricSync(clearPosition: trackChanged);
        }

        if (!string.Equals(_lyricsKey, key, StringComparison.Ordinal))
        {
            ClearFramePresentationState();
            _rejectedCandidateKeys.Clear();
            _rejectedCandidateTrackKey = key;
            SetCurrentLyricsResolution(null);
            _forceNextLyricsUpdate = true;
            _lyricsKey = key;
            _timings = Array.Empty<LyricLineTiming>();
            _lyricsStatus = LyricsStatus.Searching;
            BeginLyricsFetch(nowPlaying);
        }

        // 歌词还没就绪：所有框显示一句状态提示，避免看起来像坏了
        if (_lyricsStatus != LyricsStatus.Done)
        {
            EnsureAudioLyricSync(config);
            var hint = _lyricsStatus == LyricsStatus.Searching
                ? "正在查找歌词…"
                : $"{nowPlaying.Title}（未找到歌词）";

            foreach (var window in _windows.Values)
            {
                window.SetTrackText(NowPlayingDisplay.GetSubText(nowPlaying));
                var frame = config.Frames.Frames.FirstOrDefault(candidate => candidate.Id == window.FrameId);
                if (frame is not null)
                {
                    window.UpdatePlaybackProgress(null, TimeSpan.Zero, config.Frames, frame);
                }
                window.ShowLine(hint, config.Frames.PlaybackEffect == PlaybackEffectMode.Fade
                    ? config.Frames.FadeInMs
                    : 0);
            }

            return;
        }

        EnsureAudioLyricSync(config);
        var position = GetCurrentLyricPosition(nowPlaying, nowUtc);
        position += TimeSpan.FromMilliseconds(
            _config.Current.GetSongLyricOffsetMs(nowPlaying.Title, nowPlaying.Artist));
        var forceUpdate = _forceNextLyricsUpdate;
        UpdateLyricsDisplay(
            nowPlaying,
            position,
            config.Frames,
            forceUpdate,
            becamePaused);
        _forceNextLyricsUpdate = false;
    }

    /// <summary>按当前歌词目录和联网设置重新查找正在播放的歌曲。</summary>
    public bool RefreshCurrentLyrics(bool forceSearch = false)
        => RefreshCurrentLyrics(forceSearch, preserveRejectedCandidates: false);

    private bool RefreshCurrentLyrics(bool forceSearch, bool preserveRejectedCandidates)
    {
        var track = ResolveNowPlaying();
        if (_disposed || !track.HasTrack)
        {
            return false;
        }

        void Refresh()
        {
            if (_disposed)
            {
                return;
            }

            var currentTrack = ResolveNowPlaying();
            if (!currentTrack.HasTrack)
            {
                return;
            }

            var currentKey = currentTrack.Title + "\u0000" + currentTrack.Artist;
            if (!string.Equals(_rejectedCandidateTrackKey, currentKey, StringComparison.Ordinal))
            {
                _rejectedCandidateKeys.Clear();
                _rejectedCandidateTrackKey = currentKey;
            }
            else if (forceSearch && !preserveRejectedCandidates)
            {
                _rejectedCandidateKeys.Clear();
            }

            StopAudioLyricSync(clearPosition: true);
            CancelLyricsFetch();
            ClearFramePresentationState();
            _lyricsKey = currentKey;
            _timings = Array.Empty<LyricLineTiming>();
            _lyricsStatus = LyricsStatus.Searching;
            _forceNextLyricsUpdate = true;
            SetCurrentLyricsResolution(null);
            BeginLyricsFetch(currentTrack, skipCache: forceSearch);
            Tick();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Refresh();
        }
        else
        {
            dispatcher.BeginInvoke(Refresh);
        }

        return true;
    }

    /// <summary>确认当前低置信匹配，并把确认状态写回缓存。</summary>
    public bool ConfirmCurrentLyricsMatch()
    {
        var resolution = _currentLyricsResolution;
        var track = ResolveNowPlaying();
        if (_disposed
            || resolution is null
            || !resolution.NeedsConfirmation
            || !track.HasTrack
            || !string.Equals(track.Title + "\u0000" + track.Artist, _lyricsKey, StringComparison.Ordinal))
        {
            return false;
        }

        var persisted = new LyricsCache(GetLyricsDirectory()).MarkConfirmed(track.Title, track.Artist);
        SetCurrentLyricsResolution(resolution with
        {
            NeedsConfirmation = false,
            IsConfirmed = true,
            Message = persisted ? string.Empty : "本次运行中已确认；缓存未能写入确认状态。",
        });
        return true;
    }

    /// <summary>拒绝当前低置信候选，删除它的缓存并尝试下一匹配。</summary>
    public bool RejectCurrentLyricsMatch()
    {
        var resolution = _currentLyricsResolution;
        var track = ResolveNowPlaying();
        var trackKey = track.Title + "\u0000" + track.Artist;
        if (_disposed
            || resolution is null
            || !resolution.NeedsConfirmation
            || string.IsNullOrWhiteSpace(resolution.MatchedCandidateKey)
            || !track.HasTrack
            || !string.Equals(trackKey, _lyricsKey, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(_rejectedCandidateTrackKey, trackKey, StringComparison.Ordinal))
        {
            _rejectedCandidateKeys.Clear();
            _rejectedCandidateTrackKey = trackKey;
        }

        _rejectedCandidateKeys.Add(resolution.MatchedCandidateKey);
        _ = new LyricsCache(GetLyricsDirectory()).Delete(track.Title, track.Artist);
        return RefreshCurrentLyrics(forceSearch: true, preserveRejectedCandidates: true);
    }

    private void SetCurrentLyricsResolution(LyricsResolution? resolution)
    {
        _currentLyricsResolution = resolution;
        CurrentLyricsResolutionChanged?.Invoke(resolution);
    }

    private bool ObservePlayback(
        NowPlaying nowPlaying,
        DateTimeOffset nowUtc,
        out bool positionWasSought,
        out bool becamePaused)
    {
        var trackKey = nowPlaying.AppId + "\u0000" + nowPlaying.Title + "\u0000" + nowPlaying.Artist;
        var position = nowPlaying.GetEstimatedPosition(nowUtc);
        var trackChanged = !_hasPlaybackSnapshot
            || !string.Equals(_playbackTrackKey, trackKey, StringComparison.Ordinal);

        positionWasSought = false;
        becamePaused = false;

        if (!trackChanged)
        {
            var elapsed = nowUtc - _lastPlayerPositionAtUtc;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }

            var expectedPosition = _lastPlayerWasPlaying
                ? _lastPlayerPosition + elapsed
                : _lastPlayerPosition;
            positionWasSought = Math.Abs((position - expectedPosition).TotalMilliseconds) > 700;
            becamePaused = _lastPlayerWasPlaying && !nowPlaying.IsPlaying;
        }

        _playbackTrackKey = trackKey;
        _lastPlayerPosition = position;
        _lastPlayerPositionAtUtc = nowUtc;
        _lastPlayerWasPlaying = nowPlaying.IsPlaying;
        _hasPlaybackSnapshot = true;
        return trackChanged;
    }

    private void EnsureAudioLyricSync(AppConfig config)
    {
        if (!config.AudioLyricSyncEnabled || _lyricsStatus != LyricsStatus.Done || _timings.Count < 2)
        {
            if (_audioLyricSyncCancellation is not null)
            {
                StopAudioLyricSync(clearPosition: true);
            }

            return;
        }

        if (!_nowPlaying.IsPlaying)
        {
            if (_audioLyricSyncCancellation is not null)
            {
                StopAudioLyricSync(clearPosition: false);
            }

            return;
        }

        if (_audioLyricSyncCancellation is not null
            && string.Equals(_audioLyricSyncKey, _lyricsKey, StringComparison.Ordinal)
            && _audioLyricSyncRun is { } currentRun
            && _audioLyricSyncLifecycle.IsCurrent(currentRun))
        {
            return;
        }

        if (_audioLyricSyncCancellation is not null)
        {
            StopAudioLyricSync(clearPosition: false);
        }

        var trackKey = _lyricsKey;
        var run = _audioLyricSyncLifecycle.TryStart(trackKey, _nowPlaying.IsPlaying, DateTimeOffset.UtcNow);
        if (run is null)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _audioLyricSyncStatusOverride = null;
        _audioLyricSyncCancellation = cancellation;
        _audioLyricSyncKey = trackKey;
        _audioLyricSyncRun = run;
        var cancellationToken = cancellation.Token;

        var modelPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LyricCanvas",
            "models",
            "ggml-tiny-q5_1.bin");

        var task = Task.Run(
            () => _audioLyricSynchronizer.RunAsync(
                trackKey,
                run.Value.Generation,
                _timings,
                modelPath,
                () => _nowPlaying.IsPlaying,
                cancellationToken));
        _ = ObserveAudioLyricSyncTaskAsync(task, run.Value, cancellation);
    }

    private void StopAudioLyricSync(bool clearPosition)
    {
        _audioLyricSyncLifecycle.Invalidate();
        var cancellation = _audioLyricSyncCancellation;
        _audioLyricSyncCancellation = null;
        _audioLyricSyncKey = string.Empty;
        _audioLyricSyncRun = null;
        if (cancellation is not null)
        {
            try
            {
                cancellation.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        if (clearPosition)
        {
            _audioLyricOffset = TimeSpan.Zero;
            _hasAudioLyricCalibration = false;
        }
    }

    private TimeSpan GetCurrentLyricPosition(NowPlaying nowPlaying, DateTimeOffset nowUtc)
    {
        // The media session remains the master clock so pause and seek changes are
        // always reflected. Audio recognition contributes only a relative correction.
        return nowPlaying.GetEstimatedPosition(nowUtc) + _audioLyricOffset;
    }

    private void OnAudioLyricSyncStatusChanged(string status)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            _audioLyricSyncStatusOverride = null;
            AudioLyricSyncStatusChanged?.Invoke(status);
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            _audioLyricSyncStatusOverride = null;
            AudioLyricSyncStatusChanged?.Invoke(status);
        });
    }

    private void OnAudioLyricSyncRunStarted(long runGeneration)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            if (_audioLyricSyncRun is { } run
                && run.Generation == runGeneration
                && _audioLyricSyncLifecycle.MarkStarted(run))
            {
                AudioLyricSyncStatusChanged?.Invoke(_audioLyricSynchronizer.Status);
            }
        });
    }

    private async Task ObserveAudioLyricSyncTaskAsync(
        Task task,
        AudioLyricSyncRun run,
        CancellationTokenSource cancellation)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // RunAsync reports its own errors through StatusChanged; this observer owns cleanup/retry.
        }

        void Complete()
        {
            var retryDelay = _audioLyricSyncLifecycle.Complete(
                run,
                cancellation.IsCancellationRequested,
                DateTimeOffset.UtcNow);

            if (ReferenceEquals(_audioLyricSyncCancellation, cancellation))
            {
                _audioLyricSyncCancellation = null;
                _audioLyricSyncKey = string.Empty;
                _audioLyricSyncRun = null;
            }

            cancellation.Dispose();

            if (retryDelay is { } delay
                && delay > TimeSpan.Zero
                && !_disposed
                && _config.Current.AudioLyricSyncEnabled)
            {
                var message = $"{_audioLyricSynchronizer.Status} 将在 {Math.Ceiling(delay.TotalSeconds):0} 秒后自动重试。";
                AudioLyricSyncStatusChanged?.Invoke(message);
            }
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            Complete();
        }
        else
        {
            _ = dispatcher.BeginInvoke(Complete);
        }
    }

    private void OnAudioLyricAlignmentFound(AudioLyricAlignment alignment)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            return;
        }

        dispatcher.BeginInvoke(() =>
        {
            var run = new AudioLyricSyncRun(alignment.RunGeneration, alignment.TrackKey);
            if (_disposed
                || !_audioLyricSyncLifecycle.IsCurrent(run)
                || !_nowPlaying.IsPlaying
                || !string.Equals(_lyricsKey, alignment.TrackKey, StringComparison.Ordinal)
                || !string.Equals(_audioLyricSyncKey, alignment.TrackKey, StringComparison.Ordinal))
            {
                return;
            }

            var nowUtc = DateTimeOffset.UtcNow;
            var current = ResolveNowPlaying();
            if (!current.HasTrack || !current.IsPlaying)
            {
                return;
            }

            var elapsedSinceCapture = AudioLyricSyncLifecycle.GetElapsedSince(alignment.CapturedAtTimestamp);
            var compensatedPosition = AudioLyricSyncLifecycle.CompensateRecognitionDelay(
                alignment.Position,
                elapsedSinceCapture,
                current.IsPlaying);
            var playerPosition = current.GetEstimatedPosition(nowUtc);
            var suggestedOffset = compensatedPosition - playerPosition;
            var offsetDifference = suggestedOffset - _audioLyricOffset;
            var correctionApplied = TryUpdateAudioLyricOffset(
                suggestedOffset,
                out var appliedStep);
            var displayedPosition = playerPosition + _audioLyricOffset;
            if (correctionApplied)
            {
                // Let the normal lyric state comparison handle a real line change.
                // Forcing a refresh here resets frame presentation state on every
                // calibration pass and can replay a fade for an otherwise stable line.
                Tick();
            }

            var appliedStatus = correctionApplied
                ? $"已平滑应用歌词校正：时间轴约 {FormatLyricPosition(displayedPosition)}；本次调整 {appliedStep.TotalMilliseconds:+0;-0;0} 毫秒。"
                : $"本轮校准与当前偏移仅差 {offsetDifference.TotalMilliseconds:+0;-0;0} 毫秒，低于 650 毫秒抖动阈值，保持当前歌词位置。";
            _audioLyricSyncStatusOverride = appliedStatus;
            AudioLyricSyncStatusChanged?.Invoke(appliedStatus);
        });
    }

    private bool TryUpdateAudioLyricOffset(TimeSpan suggestedOffset, out TimeSpan appliedStep)
    {
        if (!_hasAudioLyricCalibration)
        {
            appliedStep = suggestedOffset - _audioLyricOffset;
            _audioLyricOffset = suggestedOffset;
            _hasAudioLyricCalibration = true;
            return true;
        }

        var difference = suggestedOffset - _audioLyricOffset;
        if (difference.Duration() <= AudioLyricCorrectionDeadband)
        {
            appliedStep = TimeSpan.Zero;
            return false;
        }

        var smoothedStepMs = difference.TotalMilliseconds * AudioLyricCorrectionSmoothing;
        var boundedStepMs = Math.Clamp(
            smoothedStepMs,
            -MaximumAudioLyricCorrectionStepMs,
            MaximumAudioLyricCorrectionStepMs);
        appliedStep = TimeSpan.FromMilliseconds(boundedStepMs);
        _audioLyricOffset += appliedStep;
        return true;
    }

    private static string FormatLyricPosition(TimeSpan position)
    {
        var safePosition = position < TimeSpan.Zero ? TimeSpan.Zero : position;
        return $"{(int)safePosition.TotalMinutes:00}:{safePosition.Seconds:00}";
    }

    private AppConfig GetEffectiveConfig()
    {
        var config = _config.Current;
        if (_lyricsPreview is not { } preview)
        {
            return config;
        }

        // 位置、尺寸、旋转角度和单框覆盖值始终取真实配置；设置草稿只预览全局样式与内容。
        config.Frames.CopyGlobalStyleFrom(preview.Frames);
        config.ContentMode = preview.ContentMode;
        config.CustomText = preview.CustomText;
        return config;
    }

    /// <summary>
    /// 判断框的布局是否变了。比对的是位置、尺寸、样式这些会影响显示的字段。
    /// 每 100 毫秒做一次浅比较，比订阅配置变更事件更直接，也不怕漏通知。
    /// </summary>
    private bool FrameLayoutChanged(AppConfig config)
    {
        var enabled = config.Frames.Frames.Where(f => f.Enabled).ToList();
        if (enabled.Count != _frameSnapshot.Count)
        {
            return true;
        }

        for (var i = 0; i < enabled.Count; i++)
        {
            var a = enabled[i];
            var b = _frameSnapshot[i];

            if (!string.Equals(a.Id, b.Id, StringComparison.Ordinal)
                || Math.Abs(a.Left - b.Left) > 0.5
                || Math.Abs(a.Top - b.Top) > 0.5
                || Math.Abs(a.Width - b.Width) > 0.5
                || Math.Abs(a.Height - b.Height) > 0.5
                || Math.Abs(a.RotationDegrees - b.RotationDegrees) > 0.01
                || a.FontSize != b.FontSize
                || !string.Equals(a.TextColor, b.TextColor, StringComparison.Ordinal)
                || a.Opacity != b.Opacity
                || a.Locked != b.Locked)
            {
                return true;
            }
        }

        // **全局样式**也必须比对。
        //
        // 这一段是补上的：之前只比对了每个框各自的属性，漏了 FramesConfig 上
        // 那些共享字段（fontSize / opacity / fontFamily / textColor ...）。
        // 设置界面里的字号滑块、透明度滑块改的正是这些共享字段，
        // 于是配置明明变了、这里却判定"没变化"，样式永远不重新应用——
        // 表现就是"拖了滑块没反应"。这个 bug 是用户报出来的。
        return StyleChanged(config.Frames);
    }

    /// <summary>全局样式有没有变化。有变化就需要重新应用到所有框。</summary>
    private bool StyleChanged(FramesConfig style)
    {
        if (_styleSnapshot is null)
        {
            return true;
        }

        return _styleSnapshot.FontSize != style.FontSize
               || _styleSnapshot.Opacity != style.Opacity
               || _styleSnapshot.ShowTrackInfo != style.ShowTrackInfo
               || _styleSnapshot.LetterSpacing != style.LetterSpacing
               || _styleSnapshot.FadeInMs != style.FadeInMs
               || _styleSnapshot.FadeOutMs != style.FadeOutMs
               || _styleSnapshot.HoldUntilNextFrameLine != style.HoldUntilNextFrameLine
               || _styleSnapshot.HoldMs != style.HoldMs
               || _styleSnapshot.GlowRadius != style.GlowRadius
               || _styleSnapshot.GlowOpacity != style.GlowOpacity
               || _styleSnapshot.LyricEffect != style.LyricEffect
               || _styleSnapshot.EffectIntensity != style.EffectIntensity
               || _styleSnapshot.MotionDirection != style.MotionDirection
               || _styleSnapshot.MotionPeriodMs != style.MotionPeriodMs
               || _styleSnapshot.ParticleSize != style.ParticleSize
               || _styleSnapshot.ParticleCount != style.ParticleCount
               || _styleSnapshot.ParticleSpread != style.ParticleSpread
               || _styleSnapshot.ParticleDurationMs != style.ParticleDurationMs
               || !string.Equals(_styleSnapshot.FontFamily, style.FontFamily, StringComparison.Ordinal)
               || !string.Equals(_styleSnapshot.TextColor, style.TextColor, StringComparison.Ordinal)
               || !_styleSnapshot.TextGradientColors.SequenceEqual(style.TextGradientColors, StringComparer.Ordinal)
               || !string.Equals(_styleSnapshot.PlayedTextColor, style.PlayedTextColor, StringComparison.Ordinal)
               || !string.Equals(_styleSnapshot.UnplayedTextColor, style.UnplayedTextColor, StringComparison.Ordinal)
               || _styleSnapshot.PlaybackEffect != style.PlaybackEffect
               || !string.Equals(_styleSnapshot.SubTextColor, style.SubTextColor, StringComparison.Ordinal)
               || !string.Equals(_styleSnapshot.GlowColor, style.GlowColor, StringComparison.Ordinal);
    }

    /// <summary>
    /// 按取模分配把当前句推给对应的框。
    ///
    /// 默认让每个框保留自己的歌词，直到该框下一句即将出现时再淡出；
    /// 用户也可以切回每次全局换句都淡出的旧节奏。
    /// </summary>
    private void UpdateLyricsDisplay(
        NowPlaying nowPlaying,
        TimeSpan position,
        FramesConfig frames,
        bool forceUpdate,
        bool becamePaused)
    {
        var fadeEnabled = frames.PlaybackEffect == PlaybackEffectMode.Fade;
        var fadeInMs = fadeEnabled ? frames.FadeInMs : 0;
        var fadeOutMs = fadeEnabled ? frames.FadeOutMs : 0;
        if (_timings.Count == 0)
        {
            // 查找已经结束、但一句歌词都没有（本地没有、也没联网到）。
            // 这种情况必须给一句提示——直接淡出会留下一个完全空白的窗口，
            // 用户看到的是"程序坏了"，而不是"这首歌没有歌词"。
            var notFound = $"{nowPlaying.Title}（未找到歌词）";

            foreach (var window in _windows.Values)
            {
                window.SetTrackText(NowPlayingDisplay.GetSubText(nowPlaying));
                window.ShowLine(notFound, fadeInMs);
                var frame = frames.Frames.FirstOrDefault(candidate => candidate.Id == window.FrameId);
                if (frame is not null)
                {
                    window.UpdatePlaybackProgress(null, position, frames, frame);
                }
            }

            return;
        }

        var current = LyricTimeline.FindAt(_timings, position);
        var subText = NowPlayingDisplay.GetSubText(nowPlaying);

        var enabledFrames = frames.GetEnabledFrames();
        if (enabledFrames.Count == 0)
        {
            return;
        }

        // 每个歌词框各自找“最近一条已开始的歌词”和“该框下一条歌词”。
        // 这样前一个框的歌词可以跨过其它框的歌词继续停留。
        var latestLineByFrame = new LyricLineTiming?[enabledFrames.Count];
        var nextLineByFrame = new LyricLineTiming?[enabledFrames.Count];
        foreach (var timing in _timings)
        {
            var ordinal = timing.Index % enabledFrames.Count;
            if (timing.Start <= position)
            {
                latestLineByFrame[ordinal] = timing;
                nextLineByFrame[ordinal] = null;
            }
            else if (latestLineByFrame[ordinal] is not null && nextLineByFrame[ordinal] is null)
            {
                nextLineByFrame[ordinal] = timing;
            }
        }

        var timelineEnd = _timings.Max(timing => timing.End);
        var trackKey = _lyricsKey;

        foreach (var (id, window) in _windows)
        {
            var frame = frames.Frames.FirstOrDefault(f => f.Id == id);
            if (frame is null)
            {
                continue;
            }

            window.SetTrackText(subText);

            // 这个框负责哪一句？按"它是启用列表里的第几个"来定。
            var ordinal = enabledFrames.FindIndex(f => f.Id == id);
            if (ordinal < 0)
            {
                continue;
            }

            if (!_framePresentations.TryGetValue(id, out var presentation))
            {
                presentation = new FramePresentationState();
                _framePresentations[id] = presentation;
            }

            var desiredLine = frames.HoldUntilNextFrameLine
                ? latestLineByFrame[ordinal]
                : current is not null && ordinal == current.Index % enabledFrames.Count ? current : null;

            if (forceUpdate || becamePaused)
            {
                presentation.SuppressedLineIndex = null;
            }

            // A line whose configured hold duration already expired must stay blank
            // until the next line assigned to this frame becomes due.
            if (presentation.SuppressedLineIndex is { } suppressedIndex
                && desiredLine?.Index != suppressedIndex)
            {
                presentation.SuppressedLineIndex = null;
            }

            if (desiredLine is not null
                && presentation.SuppressedLineIndex == desiredLine.Index
                && !forceUpdate)
            {
                desiredLine = null;
            }

            if (desiredLine is not null && position >= timelineEnd)
            {
                desiredLine = null;
            }

            var currentLineIsPresented = desiredLine is not null
                && presentation.VisibleLineIndex == desiredLine.Index;
            if (!forceUpdate && !becamePaused && nowPlaying.IsPlaying && currentLineIsPresented)
            {
                var holdExpired = frames.HoldMs > 0
                    && presentation.FadeInCompletedAtPosition is { } fadeInCompletedAt
                    && position >= fadeInCompletedAt + TimeSpan.FromMilliseconds(frames.HoldMs);

                var boundary = frames.HoldUntilNextFrameLine
                    ? nextLineByFrame[ordinal]?.Start ?? timelineEnd
                    : desiredLine!.End;
                var nextLineReached = position >= boundary;

                if (holdExpired || nextLineReached)
                {
                    presentation.SuppressedLineIndex = desiredLine!.Index;
                    desiredLine = null;
                }
            }

            window.UpdatePlaybackProgress(desiredLine, position, frames, frame);

            if (forceUpdate || becamePaused)
            {
                // Seeking or pausing must correct the visible line immediately and
                // cancel an old fade-out, even if the lyric text happens to be equal.
                presentation.Generation++;
                presentation.RequestedLineIndex = desiredLine?.Index;
                presentation.VisibleLineIndex = null;
                presentation.FadeInCompletedAtPosition = null;

                if (desiredLine is null)
                {
                    window.FadeOut(fadeOutMs);
                }
                else
                {
                    ShowFrameLine(
                        id,
                        window,
                        desiredLine,
                        frames,
                        trackKey,
                        presentation.Generation,
                        interruptFadeOut: true,
                        immediate: becamePaused);
                    window.UpdatePlaybackProgress(desiredLine, position, frames, frame);
                }

                continue;
            }

            if (desiredLine is null)
            {
                if (presentation.RequestedLineIndex is not null || presentation.VisibleLineIndex is not null)
                {
                    presentation.Generation++;
                    presentation.RequestedLineIndex = null;
                    presentation.VisibleLineIndex = null;
                    presentation.FadeInCompletedAtPosition = null;
                    window.FadeOut(fadeOutMs);
                }
                else
                {
                    // Also clears status text that may have been shown while lyrics loaded.
                    window.FadeOut(fadeOutMs);
                }

                continue;
            }

            if (presentation.RequestedLineIndex == desiredLine.Index)
            {
                continue;
            }

            var previousRequestedIndex = presentation.RequestedLineIndex;
            presentation.Generation++;
            presentation.RequestedLineIndex = desiredLine.Index;
            presentation.FadeInCompletedAtPosition = null;
            var needsToFadeOut = presentation.VisibleLineIndex is not null
                || previousRequestedIndex is not null;
            if (needsToFadeOut)
            {
                presentation.VisibleLineIndex = null;
                window.FadeOut(fadeOutMs, clearPending: false);
            }

            ShowFrameLine(
                id,
                window,
                desiredLine,
                frames,
                trackKey,
                presentation.Generation,
                interruptFadeOut: false,
                immediate: false);
            window.UpdatePlaybackProgress(desiredLine, position, frames, frame);
        }
    }

    private void ShowFrameLine(
        string frameId,
        OverlayWindow window,
        LyricLineTiming line,
        FramesConfig frames,
        string trackKey,
        int generation,
        bool interruptFadeOut,
        bool immediate)
    {
        window.ShowLine(
            line.Text,
            immediate || frames.PlaybackEffect == PlaybackEffectMode.LeftToRight ? 0 : frames.FadeInMs,
            interruptFadeOut,
            onFadeInCompleted: () =>
            {
                var currentTrack = ResolveNowPlaying();
                if (_disposed
                    || !string.Equals(_lyricsKey, trackKey, StringComparison.Ordinal)
                    || !currentTrack.HasTrack
                    || !_framePresentations.TryGetValue(frameId, out var state)
                    || state.Generation != generation
                    || state.RequestedLineIndex != line.Index)
                {
                    return;
                }

                state.VisibleLineIndex = line.Index;
                state.FadeInCompletedAtPosition = GetCurrentLyricPosition(
                    currentTrack, DateTimeOffset.UtcNow)
                    + TimeSpan.FromMilliseconds(
                        _config.Current.GetSongLyricOffsetMs(currentTrack.Title, currentTrack.Artist));
            });
    }

    private void ClearFramePresentationState()
    {
        _framePresentations.Clear();
    }

    // ---------------------------------------------------------------
    // 歌词获取
    // ---------------------------------------------------------------

    private void ResetLyricsState()
    {
        StopAudioLyricSync(clearPosition: true);
        ClearFramePresentationState();
        _lyricsKey = string.Empty;
        _timings = Array.Empty<LyricLineTiming>();
        _lyricsStatus = LyricsStatus.Idle;
        _rejectedCandidateKeys.Clear();
        _rejectedCandidateTrackKey = string.Empty;
        SetCurrentLyricsResolution(null);
        _playbackTrackKey = string.Empty;
        _lastPlayerPosition = TimeSpan.Zero;
        _lastPlayerPositionAtUtc = DateTimeOffset.MinValue;
        _lastPlayerWasPlaying = false;
        _hasPlaybackSnapshot = false;
        _forceNextLyricsUpdate = false;
    }

    private void BeginLyricsFetch(NowPlaying nowPlaying, bool skipCache = false)
    {
        CancelLyricsFetch();

        var cts = new CancellationTokenSource();
        _lyricsFetch = cts;
        var cancellationToken = cts.Token;
        var key = _lyricsKey;
        var excludedCandidateKeys = _rejectedCandidateKeys.ToHashSet(StringComparer.Ordinal);

        _ = Task.Run(async () =>
        {
            LyricsResolution resolution;
            try
            {
                resolution = await _lyricsResolver.ResolveAsync(
                    nowPlaying.Title,
                    nowPlaying.Artist,
                    nowPlaying.AlbumTitle,
                    nowPlaying.Duration,
                    cancellationToken,
                    excludedCandidateKeys,
                    skipCache).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                resolution = LyricsResolution.NotFound("查找歌词时出错");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return;
            }

            System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
            {
                // 结果回来时可能已经换歌了，丢弃过期结果
                if (_disposed || cancellationToken.IsCancellationRequested
                    || !string.Equals(_lyricsKey, key, StringComparison.Ordinal))
                {
                    return;
                }

                var currentTrack = ResolveNowPlaying();
                var duration = currentTrack.HasTrack
                    && string.Equals(currentTrack.Title + "\u0000" + currentTrack.Artist, key, StringComparison.Ordinal)
                        ? currentTrack.Duration
                        : nowPlaying.Duration;
                _timings = LyricTimeline.Build(resolution.Document, duration);
                _lyricsStatus = LyricsStatus.Done;
                SetCurrentLyricsResolution(resolution);
                Tick();
            });
        });
    }

    private void CancelLyricsFetch()
    {
        var cts = _lyricsFetch;
        if (cts is null)
        {
            return;
        }

        _lyricsFetch = null;

        try
        {
            cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        cts.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        CancelLyricsFetch();
        StopAudioLyricSync(clearPosition: true);
        _media.Changed -= OnMediaChanged;
        _audioLyricSynchronizer.StatusChanged -= OnAudioLyricSyncStatusChanged;
        _audioLyricSynchronizer.RunStarted -= OnAudioLyricSyncRunStarted;
        _audioLyricSynchronizer.AlignmentFound -= OnAudioLyricAlignmentFound;

        PersistLayout();

        foreach (var window in _windows.Values)
        {
            window.Close();
        }

        _windows.Clear();
        _media.Dispose();
    }
}
