namespace LyricCanvas.Core;

/// <summary>
/// 歌词解析的配置。
/// </summary>
public sealed class LyricsOptions
{
    /// <summary>用户自己的歌词目录（程序只读不写）。</summary>
    public required string LyricsDirectory { get; init; }

    /// <summary>
    /// 按优先级排列的用户歌词搜索目录。为空时兼容旧调用，仅搜索 <see cref="LyricsDirectory"/>。
    /// </summary>
    public IReadOnlyList<string> SearchDirectories { get; init; } = Array.Empty<string>();

    /// <summary>
    /// 是否允许联网抓歌词。关掉后只认本地文件和已有的缓存。
    /// </summary>
    public bool FetchFromNetwork { get; init; } = true;

    /// <summary>
    /// 是否优先使用缓存。正常都应该是 true——
    /// 缓存命中就不用再发网络请求，也保证同一首歌的歌词稳定不变。
    /// </summary>
    public bool UseCache { get; init; } = true;

    /// <summary>本次要用的歌词来源，按顺序尝试。</summary>
    public IReadOnlyList<ILyricsSource> Sources { get; init; } = Array.Empty<ILyricsSource>();

    /// <summary>检索超时。太短会频繁失败，太长会让换歌后歌词迟迟不出现。</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(8);
}
