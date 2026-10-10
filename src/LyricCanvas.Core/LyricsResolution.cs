namespace LyricCanvas.Core;

/// <summary>
/// 一次歌词解析的结果。
/// </summary>
/// <param name="Document">解析好的歌词。没拿到时是空文档。</param>
/// <param name="Origin">歌词是从哪来的，用于显示和排查。见 <see cref="LyricsOrigin"/>。</param>
/// <param name="SourceName">来自哪个歌词源，例如 "kugou"；本地文件时为空。</param>
/// <param name="FilePath">歌词文件路径（本地或缓存）；没有文件时为空。</param>
/// <param name="NeedsConfirmation">
/// 匹配不确定，建议让用户确认（例如曲名对上了但歌手不一致，或有多个同名版本）。
/// 此时仍然返回了最可能的那一个，界面可以照常显示并提示一句。
/// </param>
/// <param name="Message">给用户看的说明文字，例如"未找到歌词"。可能为空。</param>
public sealed record LyricsResolution(
    LrcDocument Document,
    LyricsOrigin Origin,
    string SourceName,
    string FilePath,
    bool NeedsConfirmation,
    string Message)
{
    /// <summary>本次采用的候选身份，用于确认或排除低置信匹配。</summary>
    public string MatchedCandidateKey { get; init; } = string.Empty;

    /// <summary>最终采用的候选曲名和歌手；本地文件与旧缓存为空。</summary>
    public string MatchedTitle { get; init; } = string.Empty;
    public string MatchedArtist { get; init; } = string.Empty;

    /// <summary>用户是否已确认过该缓存匹配。</summary>
    public bool IsConfirmed { get; init; }

    /// <summary>是否拿到了可用的歌词。</summary>
    public bool HasLyrics => !Document.IsEmpty;

    /// <summary>什么都没找到。</summary>
    public static LyricsResolution NotFound(string message) =>
        new(LrcDocument.Empty, LyricsOrigin.None, string.Empty, string.Empty, false, message);
}

/// <summary>歌词的来源。</summary>
public enum LyricsOrigin
{
    /// <summary>没找到。</summary>
    None = 0,

    /// <summary>用户自己放在歌词目录里的文件。优先级最高。</summary>
    UserFile = 1,

    /// <summary>程序之前自动抓取、存在缓存里的。</summary>
    Cache = 2,

    /// <summary>这次刚从网络抓到的。</summary>
    Network = 3,
}
