using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace LyricCanvas.Core;

/// <summary>
/// 从歌词接口返回的一堆候选里，挑出真正对应的那一首。
///
/// 为什么这件事值得单独写一个类并配单元测试：
/// 搜索结果里经常混着同名不同版本（原版 / Live / Remix / 翻唱 / 伴奏），
/// 直接拿第一条结果用，就会出现"放的是 A 却显示 B 的歌词"。
/// 这种错误比"没有歌词"更糟——用户会以为程序坏了，而且很难自己想明白原因。
///
/// 打分模型：
///   总分 = 0.6 × 曲名相似度 + 0.3 × 歌手相似度 + 0.1 × 时长接近度
///
/// 但曲名是**必要条件**：曲名不像的候选，无论歌手和时长多吻合都会被压到及格线以下。
/// 反过来，如果曲名非常像，歌手缺失也不至于一票否决（很多来源不提供歌手）。
/// </summary>
public static class LyricsMatcher
{
    /// <summary>低于这个总分就不采用，宁可显示"没找到"也不要匹配错歌。</summary>
    public const double MinimumScore = 0.55;

    /// <summary>曲名相似度低于这个值时，认为候选不可信（这是一道硬门槛）。</summary>
    private const double MinimumTitleSimilarity = 0.45;

    /// <summary>曲名很像时，对歌手的依赖就降低（用这个分数作为分界）。</summary>
    private const double StrongTitleThreshold = 0.90;

    /// <summary>两个候选分数差小于这个值，就认为"不好确定"。</summary>
    private const double AmbiguityGap = 0.06;

    /// <summary>
    /// 歌手相似度低于这个值，视为"歌手对不上"（翻唱、重名歌、来源写错）。
    /// </summary>
    private const double ArtistConflictThreshold = 0.30;

    /// <summary>
    /// 只有当总分不低于这个值时，"歌手对不上"才值得提醒用户确认。
    /// 总分本来就低的话，压根不会被采用，不必再提示。
    /// </summary>
    private const double ArtistConflictScore = 0.60;

    /// <summary>时长差在这个范围内视为同一首歌（不同来源的时长标注常有几秒出入）。</summary>
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(2);

    /// <summary>时长差超过这个值就认为不是同一首（比如一个是 3 分钟一个是 5 分钟）。</summary>
    private static readonly TimeSpan DurationLimit = TimeSpan.FromSeconds(15);

    private const int MaxAlternatives = 5;

