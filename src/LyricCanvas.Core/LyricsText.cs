using System.Globalization;
using System.Text;

namespace LyricCanvas.Core;

/// <summary>
/// 把曲名/歌手归一化成可以比较的形式。
///
/// 为什么需要它：同一个作品在不同来源里的写法差别很大，直接比字符串几乎永远不相等。
/// 例如以下这些"其实是同一首"：
/// <code>
/// LAST STARDUST
/// Last Stardust (Live)
/// 【官方】LAST STARDUST
/// 起风了
/// 起风了 (Live)
/// </code>
///
/// 归一化做的事：统一字符宽度、去掉括号补充说明与装饰性符号、去掉标点、
/// 转小写、压缩空白。归一化之后比较的是"作品本身"，而不是"书写方式"。
/// </summary>
public static class LyricsText
{
    /// <summary>
    /// 归一化一个曲名或歌手名。归一化后可能为空字符串（原文全是装饰符号时）。
    /// </summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // 先做 Unicode 兼容分解，一次性解决两件事：
        //   1. 全角字母数字变成半角（Ａ→A、１→1）—— 需要 FormKC/FormKD 的兼容映射；
        //   2. 带音标的拉丁字母拆成"基字母 + 组合符"（é → e + ́）—— 需要 D 形式的分解。
        // 实测：FormKC 对 é（预组合的 U+00E9）**不会**拆开，必须用带 D 的形式。
        // FormKD 同时具备兼容映射和分解，所以选它。
        var normalized = value.Normalize(NormalizationForm.FormKD);
        var builder = new StringBuilder(normalized.Length);
        var depth = 0;

        foreach (var ch in normalized)
        {
            // 去掉组合用记号（音标等），让 café 和 cafe 能对上
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (ch is '(' or '[' or '（' or '【' or '{' or '〔' or '〖')
            {
                depth++;
                continue;
            }

            if (ch is ')' or ']' or '）' or '】' or '}' or '〕' or '〗')
            {
                if (depth > 0)
                {
                    depth--;
                }

                continue;
            }

            // 括号里的补充说明整段丢掉：常见的是 (Live)、(Remix)、(Explicit)、【官方版】
            if (depth > 0)
            {
                continue;
            }

            // 去掉所有标点与符号，只保留字母、数字和汉字
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category is UnicodeCategory.ConnectorPunctuation
                or UnicodeCategory.DashPunctuation
                or UnicodeCategory.OpenPunctuation
                or UnicodeCategory.ClosePunctuation
                or UnicodeCategory.InitialQuotePunctuation
                or UnicodeCategory.FinalQuotePunctuation
                or UnicodeCategory.OtherPunctuation
                or UnicodeCategory.MathSymbol
                or UnicodeCategory.CurrencySymbol
                or UnicodeCategory.ModifierSymbol
                or UnicodeCategory.OtherSymbol)
            {
                builder.Append(' ');
                continue;
            }

            builder.Append(char.ToLowerInvariant(ch));
        }

        // 压缩连续空白
        var result = builder.ToString();
        var compact = new StringBuilder(result.Length);
        var lastWasSpace = true;

        foreach (var ch in result)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!lastWasSpace)
                {
                    compact.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            compact.Append(ch);
            lastWasSpace = false;
        }

        return compact.ToString().Trim();
    }

    /// <summary>去掉开头的 "feat." / "ft." 段。歌手字段里常见这类写法。</summary>
    public static string StripFeatured(string normalized)
    {
        if (string.IsNullOrEmpty(normalized))
        {
            return normalized;
        }

        var index = normalized.IndexOf("feat", StringComparison.Ordinal);
        if (index < 0)
        {
            index = normalized.IndexOf(" ft ", StringComparison.Ordinal);
        }

        return index > 0 ? normalized[..index].Trim() : normalized;
    }

    /// <summary>
    /// 两个已归一化字符串的相似度，0 ~ 1。
    ///
    /// 用字符二元组（bigram）而不是按词切分：中文曲名没有空格，
    /// 按词切分会把整句话当成一个"词"，那样几乎什么都匹配不上。
    /// </summary>
    public static double Similarity(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b))
        {
            return 1.0;
        }

        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return 0.0;
        }

        if (string.Equals(a, b, StringComparison.Ordinal))
        {
            return 1.0;
        }

        // 一个完整包含另一个：给很高的分而不是满分。
        // 例如搜索时用 "起风了"，候选是 "起风了 买辣椒也用券"（标题里混进了歌手）。
        // 这种情况应该匹配上，但不该和完全相等一样确定。
        if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
        {
            var shorter = Math.Min(a.Length, b.Length);
            var longer = Math.Max(a.Length, b.Length);
            return 0.85 + (0.1 * shorter / longer);
        }

        var setA = Bigrams(a);
        var setB = Bigrams(b);

        // 只有一个字符时凑不出二元组，退回单字符比较
        if (setA.Count == 0 || setB.Count == 0)
        {
            return 0.0;
        }

        var intersection = 0;
        foreach (var gram in setA)
        {
            if (setB.Contains(gram))
            {
                intersection++;
            }
        }

        // Dice 系数的多重集版本：2 * 交集 / 总数
        return 2.0 * intersection / (setA.Count + setB.Count);
    }

    private static Dictionary<string, int> Bigrams(string value)
    {
        var grams = new Dictionary<string, int>(StringComparer.Ordinal);

        // 空格去掉再取二元组，避免 "last stardust" 和 "laststardust" 产生不同结果
        var compact = value.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (compact.Length < 2)
        {
            return grams;
        }

        for (var i = 0; i < compact.Length - 1; i++)
        {
            var gram = compact.Substring(i, 2);
            grams[gram] = grams.TryGetValue(gram, out var count) ? count + 1 : 1;
        }

        return grams;
    }
}
