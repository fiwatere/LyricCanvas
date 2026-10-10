using LyricCanvas.Core;
using Windows.Media.Control;
using WindowsMediaController;

namespace LyricCanvas.Media;

/// <summary>
/// 读取"系统正在播放什么"的封装。
///
/// 底层用 WindowsMediaController 这个库（NuGet 包 Dubya.WindowsMediaController），
/// 它把 Windows 的 SMTC（SystemMediaTransportControls，就是按 Win 键能看到的那套
/// 媒体控制）包装成了 .NET 对象。
///
/// 为什么不自己直接调 WinRT API：
///   直接调需要自己处理 WinRT 的异步、COM 线程模型、会话事件订阅与释放，
///   代码量大概会多两三倍。这个库只依赖 Microsoft.Extensions.Logging.Abstractions，
///   很轻，MIT 协议，且提供了 net8.0-windows10.0.19041 这份目标框架。
///
/// 工作方式：
///   订阅库提供的事件（打开/关闭/换歌/播放状态/时间轴），同时每 250 毫秒轮询一次
///   兜底。为什么要轮询：SMTC 的事件推送并不总是及时，有些播放器（尤其国内播放器）
///   推送得很少；只靠事件会导致界面长时间不刷新。媒体来源筛选只让已识别或用户明确允许的
///   播放器驱动歌词，视频软件和浏览器会话保留在诊断信息中，但不抢占歌词来源。
/// </summary>
public sealed class MediaSessionService : IDisposable
{
    private readonly MediaManager _manager;
    private readonly TimeSpan _pollInterval;
    private readonly Func<string> _mediaAppAllowListProvider;
    private readonly object _gate = new();

    private System.Threading.Timer? _pollTimer;
    private Task? _startTask;
    private NowPlaying _current = NowPlaying.Empty;
    private DateTimeOffset? _missingSinceUtc;
    private string _lastReportedTrackKey = string.Empty;
    private TimeSpan _lastReportedPosition;
    private DateTimeOffset? _lastReportedTimelineUpdatedAtUtc;
    private string _selectedSessionId = string.Empty;
    private MediaSourceDiagnostics _diagnostics = MediaSourceDiagnostics.Empty;
    private int _refreshing;
    private volatile bool _disposed;
    private string? _allowListRaw;
    private HashSet<string> _allowList = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan MissingSessionGracePeriod = TimeSpan.FromSeconds(2.5);
    private static readonly (string Token, string Name)[] KnownMusicApps =
    [
        ("kugou", "酷狗音乐"),
        ("kgmusic", "酷狗音乐"),
        ("qqmusic", "QQ 音乐"),
        ("qq音乐", "QQ 音乐"),
        ("cloudmusic", "网易云音乐"),
        ("neteasecloudmusic", "网易云音乐"),
        ("spotify", "Spotify"),
        ("foobar2000", "foobar2000"),
        ("musicbee", "MusicBee"),
        ("aimp", "AIMP"),
        ("winamp", "Winamp"),
        ("itunes", "iTunes / Apple Music"),
        ("applemusic", "Apple Music"),
        ("tidal", "TIDAL"),
        ("deezer", "Deezer"),
        ("amazonmusic", "Amazon Music"),
        ("ytmusic", "YouTube Music"),
    ];

    private sealed record SessionCandidate(
        MediaManager.MediaSession Session,
        string AppId,
        PlaybackState Playback,
        TimeSpan Position,
        TimeSpan Duration,
        DateTimeOffset? TimelineUpdatedAtUtc,
        bool IsFocused,
        string? Recognition);

    private sealed record SessionReadResult(
        NowPlaying Current,
        NowPlaying? DiagnosticSource,
        int SessionCount,
        string SelectedSessionId,
        string SelectionStatus)
    {
        public bool IsSelected => !string.IsNullOrWhiteSpace(SelectedSessionId);
    }

    /// <summary>当前播放内容的快照。没会话时是 <see cref="NowPlaying.Empty"/>。</summary>
    public NowPlaying Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    /// <summary>当前媒体源诊断快照。设置页可以定时读取，不依赖歌曲切换事件。</summary>
    public MediaSourceDiagnostics Diagnostics
    {
        get
        {
            lock (_gate)
            {
                return _diagnostics;
            }
        }
    }

    /// <summary>
    /// 播放内容或播放状态发生变化时触发。注意：**在后台线程触发**，
    /// WPF 那边要用 Dispatcher 切回界面线程再更新控件。
    /// </summary>
    public event EventHandler<NowPlaying>? Changed;

