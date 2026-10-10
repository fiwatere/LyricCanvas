namespace LyricCanvas.Core;

/// <summary>
/// 一个候选与查询的匹配得分。
/// </summary>
/// <param name="Candidate">候选本身。</param>
/// <param name="Score">综合得分，0 ~ 1，越大越像。</param>
public sealed record ScoredCandidate(LyricCandidate Candidate, double Score);

/// <summary>
/// 匹配结果。
/// </summary>
/// <param name="Best">得分最高的候选；没有任何候选够格时为 null。</param>
/// <param name="Alternatives">其他够格的候选，按得分降序。用于"匹配不确定时让用户选"。</param>
/// <param name="Ambiguous">
/// 是否"难以确定"：最高分和次高分太接近，说明可能是同名不同版本。
/// 界面可以据此提示用户确认。
/// </param>
public sealed record LyricMatchResult(
    ScoredCandidate? Best,
    IReadOnlyList<ScoredCandidate> Alternatives,
    bool Ambiguous)
{
    /// <summary>没匹配到任何东西。</summary>
    public static LyricMatchResult None { get; } = new(null, Array.Empty<ScoredCandidate>(), false);

    /// <summary>是否匹配成功。</summary>
    public bool HasMatch => Best is not null;

    /// <summary>最合适的候选（没匹配到时为 null）。</summary>
    public LyricCandidate? BestCandidate => Best?.Candidate;
}
