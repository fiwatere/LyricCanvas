namespace LyricCanvas.Core;

/// <summary>
/// "当前正在播放什么"的一份快照。
///
/// 用 record 是因为它天然是不可变的数据载体：读出来就是一份事实，
/// 界面拿到之后不会再被后台线程改掉，省掉一大堆跨线程同步问题。
/// </summary>
/// <param name="AppId">
/// 会话来源，例如 "KuGou.exe"、"Spotify.exe"。Windows 上叫
/// SourceAppUserModelId。用它来区分是哪个播放器在放。
/// </param>
/// <param name="Title">曲名。可能为空。</param>
/// <param name="Artist">歌手。可能为空。</param>
/// <param name="AlbumTitle">专辑。可能为空。</param>
/// <param name="Playback">播放状态。</param>
/// <param name="Position">读取那一刻的播放进度。</param>
/// <param name="Duration">
/// 这首歌的总时长。SMTC 的时间轴里有，但有些播放器会给 0 或给不准，
/// 所以用它的地方（比如匹配歌词时的时长校验）都必须能容忍缺失。
/// </param>
/// <param name="ReadAtUtc">读取那一刻的 UTC 时间，用于推算"现在"的进度。</param>
public sealed record NowPlaying(
    string AppId,
    string Title,
    string Artist,
    string AlbumTitle,
    PlaybackState Playback,
    TimeSpan Position,
    DateTimeOffset ReadAtUtc,
    TimeSpan Duration = default)
{
    /// <summary>播放器时间轴原样报告的位置；Position 可能是为连续显示而外推的值。</summary>
    public TimeSpan? ReportedPosition { get; init; }

    /// <summary>Windows 媒体会话声称最后更新时间轴的时刻。</summary>
    public DateTimeOffset? TimelineUpdatedAtUtc { get; init; }

    /// <summary>没有会话时的空快照。</summary>
    public static NowPlaying Empty { get; } = new(
        AppId: string.Empty,
        Title: string.Empty,
        Artist: string.Empty,
        AlbumTitle: string.Empty,
        Playback: PlaybackState.Unknown,
        Position: TimeSpan.Zero,
        ReadAtUtc: DateTimeOffset.MinValue,
        Duration: TimeSpan.Zero);

    /// <summary>是否真的有一首歌可以显示（曲名非空）。</summary>
    public bool HasTrack => !string.IsNullOrWhiteSpace(Title);

    /// <summary>是否正在播放。</summary>
    public bool IsPlaying => Playback == PlaybackState.Playing;

    /// <summary>总时长是否可用（大于 0）。不可用时不要拿它做判断。</summary>
    public bool HasDuration => Duration > TimeSpan.Zero;

    /// <summary>
    /// 推算"此时此刻"的播放进度。
    ///
    /// SMTC 只在播放开始、暂停、拖动进度条等时刻推送时间轴更新，
    /// 不会每毫秒都推。所以正在播放时，真实进度 = 读取时的进度 + 已经过去的时间。
    /// 暂停时进度不动，直接返回读取值。
    /// </summary>
    /// <param name="nowUtc">当前时间。传进来而不是内部取 Now，是为了能写单元测试。</param>
    public TimeSpan GetEstimatedPosition(DateTimeOffset nowUtc)
    {
        if (!IsPlaying || ReadAtUtc == DateTimeOffset.MinValue)
        {
            return Position;
        }

        var elapsed = nowUtc - ReadAtUtc;
        if (elapsed <= TimeSpan.Zero)
        {
            return Position;
        }

        var estimated = Position + elapsed;
        return HasDuration && estimated > Duration ? Duration : estimated;
    }

    /// <summary>"曲名 - 歌手"形式的显示文本，缺哪部分就省哪部分。</summary>
    public string DisplayText
    {
        get
        {
            if (!HasTrack)
            {
                return string.Empty;
            }

            return string.IsNullOrWhiteSpace(Artist) ? Title : $"{Title} - {Artist}";
        }
    }
}