    /// <param name="pollInterval">轮询间隔。默认 250 毫秒，减少 seek 后显示旧歌词的等待。</param>
    public MediaSessionService(TimeSpan? pollInterval = null)
        : this(() => string.Empty, pollInterval)
    {
    }

    public MediaSessionService(Func<string> mediaAppAllowListProvider, TimeSpan? pollInterval = null)
    {
        _mediaAppAllowListProvider = mediaAppAllowListProvider ?? throw new ArgumentNullException(nameof(mediaAppAllowListProvider));
        _pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(250);
        _manager = new MediaManager();
    }

    /// <summary>启动监听。等价于调用 StartAsync().GetAwaiter().GetResult()。</summary>
    public void Start() => StartAsync().GetAwaiter().GetResult();

    /// <summary>启动监听。</summary>
    public Task StartAsync()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _startTask ??= StartCoreAsync();
        }
    }

    private async Task StartCoreAsync()
    {
        await _manager.StartAsync().ConfigureAwait(false);

        lock (_gate)
        {
            // 启动尚未完成时也可能关闭应用，避免关闭后重新创建轮询定时器。
            if (_disposed)
            {
                return;
            }

            // 重复调用 StartAsync 共用同一启动任务，只订阅一次事件、创建一个定时器。
            _manager.OnAnySessionOpened += _ => Refresh();
            _manager.OnAnySessionClosed += _ => Refresh();
            _manager.OnAnyMediaPropertyChanged += (_, _) => Refresh();
            _manager.OnAnyPlaybackStateChanged += (_, _) => Refresh();
            _manager.OnAnyTimelinePropertyChanged += (_, _) => Refresh();
            _manager.OnFocusedSessionChanged += _ => Refresh();

            _pollTimer = new System.Threading.Timer(
                _ => Refresh(),
                state: null,
                dueTime: _pollInterval,
                period: _pollInterval);
        }

        // 先同步读一次，界面一打开就有内容
        Refresh();
    }

    /// <summary>
    /// 读一次当前状态；有变化就更新 <see cref="Current"/> 并触发 <see cref="Changed"/>。
    ///
    /// 媒体信息暂时不可用时短暂保留上一首歌，并冻结在最后进度。
    /// 只有连续丢失超过宽限期才清空，防止播放器切歌或暂停时 SMTC 短暂断流让歌词消失。
    /// 读的过程中任何异常都吞掉并当作"暂无会话"。
    /// 理由：播放器退出、会话被系统回收、权限变化都会让 WinRT 调用抛异常，
    /// 这些都属于正常情况，不该让整个歌词窗口崩掉。
    /// </summary>
    public void Refresh()
    {
        if (_disposed)
        {
            return;
        }

        if (Interlocked.Exchange(ref _refreshing, 1) != 0)
        {
            return;
        }

        try
        {
            RefreshCore();
        }
        finally
        {
            Volatile.Write(ref _refreshing, 0);
        }
    }

    private void RefreshCore()
    {
        var observedAtUtc = DateTimeOffset.UtcNow;
        SessionReadResult read;
        try
        {
            read = ReadCurrent(observedAtUtc);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"读取媒体会话失败：{ex.Message}");
            read = new SessionReadResult(
                NowPlaying.Empty,
                null,
                0,
                string.Empty,
                "读取媒体会话失败");
        }

        var next = read.Current;
        bool changed;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            if (next.HasTrack)
            {
                _missingSinceUtc = null;

                var trackKey = GetTrackKey(next);
                if (!string.Equals(trackKey, _lastReportedTrackKey, StringComparison.Ordinal))
                {
                    _lastReportedTrackKey = trackKey;
                    _lastReportedPosition = next.ReportedPosition ?? next.Position;
                    _lastReportedTimelineUpdatedAtUtc = next.TimelineUpdatedAtUtc;
                }
                else
                {
                    var sourcePosition = next.ReportedPosition ?? next.Position;
                    var positionChanged = sourcePosition != _lastReportedPosition;
                    var timelineUpdated = next.TimelineUpdatedAtUtc != _lastReportedTimelineUpdatedAtUtc;

                    if (!positionChanged && !timelineUpdated)
                    {
                        // 时间轴没有提供新快照时，不能让重复的旧位置（酷狗目前一直是 0）
                        // 覆盖本地时钟。这样暂停时冻结最后估算位置，恢复时从该位置继续。
                        next = next with
                        {
                            Position = _current.GetEstimatedPosition(observedAtUtc),
                            ReadAtUtc = observedAtUtc,
                        };
                    }
                    else
                    {
                        // 位置或时间戳有变化，才把媒体会话的新值作为时间锚点。
                        // 如果播放器完全不报告 seek，这里无法推断用户跳到了哪里，需由播放器适配器解决。
                        _lastReportedPosition = sourcePosition;
                        _lastReportedTimelineUpdatedAtUtc = next.TimelineUpdatedAtUtc;
                    }
                }
            }
            else if (_current.HasTrack)
            {
                _missingSinceUtc ??= observedAtUtc;
                if (observedAtUtc - _missingSinceUtc < MissingSessionGracePeriod)
                {
                    next = _current with
                    {
                        Playback = PlaybackState.Paused,
                        Position = _current.GetEstimatedPosition(observedAtUtc),
                        ReadAtUtc = observedAtUtc,
                    };
                }
                else
                {
                    _missingSinceUtc = null;
                }
            }
            else
            {
                _missingSinceUtc = null;
                _lastReportedTrackKey = string.Empty;
                _lastReportedPosition = TimeSpan.Zero;
                _lastReportedTimelineUpdatedAtUtc = null;
            }

            changed = !IsSame(_current, next);
            _current = next;

            var diagnosticSource = read.IsSelected
                ? next.HasTrack ? next : read.DiagnosticSource ?? next
                : read.DiagnosticSource ?? NowPlaying.Empty;
            var reportedPosition = diagnosticSource.ReportedPosition ?? diagnosticSource.Position;
            _diagnostics = new MediaSourceDiagnostics(
                read.SessionCount,
                diagnosticSource.AppId,
                diagnosticSource.Title,
                diagnosticSource.Playback,
                reportedPosition,
                diagnosticSource.GetEstimatedPosition(observedAtUtc),
                observedAtUtc,
                diagnosticSource.TimelineUpdatedAtUtc,
                read.SelectionStatus);
        }

        if (changed)
        {
            Changed?.Invoke(this, next);
        }
    }

    /// <summary>
    /// 判定两份快照是否"对界面而言相同"。
    ///
    /// 刻意不比 Position 和 ReadAtUtc：它们几乎每次读取都在变，
    /// 拿它们做比较会导致事件狂发。界面需要精确进度时自己按需调
    /// <see cref="NowPlaying.GetEstimatedPosition"/> 推算。
    /// </summary>
    private static bool IsSame(NowPlaying a, NowPlaying b)
        => a.AppId == b.AppId
           && a.Title == b.Title
           && a.Artist == b.Artist
           && a.Playback == b.Playback;

    private static string GetTrackKey(NowPlaying nowPlaying)
        => nowPlaying.AppId + "\u0000" + nowPlaying.Title + "\u0000" + nowPlaying.Artist;

    private SessionReadResult ReadCurrent(DateTimeOffset sampledAtUtc)
    {
        var sessions = _manager.CurrentMediaSessions.Values.ToList();
        var focused = _manager.GetFocusedSession();
        if (focused is not null && sessions.All(session => session.Id != focused.Id))
        {
            sessions.Add(focused);
        }

        HashSet<string> allowList;
        try
        {
            var raw = _mediaAppAllowListProvider() ?? string.Empty;
            if (!string.Equals(_allowListRaw, raw, StringComparison.Ordinal))
            {
                _allowList = ParseAllowList(raw);
                _allowListRaw = raw;
            }

            allowList = _allowList;
        }
        catch
        {
            allowList = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var candidates = new List<SessionCandidate>(sessions.Count);
        foreach (var session in sessions)
        {
            var candidate = TryReadSession(session, focused?.Id, allowList);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        var recognized = candidates.Where(candidate => candidate.Recognition is not null).ToList();
        var playing = recognized.Where(candidate => candidate.Playback == PlaybackState.Playing).ToList();
        SessionCandidate? selected;

        if (playing.Count > 0)
        {
            selected = playing.FirstOrDefault(candidate => candidate.IsFocused)
                ?? playing.FirstOrDefault(candidate => candidate.Session.Id == _selectedSessionId)
                ?? playing.OrderByDescending(candidate => candidate.TimelineUpdatedAtUtc ?? DateTimeOffset.MinValue).First();
        }
        else
        {
            // 暂停时保留先前的音乐会话；若没有历史选择，再用焦点和最近更新时间决胜。
            selected = recognized.FirstOrDefault(candidate => candidate.Session.Id == _selectedSessionId)
                ?? recognized.FirstOrDefault(candidate => candidate.IsFocused)
                ?? recognized.OrderByDescending(candidate => candidate.TimelineUpdatedAtUtc ?? DateTimeOffset.MinValue).FirstOrDefault();
        }

        if (selected is not null)
        {
            _selectedSessionId = selected.Session.Id;
            var current = ReadCandidate(selected, sampledAtUtc);
            return new SessionReadResult(
                current,
                current,
                sessions.Count,
                selected.Session.Id,
                selected.Recognition!);
        }

        _selectedSessionId = string.Empty;
        var observed = candidates.FirstOrDefault(candidate => candidate.IsFocused)
            ?? candidates.Where(candidate => candidate.Playback == PlaybackState.Playing)
                .OrderByDescending(candidate => candidate.TimelineUpdatedAtUtc ?? DateTimeOffset.MinValue)
                .FirstOrDefault()
            ?? candidates.FirstOrDefault();

        var diagnosticSource = observed is null ? null : ReadCandidate(observed, sampledAtUtc);
        var status = observed is null
            ? sessions.Count == 0 ? "未发现媒体会话" : "媒体会话不可读取"
            : "已忽略未识别的应用；可将其 App ID 加入下方允许列表";
        return new SessionReadResult(
            NowPlaying.Empty,
            diagnosticSource,
            sessions.Count,
            string.Empty,
            status);
    }

    private static SessionCandidate? TryReadSession(
        MediaManager.MediaSession session,
        string? focusedSessionId,
        HashSet<string> allowList)
    {
        try
        {
            var control = session.ControlSession;
            var appId = control.SourceAppUserModelId ?? string.Empty;
            if (appId.Length == 0)
            {
                return null;
            }

            var playback = control.GetPlaybackInfo();
            var timeline = control.GetTimelineProperties();
            var recognition = RecognizeMusicApp(appId, allowList);
            var updatedAt = timeline?.LastUpdatedTime;
            return new SessionCandidate(
                session,
                appId,
                MapPlayback(playback?.PlaybackStatus),
                timeline?.Position ?? TimeSpan.Zero,
                timeline?.EndTime ?? TimeSpan.Zero,
                updatedAt == DateTimeOffset.MinValue ? null : updatedAt,
                string.Equals(session.Id, focusedSessionId, StringComparison.Ordinal),
                recognition);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"忽略读取失败的媒体会话 {session.Id}：{ex.Message}");
            return null;
        }
    }

    private static NowPlaying ReadCandidate(SessionCandidate candidate, DateTimeOffset sampledAtUtc)
    {
        string title = string.Empty;
        string artist = string.Empty;
        string album = string.Empty;
        try
        {
            var props = candidate.Session.ControlSession
                .TryGetMediaPropertiesAsync()
                .AsTask()
                .ConfigureAwait(false)
                .GetAwaiter()
                .GetResult();
            title = props?.Title ?? string.Empty;
            artist = props?.Artist ?? string.Empty;
            album = props?.AlbumTitle ?? string.Empty;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning($"读取媒体标题失败（{candidate.AppId}）：{ex.Message}");
        }

        return new NowPlaying(
            AppId: candidate.AppId,
            Title: title,
            Artist: artist,
            AlbumTitle: album,
            Playback: candidate.Playback,
            Position: candidate.Position,
            ReadAtUtc: sampledAtUtc,
            Duration: candidate.Duration)
        {
            ReportedPosition = candidate.Position,
            TimelineUpdatedAtUtc = candidate.TimelineUpdatedAtUtc,
        };
    }

    private static HashSet<string> ParseAllowList(string? raw)
        => (raw ?? string.Empty)
            .Split(new[] { '\r', '\n', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static string? RecognizeMusicApp(string appId, HashSet<string> allowList)
    {
        if (allowList.Contains(appId))
        {
            return "手动允许的媒体应用";
        }

        var known = KnownMusicApps.FirstOrDefault(entry => appId.Contains(entry.Token, StringComparison.OrdinalIgnoreCase));
        return known.Token is null ? null : $"已识别：{known.Name}";
    }

    /// <summary>把 WinRT 的播放状态映射成本项目自己的枚举。</summary>
    private static PlaybackState MapPlayback(GlobalSystemMediaTransportControlsSessionPlaybackStatus? status)
        => status switch
        {
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed => PlaybackState.Closed,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Opened => PlaybackState.Opened,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing => PlaybackState.Playing,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Paused => PlaybackState.Paused,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped => PlaybackState.Stopped,
            GlobalSystemMediaTransportControlsSessionPlaybackStatus.Changing => PlaybackState.Changing,
            _ => PlaybackState.Unknown,
        };

    public void Dispose()
    {
        System.Threading.Timer? pollTimer;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            pollTimer = _pollTimer;
            _pollTimer = null;
        }

        pollTimer?.Dispose();
        _manager.Dispose();
    }
}
