using System.Globalization;
using System.Text;

namespace LyricCanvas.Core;

/// <summary>
/// LRC 歌词解析器。
///
/// 支持的格式（标准 LRC）：
/// <code>
/// [ti:歌曲名]
/// [ar:歌手]
/// [al:专辑]
/// [by:制作人]
/// [offset:+200]           整体时间偏移，单位毫秒
/// [00:12.34]这一行从 12.34 秒开始
/// [01:23.456]毫秒写三位也可以
/// [00:10.00][00:20.00]一行挂多个时间戳（副歌重复时常见）
/// </code>
///
/// 设计原则：
///   - 不认识的行整行忽略，绝不抛异常。网上的歌词文件格式很乱，
///     解析器因为一行怪东西就崩溃是不可接受的。
///   - 时间戳支持的写法尽量宽：分秒之间用 : 或 ：，小数部分 0~3 位。
/// </summary>
public static class LrcParser
{
    private const long MaxMilliseconds = long.MaxValue / TimeSpan.TicksPerMillisecond;
    private static readonly Encoding LegacyEncoding = CodePagesEncodingProvider.Instance.GetEncoding(936)
        ?? Encoding.UTF8;

    /// <summary>解析一段 LRC 文本。</summary>
    /// <param name="content">LRC 内容。</param>
    public static LrcDocument Parse(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return LrcDocument.Empty;
        }

        var entries = new List<LyricLine>();
        var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long offsetMilliseconds = 0;

        // 统一换行符：CRLF（Windows）、LF（Unix）、CR（老式 Mac）都当换行。
        // 不先归一化的话，只用 CR 的歌词文件会被整份当成一行，一个时间戳都解析不出来。
        content = content.Replace("\r\n", "\n").Replace('\r', '\n');

        var lines = content.Split('\n');

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim().TrimStart('\uFEFF');
            if (line.Length == 0)
            {
                continue;
            }

            var times = new List<TimeSpan>();

