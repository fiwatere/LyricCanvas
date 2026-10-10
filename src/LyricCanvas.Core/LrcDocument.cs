namespace LyricCanvas.Core;

/// <summary>
/// 一首歌的歌词文档：元信息 + 按时间排好序的歌词行。
/// </summary>
public sealed class LrcDocument
{
    /// <summary>空文档（例如没找到歌词文件时）。</summary>
    public static LrcDocument Empty { get; } = new(
        Array.Empty<LyricLine>(),
        new Dictionary<string, string>(),
        TimeSpan.Zero);

    public LrcDocument(
        IReadOnlyList<LyricLine> lines,
        IReadOnlyDictionary<string, string> metadata,
        TimeSpan offset)
    {
        Lines = lines;
        Metadata = metadata;
        Offset = offset;
    }

    /// <summary>按时间升序排好的歌词行。</summary>
    public IReadOnlyList<LyricLine> Lines { get; }

    /// <summary>
    /// 除了时间戳之外的所有元信息标签，键名统一转成小写，例如
    /// <c>ar</c>（歌手）、<c>ti</c>（标题）、<c>al</c>（专辑）、<c>by</c>（制作者）。
    /// </summary>
    public IReadOnlyDictionary<string, string> Metadata { get; }

    /// <summary>LRC 里 [offset:] 标签带来的整体时间偏移量（已应用到所有行上）。</summary>
    public TimeSpan Offset { get; }

    /// <summary>是否一行歌词都没有。</summary>
    public bool IsEmpty => Lines.Count == 0;

    /// <summary>歌手（对应 LRC 的 [ar:] 标签）。</summary>
    public string Artist => GetMetadata("ar");

    /// <summary>标题（对应 LRC 的 [ti:] 标签）。</summary>
    public string Title => GetMetadata("ti");

    /// <summary>专辑（对应 LRC 的 [al:] 标签）。</summary>
    public string Album => GetMetadata("al");

    private string GetMetadata(string key)
        => Metadata.TryGetValue(key, out var value) ? value : string.Empty;

    /// <summary>
    /// 找出"在 <paramref name="position"/> 这一刻应该显示第几行"。
    ///
    /// 返回的是最后一个时间点 &lt;= position 的行；position 早于第一行时返回 -1。
    /// 用二分查找，所以每帧调用也不会有性能问题。
    /// </summary>
    public int FindLineIndexAt(TimeSpan position)
    {
        var lines = Lines;
        if (lines.Count == 0 || position < lines[0].Time)
        {
            return -1;
        }

        var low = 0;
        var high = lines.Count - 1;
        var result = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);

            if (lines[mid].Time <= position)
            {
                result = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return result;
    }

    /// <summary>取那一刻应该显示的歌词文字；还没到第一行时返回空字符串。</summary>
    public string GetTextAt(TimeSpan position)
    {
        var index = FindLineIndexAt(position);
        return index < 0 ? string.Empty : Lines[index].Text;
    }
}