    /// <summary>生成可跨进程复现的候选标识，供用户拒绝某个低置信匹配后排除它。</summary>
    public static string GetCandidateKey(LyricCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            LyricsText.Normalize(candidate.Source),
            LyricsText.Normalize(candidate.Title),
            LyricsText.Normalize(candidate.Artist),
            candidate.Duration.Ticks.ToString(CultureInfo.InvariantCulture),
            candidate.SourceId ?? string.Empty,
        });
        return Convert.ToHexString(SHA256.HashData(identity));
    }

    private sealed record CandidateScore(
        ScoredCandidate Scored,
        string NormalizedTitle,
        string NormalizedArtist,
        double ArtistSimilarity,
        bool HasArtist);

    /// <summary>
    /// 在候选里挑最合适的一首。
    /// </summary>
    /// <param name="title">目标曲名（来自 SMTC，未归一化）。</param>
    /// <param name="artist">目标歌手（可能为空）。</param>
    /// <param name="duration">目标时长（可能为 0，表示拿不到）。</param>
    /// <param name="candidates">待筛选的候选。</param>
    public static LyricMatchResult Match(
        string? title,
        string? artist,
        TimeSpan duration,
        IEnumerable<LyricCandidate>? candidates)
    {
        if (candidates is null)
        {
            return LyricMatchResult.None;
        }

        var targetTitle = LyricsText.Normalize(title);
        if (targetTitle.Length == 0)
        {
            return LyricMatchResult.None;
        }

        var targetArtist = LyricsText.StripFeatured(LyricsText.Normalize(artist));
        var hasDuration = duration > TimeSpan.Zero;

        var scored = new List<CandidateScore>();

        foreach (var candidate in candidates)
        {
            if (candidate is null)
            {
                continue;
            }

            var candidateTitle = LyricsText.Normalize(candidate.Title);
            if (candidateTitle.Length == 0)
            {
                continue;
            }

            var titleSimilarity = LyricsText.Similarity(targetTitle, candidateTitle);

            // 曲名不像，直接淘汰。这一条是整个匹配质量的底线。
            if (titleSimilarity < MinimumTitleSimilarity)
            {
                continue;
            }

            var normalizedArtist = LyricsText.Normalize(candidate.Artist);
            var candidateArtist = LyricsText.StripFeatured(normalizedArtist);
            var artistSimilarity = GetArtistSimilarity(targetArtist, candidateArtist);

            // 曲名非常像的时候，歌手信息的权重降低：
            // 有些来源只给"主要艺人"，或者干脆不给歌手，不该因此否掉正确的歌词。
            double titleWeight, artistWeight;
            if (titleSimilarity >= StrongTitleThreshold)
            {
                titleWeight = 0.75;
                artistWeight = targetArtist.Length == 0 ? 0.0 : 0.15;
            }
            else
            {
                titleWeight = 0.60;
                artistWeight = targetArtist.Length == 0 ? 0.0 : 0.30;
            }

            var durationWeight = hasDuration && candidate.Duration > TimeSpan.Zero ? 0.10 : 0.0;
            var totalWeight = titleWeight + artistWeight + durationWeight;

            var score = (titleWeight * titleSimilarity) + (artistWeight * artistSimilarity);

            if (durationWeight > 0)
            {
                score += durationWeight * GetDurationSimilarity(duration, candidate.Duration);
            }

            score /= totalWeight;
            scored.Add(new CandidateScore(new ScoredCandidate(candidate, score), candidateTitle,
                normalizedArtist, artistSimilarity, candidateArtist.Length > 0));
        }

        if (scored.Count == 0)
        {
            return LyricMatchResult.None;
        }

        // 归一化与歌手相似度沿用评分时的结果，避免同一批候选重复计算。
        var ordered = scored.OrderByDescending(candidate => candidate.Scored.Score).ToList();

        var best = ordered[0].Scored;

        if (best.Score < MinimumScore)
        {
            return LyricMatchResult.None;
        }

        var distinctCandidates = new List<CandidateScore>(MaxAlternatives);
        foreach (var candidate in ordered)
        {
            if (candidate.Scored.Score < MinimumScore)
            {
                break;
            }

            if (distinctCandidates.Any(existing => IsDuplicate(existing, candidate)))
            {
                continue;
            }

            distinctCandidates.Add(candidate);
            if (distinctCandidates.Count == MaxAlternatives)
            {
                break;
            }
        }

        var alternatives = distinctCandidates.Select(candidate => candidate.Scored).ToList();

        // 什么时候该让用户确认？两种情况：
        //
        // 1) 前两名分数咬得很近 —— 典型的"同名不同版本"，程序无法分辨。
        //
        // 2) 曲名非常吻合，但歌手明显对不上 —— 要么是翻唱/重名歌，
        //    要么来源把歌手写成了别人。光凭曲名和时长分不出来，
        //    宁可问一句，也不要默默显示一首可能不对的歌的歌词。
        //    注意必须排除"候选压根没提供歌手"的情况，那不算冲突。
        var scoresAreClose = alternatives.Count > 1
                             && (best.Score - alternatives[1].Score) < AmbiguityGap;

        var artistConflicts = best.Score >= ArtistConflictScore
                              && targetArtist.Length > 0
                              && ordered[0].HasArtist
                              && ordered[0].ArtistSimilarity < ArtistConflictThreshold;

        var ambiguous = scoresAreClose || artistConflicts;

        return new LyricMatchResult(best, alternatives, ambiguous);
    }

    /// <summary>
    /// 两个候选是不是"同一份东西的重复项"。
    ///
    /// 判据是归一化后的曲名歌手都一样、时长也几乎一样。
    /// 这种情况多半是同一个来源返回了重复条目，或者是不同来源给出了同一份歌词，
    /// 用户选哪个都一样，没必要弹出来问。
    /// 反之，如果歌手或时长不同，那就是真的"同名不同版本"，必须让用户决定。
    /// </summary>
    private static bool IsDuplicate(CandidateScore a, CandidateScore b)
    {
        if (!string.Equals(a.NormalizedTitle, b.NormalizedTitle, StringComparison.Ordinal))
        {
            return false;
        }

        if (!string.Equals(a.NormalizedArtist, b.NormalizedArtist, StringComparison.Ordinal))
        {
            return false;
        }

        if (a.Scored.Candidate.Duration > TimeSpan.Zero && b.Scored.Candidate.Duration > TimeSpan.Zero)
        {
            return (a.Scored.Candidate.Duration - b.Scored.Candidate.Duration).Duration() <= DurationTolerance;
        }

        // 两边都没时长信息，无法区分
        return true;
    }

    /// <summary>
    /// 歌手相似度。
    ///
    /// 特殊处理"一个包含另一个"的情况：不同来源对多歌手的写法差别很大，
    /// 例如 SMTC 给 "Aimer, 澤野弘之" 而候选只写 "Aimer"。
    /// 按普通相似度算会低到 0.7 左右，把正确的候选拉下去；
    /// 这里给 0.9，因为"候选的歌手是目标歌手的子集"本身就是很强的证据。
    /// </summary>
    private static double GetArtistSimilarity(string target, string candidate)
    {
        if (target.Length == 0)
        {
            return 0.0;
        }

        if (candidate.Length == 0)
        {
            return 0.0;
        }

        if (target.Contains(candidate, StringComparison.Ordinal)
            || candidate.Contains(target, StringComparison.Ordinal))
        {
            return 0.9;
        }

        return LyricsText.Similarity(target, candidate);
    }

    /// <summary>
    /// 时长接近度，0 ~ 1。差 2 秒以内算完全一致，差 15 秒以上算完全不一致。
    /// </summary>
    private static double GetDurationSimilarity(TimeSpan target, TimeSpan candidate)
    {
        var difference = (target - candidate).Duration();

        if (difference <= DurationTolerance)
        {
            return 1.0;
        }

        if (difference >= DurationLimit)
        {
            return 0.0;
        }

        var ratio = (difference - DurationTolerance) / (DurationLimit - DurationTolerance);
        return 1.0 - ratio;
    }
}
