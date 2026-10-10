namespace LyricCanvas.Core;

/// <summary>
/// 一次歌词检索的输入。
/// </summary>
/// <param name="Title">曲名（来自 SMTC，未归一化）。</param>
/// <param name="Artist">歌手，可能为空。</param>
/// <param name="AlbumTitle">专辑，可能为空。</param>
/// <param name="Duration">歌曲总时长，可能为 <see cref="TimeSpan.Zero"/>（拿不到）。</param>
public sealed record LyricsQuery(string Title, string Artist, string AlbumTitle, TimeSpan Duration)
{
    /// <summary>至少要有曲名才值得去搜。</summary>
    public bool IsUsable => !string.IsNullOrWhiteSpace(Title);
}

/// <summary>
/// 一个歌词来源（某个公开歌词接口）。
///
/// 抽成接口是为了：换源、加源、以及测试时塞入假来源，都不用改上层代码。
/// </summary>
public interface ILyricsSource
{
    /// <summary>来源标识，例如 "kugou"、"lrclib"。会写进缓存文件头部，便于排查。</summary>
    string Name { get; }

    /// <summary>
    /// 搜索候选。
    ///
    /// 约定的返回语义：
    ///   - 返回的候选**可以**已经带好歌词正文（<see cref="LyricCandidate.Lyrics"/> 非空）；
    ///   - 也可以只带 <see cref="LyricCandidate.SourceId"/>，"歌词正文"等
    ///     <see cref="FetchLyricsAsync"/> 再取（两步式的接口，例如酷狗）。
    /// </summary>
    Task<IReadOnlyList<LyricCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default);

    /// <summary>
    /// 取某个候选的歌词正文。候选已经带正文时可以直接返回它自己的正文。
    /// 取不到时返回 null。
    /// </summary>
    Task<string?> FetchLyricsAsync(LyricCandidate candidate, CancellationToken cancellationToken = default);
}