            // 从行首开始，连续吃掉所有 [....] 标签
            var index = 0;
            while (index < line.Length && line[index] == '[')
            {
                var close = line.IndexOf(']', index + 1);
                if (close < 0)
                {
                    // 没有闭合的 ]，这行没法解析
                    break;
                }

                var inner = line.Substring(index + 1, close - index - 1);

                if (TryParseTimeTag(inner, out var time))
                {
                    times.Add(time);
                }
                else if (TryParseMetadataTag(inner, out var key, out var value))
                {
                    if (string.Equals(key, "offset", StringComparison.OrdinalIgnoreCase))
                    {
                        if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedOffset)
                            && parsedOffset is >= -MaxMilliseconds and <= MaxMilliseconds)
                        {
                            offsetMilliseconds = parsedOffset;
                        }
                    }
                    else
                    {
                        metadata[key] = value;
                    }
                }
                else
                {
                    // 既不是时间戳也不是元信息，可能是 [00:12 这种残废标签
                    // 只要它看起来是时间戳的一部分就忽略，不算正文
                    if (!LooksLikeTimeTag(inner))
                    {
                        break;
                    }
                }

                index = close + 1;
            }

            var text = line.Substring(Math.Min(index, line.Length)).Trim();
            if (text.Length == 0)
            {
                // 纯标签行（元信息）或空行
                continue;
            }

            var baseTime = times.Count > 0 ? times[0] : TimeSpan.Zero;
            var (plainText, segments) = ParseTimedSegments(text, baseTime);
            if (plainText.Length == 0)
            {
                continue;
            }

            foreach (var time in times)
            {
                IReadOnlyList<LyricSegment>? shiftedSegments = segments;
                if (segments is { Count: > 0 } && time != baseTime)
                {
                    var shift = time - baseTime;
                    shiftedSegments = segments
                        .Select(segment => ShiftSegment(segment, shift))
                        .ToList();
                }

                entries.Add(new LyricLine(time, plainText, shiftedSegments));
            }
        }

        // 应用 offset。
        // 取值约定（跟主流播放器一致）：正的 offset 表示歌词整体提前显示，
        // 也就是每一行的时间戳都要减去这个毫秒数。
        // 结果为负时归零——时间戳没法是负数，那一行就从头开始显示。
        var offset = TimeSpan.FromTicks(offsetMilliseconds * TimeSpan.TicksPerMillisecond);
        if (offsetMilliseconds != 0)
        {
            for (var i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                entries[i] = entry with
                {
                    Time = ShiftTime(entry.Time, -offset),
                    Segments = entry.Segments?.Select(segment => ShiftSegment(segment, -offset)).ToList(),
                };
            }
        }

        // 按时间排序。用 OrderBy 是稳定排序，同一时间点的多行会保持原顺序。
        var ordered = entries.OrderBy(e => e.Time).ToList();

        return new LrcDocument(ordered, metadata, offset);
    }

    /// <summary>
    /// 读取增强 LRC 的 &lt;mm:ss.xx&gt; 词/音节标签，同时移除标签生成普通显示文本。
    /// 不含有效内嵌时间戳的正文原样返回，继续按普通 LRC 处理。
    /// </summary>
    private static (string Text, IReadOnlyList<LyricSegment>? Segments) ParseTimedSegments(
        string text,
        TimeSpan lineStart)
    {
        var visibleText = new StringBuilder(text.Length);
        var segmentText = new StringBuilder();
        var segments = new List<LyricSegment>();
        var segmentStart = lineStart;
        var foundTag = false;

        void FlushSegment(TimeSpan? end = null)
        {
            if (segmentText.Length == 0)
            {
                return;
            }

            var value = segmentText.ToString();
            visibleText.Append(value);
            segments.Add(new LyricSegment(segmentStart, value, end));
            segmentText.Clear();
        }

        for (var index = 0; index < text.Length;)
        {
            if (text[index] == '<')
            {
                var close = text.IndexOf('>', index + 1);
                if (close > index
                    && TryParseTimeTag(text[(index + 1)..close], out var time))
                {
                    // 坏文件里的逆序标签不应使高亮向后跳，也不能提前到该行起点之前。
                    var boundary = time < segmentStart ? segmentStart : time;
                    FlushSegment(boundary);
                    segmentStart = boundary;
                    foundTag = true;
                    index = close + 1;
                    continue;
                }
            }

            segmentText.Append(text[index]);
            index++;
        }

        FlushSegment();
        return foundTag
            ? (visibleText.ToString(), segments)
            : (text, null);
    }

    private static LyricSegment ShiftSegment(LyricSegment segment, TimeSpan shift)
        => segment with
        {
            Start = ShiftTime(segment.Start, shift),
            End = segment.End is { } end ? ShiftTime(end, shift) : null,
        };

    private static TimeSpan ShiftTime(TimeSpan time, TimeSpan shift)
    {
        if (shift.Ticks > 0 && time.Ticks > long.MaxValue - shift.Ticks)
        {
            return TimeSpan.MaxValue;
        }

        if (shift.Ticks < 0 && time.Ticks < -shift.Ticks)
        {
            return TimeSpan.Zero;
        }

        return TimeSpan.FromTicks(time.Ticks + shift.Ticks);
    }

    /// <summary>从文件解析。文件不存在时返回空文档，不抛异常。</summary>
    public static LrcDocument ParseFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return LrcDocument.Empty;
        }

        try
        {
            // 先识别 BOM（UTF-8/UTF-16 等），无 BOM 时严格按 UTF-8 解码。
            // .NET 的 Encoding.Default 固定为 UTF-8，GBK 文件必须显式使用代码页 936。
            var bytes = File.ReadAllBytes(path);
            string content;
            try
            {
                using var stream = new MemoryStream(bytes, writable: false);
                using var reader = new StreamReader(stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                    detectEncodingFromByteOrderMarks: true);
                content = reader.ReadToEnd();
            }
            catch (DecoderFallbackException)
            {
                content = LegacyEncoding.GetString(bytes);
            }

            return Parse(content);
        }
        catch (IOException)
        {
            return LrcDocument.Empty;
        }
        catch (UnauthorizedAccessException)
        {
            return LrcDocument.Empty;
        }
    }

    /// <summary>
    /// 解析 [....] 里的时间戳，例如 "00:12.34"、"1:02:03.456"、"00:12"。
    /// 除了分:秒.毫秒，也顺手支持 时:分:秒。
    /// </summary>
    private static bool TryParseTimeTag(string inner, out TimeSpan time)
    {
        time = default;

        // 分隔符可能是半角冒号或全角冒号（中文输入法下很容易打出来）
        var normalized = inner.Replace('：', ':').Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        var parts = normalized.Split(':');
        if (parts.Length is < 2 or > 3)
        {
            return false;
        }

        // 最后一段是 "秒.毫秒"
        if (!TryParseSecondsPart(parts[^1], out var seconds, out var fraction))
        {
            return false;
        }

        long totalMilliseconds = seconds * 1000L + fraction;

        if (parts.Length == 3)
        {
            if (!TryParseWholeNumber(parts[0], out var hours))
            {
                return false;
            }

            totalMilliseconds += hours * 3_600_000L;
        }

        if (!TryParseWholeNumber(parts[^2], out var minutes))
        {
            return false;
        }

        totalMilliseconds += minutes * 60_000L;

        if (totalMilliseconds > MaxMilliseconds)
        {
            return false;
        }

        time = TimeSpan.FromTicks(totalMilliseconds * TimeSpan.TicksPerMillisecond);
        return true;
    }

    private static bool TryParseSecondsPart(string text, out int seconds, out int fraction)
    {
        seconds = 0;
        fraction = 0;

        var dot = text.IndexOf('.');
        var wholeText = dot >= 0 ? text[..dot] : text;
        var fractionText = dot >= 0 ? text[(dot + 1)..] : string.Empty;

        if (!TryParseWholeNumber(wholeText, out seconds))
        {
            return false;
        }

        if (seconds > 59)
        {
            // 秒数超过 59 视为格式错误，避免把 [99:99] 这种垃圾当成正常时间
            return false;
        }

        if (fractionText.Length == 0)
        {
            return true;
        }

        if (fractionText.Length > 3)
        {
            return false;
        }

        if (!TryParseWholeNumber(fractionText, out var fractionValue))
        {
            return false;
        }

        // 统一为整数毫秒，避免浮点换算让毫秒边界产生舍入误差。
        fraction = fractionValue * (fractionText.Length switch { 1 => 100, 2 => 10, _ => 1 });
        return true;
    }

    private static bool TryParseWholeNumber(string text, out int value)
    {
        value = 0;
        if (text.Length == 0 || text.Length > 9)
        {
            return false;
        }

        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }

        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>解析 [ar:歌手] 这种元信息标签。</summary>
    private static bool TryParseMetadataTag(string inner, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        var colon = inner.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }

        key = inner[..colon].Trim();
        value = inner[(colon + 1)..].Trim();

        // 键名必须是字母数字，防止把 [00:12] 里的 "00" 当键名
        foreach (var c in key)
        {
            if (!char.IsLetterOrDigit(c))
            {
                return false;
            }
        }

        return key.Length > 0;
    }

    /// <summary>判断一段文本"看起来像时间戳但没写完整"，用于决定是否停止解析标签。</summary>
    private static bool LooksLikeTimeTag(string inner)
    {
        foreach (var c in inner)
        {
            if (c is not (>= '0' and <= '9') && c != ':' && c != '.' && c != '：')
            {
                return false;
            }
        }

        return inner.Length > 0;
    }
}
