namespace LyricCanvas.Core;

/// <summary>
/// 一行歌词：这一行从哪个时间点开始显示，以及显示什么文字。
/// </summary>
/// <param name="Time">该行开始显示的时间点（相对歌曲开头）。</param>
/// <param name="Text">歌词文字。</param>
/// <param name="Segments">逐词/逐音节时间戳；普通 LRC 没有时为 null。</param>
public sealed record LyricLine(
    TimeSpan Time,
    string Text,
    IReadOnlyList<LyricSegment>? Segments = null);

/// <summary>
/// 歌词行中的一段逐词/逐音节时间。增强 LRC 通常标注每段的起点，显示时
/// 会在该段起点到下一段起点之间推进高亮；End 保留文件明确标出的终点。
/// </summary>
public sealed record LyricSegment(TimeSpan Start, string Text, TimeSpan? End = null);
