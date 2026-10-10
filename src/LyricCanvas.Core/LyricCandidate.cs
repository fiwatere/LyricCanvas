namespace LyricCanvas.Core;

/// <summary>
/// 从歌词接口搜到的一条候选结果。
/// </summary>
/// <param name="Source">来源标识，例如 "kugou"、"lrclib"。用于显示和排查问题。</param>
/// <param name="Title">候选的曲名。</param>
/// <param name="Artist">候选的歌手，可能为空。</param>
/// <param name="AlbumTitle">候选的专辑，可能为空。</param>
/// <param name="Duration">候选的时长。搜到的结果大多带这个，用于校验是不是同一首歌。</param>
/// <param name="Lyrics">歌词正文（标准 LRC 文本）。</param>
/// <param name="SourceId">
/// 该来源内部用来取歌词的标识。这条候选可能只有歌词正文（<see cref="Lyrics"/> 已填），
/// 也可能需要拿这个 id 再请求一次才能拿到正文。
/// </param>
/// <param name="SourceKey">取歌词时需要一并带上的凭据（例如酷狗的 accesskey）。</param>
public sealed record LyricCandidate(
    string Source,
    string Title,
    string Artist,
    string AlbumTitle,
    TimeSpan Duration,
    string Lyrics,
    string? SourceId = null,
    string? SourceKey = null)
{
    /// <summary>是否已经拿到歌词正文。没有的话需要拿 SourceId 再请求一次。</summary>
    public bool HasLyrics => !string.IsNullOrWhiteSpace(Lyrics);
}
