namespace LyricCanvas.Core;

/// <summary>
/// 一句歌词在屏幕上该显示的时间区间。
/// </summary>
/// <param name="Index">它在歌词文档里的序号。</param>
/// <param name="Text">歌词文字。</param>
/// <param name="Start">该开始显示的时刻（相对歌曲开头）。</param>
/// <param name="End">
/// 该撤下的时刻。
/// 等于**下一句的开始时间**；最后一句用歌曲总时长兜底；
/// 拿不到总时长时给一个"至少显示这么久"的保底值，免得最后一句永远不消失。
/// </param>
public sealed record LyricLineTiming(
    int Index,
    string Text,
    TimeSpan Start,
    TimeSpan End,
    IReadOnlyList<LyricSegment>? Segments = null)
{
    /// <summary>显示时长。</summary>
    public TimeSpan Duration => End > Start ? End - Start : TimeSpan.Zero;

    /// <summary>某个时刻是否落在这句的区间里（含起点、不含终点）。</summary>
    public bool Contains(TimeSpan position) => position >= Start && position < End;
}

/// <summary>
/// 把歌词文档变成"每句什么时候开始、什么时候结束"的序列。
///
/// 为什么要单独算结束时间：淡入淡出需要知道"这句什么时候该撤下来"。
/// LRC 只标了每句的开始时间，结束时间得靠下一句的开始时间推出来；
/// 最后一句没有下一句，要用歌曲总时长兜底。
/// </summary>
public static class LyricTimeline
{
    /// <summary>
    /// 最后一句拿不到总时长时的保底显示时长。
    /// 给太小会导致歌词早早消失，给太大会让最后一句一直挂着。
    /// </summary>
    private static readonly TimeSpan DefaultLastLineDuration = TimeSpan.FromSeconds(6);

    /// <summary>
    /// 计算当前应高亮到的文本偏移量。增强 LRC 按词/音节时间戳推进；普通 LRC
    /// 在整句区间内均匀估算。返回值以 UTF-16 字符数表示，可用于后续换算屏幕宽度。
    /// </summary>
    public static double GetTextOffsetAt(LyricLineTiming line, TimeSpan position)
    {
        ArgumentNullException.ThrowIfNull(line);
        var textLength = line.Text.Length;
        if (textLength == 0 || position < line.Start)
        {
            return 0;
        }

        if (position >= line.End)
        {
            return textLength;
        }

        var segments = line.Segments;
        if (segments is null || segments.Count == 0)
        {
            var duration = line.End - line.Start;
            return duration <= TimeSpan.Zero
                ? textLength
                : textLength * Math.Clamp((position - line.Start).TotalMilliseconds / duration.TotalMilliseconds, 0, 1);
        }

        var consumed = 0.0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var segmentLength = segment.Text.Length;
            var segmentEnd = segment.End ?? (index + 1 < segments.Count
                ? segments[index + 1].Start
                : line.End);
            if (segmentEnd > line.End)
            {
                segmentEnd = line.End;
            }

            if (position < segment.Start)
            {
                return Math.Clamp(consumed, 0, textLength);
            }

            if (position < segmentEnd && segmentEnd > segment.Start)
            {
                var fraction = (position - segment.Start).TotalMilliseconds
                    / (segmentEnd - segment.Start).TotalMilliseconds;
                return Math.Clamp(consumed + segmentLength * Math.Clamp(fraction, 0, 1), 0, textLength);
            }

            consumed += segmentLength;
        }

        return Math.Clamp(consumed, 0, textLength);
    }

    /// <summary>
    /// 算出每句的显示区间。
    /// </summary>
    /// <param name="document">歌词文档。</param>
    /// <param name="songDuration">
    /// 歌曲总时长（来自 SMTC 的时间轴）。拿不到时传 <see cref="TimeSpan.Zero"/>。
    /// </param>
    public static IReadOnlyList<LyricLineTiming> Build(LrcDocument document, TimeSpan songDuration)
    {
        if (document is null || document.IsEmpty)
        {
            return Array.Empty<LyricLineTiming>();
        }

        var lines = document.Lines;
        var result = new List<LyricLineTiming>(lines.Count);

        for (var i = 0; i < lines.Count;)
        {
            var start = lines[i].Time;
            var next = i + 1;
            while (next < lines.Count && lines[next].Time == start)
            {
                next++;
            }

            // 同一时间的原文/译文共用下一个不同时间点，避免其中一行被错误延长六秒。
            var end = next < lines.Count ? lines[next].Time : songDuration;
            if (end <= start)
            {
                end = start.Ticks <= long.MaxValue - DefaultLastLineDuration.Ticks
                    ? start + DefaultLastLineDuration
                    : TimeSpan.MaxValue;
            }

            for (; i < next; i++)
            {
                result.Add(new LyricLineTiming(i, lines[i].Text, start, end, lines[i].Segments));
            }
        }

        return result;
    }

    /// <summary>
    /// 找出某个时刻正在显示的那句。没有则返回 null。
    ///
    /// 用二分查找定位最后一个开始时间不晚于当前位置的候选，再检查结束时间；
    /// 两句之间的空隙不显示内容，同一开始时间优先取文件中靠后的行。
    /// </summary>
    public static LyricLineTiming? FindAt(IReadOnlyList<LyricLineTiming> timings, TimeSpan position)
    {
        if (timings is null || timings.Count == 0)
        {
            return null;
        }

        var low = 0;
        var high = timings.Count - 1;
        var candidate = -1;

        while (low <= high)
        {
            var mid = low + ((high - low) / 2);
            if (timings[mid].Start <= position)
            {
                candidate = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        if (candidate < 0)
        {
            // 还没到第一句
            return null;
        }

        return timings[candidate].Contains(position) ? timings[candidate] : null;
    }
}
