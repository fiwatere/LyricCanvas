using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 歌词候选匹配的单元测试。
///
/// 这些用例针对的都是真实会遇到的情况：搜索结果里混着 Live 版、翻唱、
/// 同名不同歌、伴奏等等。匹配错了比没匹配到更糟——用户会以为程序坏了。
/// </summary>
public class LyricsMatcherTests
{
    private static LyricCandidate Candidate(
        string title,
        string artist = "",
        int durationSeconds = 0,
        string source = "test",
        string lyrics = "[00:01.00]测试歌词")
        => new(source, title, artist, string.Empty, TimeSpan.FromSeconds(durationSeconds), lyrics);

    // ---------------------------------------------------------------
    // 基本挑选
    // ---------------------------------------------------------------

    [Fact]
    public void 完全一致的候选被选中()
    {
        var candidates = new[]
        {
            Candidate("完全不相干的歌", "别的歌手", 200),
            Candidate("LAST STARDUST", "Aimer", 318),
            Candidate("另一首歌", "另一个歌手", 250),
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(318), candidates);

        Assert.True(result.HasMatch);
        Assert.Equal("LAST STARDUST", result.BestCandidate!.Title);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void 曲名对应的候选排在前面时不会误选后面的()
    {
        var candidates = new[]
        {
            Candidate("起风了", "买辣椒也用券", 325),
            Candidate("晴天", "周杰伦", 269),
        };

        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), candidates);

        Assert.Equal("起风了", result.BestCandidate!.Title);
    }

    // ---------------------------------------------------------------
    // 版本区分 —— 这是最容易出错的地方
    // ---------------------------------------------------------------

    [Fact]
    public void 优先选原版而不是Live版()
    {
        // 原版 318 秒，Live 版 425 秒，用时长把它们区分开
        var candidates = new[]
        {
            Candidate("LAST STARDUST (Live)", "Aimer", 425),
            Candidate("LAST STARDUST", "Aimer", 318),
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(318), candidates);

        Assert.Equal("LAST STARDUST", result.BestCandidate!.Title);
    }

    [Fact]
    public void 归一化后同名时用时长决定()
    {
        // 两个候选归一化后都是 "last stardust"，只能靠时长区分
        var candidates = new[]
        {
            Candidate("LAST STARDUST", "Aimer", 425),   // 这场其实是 Live
            Candidate("LAST STARDUST", "Aimer", 318),   // 这场是原版
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(318), candidates);

        Assert.Equal(318, result.BestCandidate!.Duration.TotalSeconds);
    }

    [Fact]
    public void 曲名带Live标记但目标也是Live版时能匹配上()
    {
        var candidates = new[]
        {
            Candidate("LAST STARDUST (Live)", "Aimer", 425),
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(425), candidates);

        Assert.True(result.HasMatch);
        Assert.Equal(425, result.BestCandidate!.Duration.TotalSeconds);
    }

    // ---------------------------------------------------------------
    // 歌手缺失与多歌手的处理
    // ---------------------------------------------------------------

    [Fact]
    public void 候选没提供歌手时曲名一致仍能匹配()
    {
        var candidates = new[] { Candidate("起风了", string.Empty, 325) };

        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), candidates);

        Assert.True(result.HasMatch);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void 目标有多个歌手而候选只给一个时仍能匹配()
    {
        // SMTC 常给出 "Aimer, 澤野弘之" 这种，而歌词来源只写主要艺人
        var candidates = new[] { Candidate("LAST STARDUST", "Aimer", 318) };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer, 澤野弘之", TimeSpan.FromSeconds(318), candidates);

        Assert.True(result.HasMatch);
        Assert.True(result.Best!.Score > 0.85, $"分数应该很高，实际 {result.Best.Score}");
    }

    [Fact]
    public void 歌手写法带feat时不影响匹配()
    {
        var candidates = new[] { Candidate("歌名", "周杰伦", 240) };

        var result = LyricsMatcher.Match("歌名", "周杰伦 feat. 阿信", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
    }

    // ---------------------------------------------------------------
    // 拒绝错误匹配 —— 宁可没有，也不要给错歌
    // ---------------------------------------------------------------

    [Fact]
    public void 曲名完全对不上时拒绝采用()
    {
        var candidates = new[]
        {
            Candidate("晴天", "周杰伦", 269),
            Candidate("七里香", "周杰伦", 299),
        };

        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), candidates);

        Assert.False(result.HasMatch);
        Assert.Null(result.BestCandidate);
    }

    [Fact]
    public void 候选为空时返回未匹配()
    {
        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), Array.Empty<LyricCandidate>());

        Assert.False(result.HasMatch);
    }

    [Fact]
    public void 候选集合为null时返回未匹配()
    {
        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), null);

        Assert.False(result.HasMatch);
    }

    [Fact]
    public void 目标曲名为空时返回未匹配()
    {
        var candidates = new[] { Candidate("起风了", "买辣椒也用券", 325) };

        Assert.False(LyricsMatcher.Match(null, "某歌手", TimeSpan.FromSeconds(325), candidates).HasMatch);
        Assert.False(LyricsMatcher.Match("   ", "某歌手", TimeSpan.FromSeconds(325), candidates).HasMatch);
    }

    [Fact]
    public void 曲名相似但时长差太多时分数被拉低()
    {
        // 曲名一样但时长差 100 秒，很可能是完全不同的作品（比如同名纯音乐）
        var candidates = new[] { Candidate("同名歌曲", "某歌手", 400) };

        var result = LyricMatchResult.None;
        var withDuration = LyricsMatcher.Match("同名歌曲", "某歌手", TimeSpan.FromSeconds(200), candidates);
        result = withDuration;

        // 曲名满分 + 歌手满分 + 时长零分，加权后仍会高于阈值，
        // 但一定低于时长也吻合的情况
        var withoutDuration = LyricsMatcher.Match("同名歌曲", "某歌手", TimeSpan.Zero, candidates);

        Assert.True(withoutDuration.Best!.Score > result.Best!.Score,
            "时长差很大时，得分应该低于完全不看时长的情况");
    }

    // ---------------------------------------------------------------
    // 歧义标记
    // ---------------------------------------------------------------

    [Fact]
    public void 曲名吻合但歌手完全不同时标记为不确定()
    {
        // 这是一类很值得提醒用户的情况：曲名完全一样但歌手是别人，
        // 可能是翻唱，也可能是重名歌。光看曲名和时长分不出来，
        // 默默显示一首可能不对的歌的歌词，比问一句糟糕得多。
        var candidates = new[]
        {
            Candidate("同名歌", "另一个歌手", 240),
        };

        var result = LyricsMatcher.Match("同名歌", "原唱歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.True(result.Ambiguous, "曲名吻合但歌手对不上时应该请用户确认");
    }

    [Fact]
    public void 来源不提供歌手时不标记为不确定()
    {
        // 很多歌词来源就是不写歌手，这是常态，不该当成"歌手对不上"
        var candidates = new[] { Candidate("同名歌", string.Empty, 240) };

        var result = LyricsMatcher.Match("同名歌", "原唱歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.False(result.Ambiguous, "来源没给歌手时不该提示冲突");
    }

    [Fact]
    public void 歌手吻合时不标记为不确定()
    {
        var candidates = new[] { Candidate("同名歌", "原唱歌手", 240) };

        var result = LyricsMatcher.Match("同名歌", "原唱歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void 两个候选分数接近时标记为不确定()
    {
        // 曲名一样，两个候选各有各的道理，分数咬得很近
        var candidates = new[]
        {
            Candidate("同名歌", "另一个歌手", 240, "来源A"),
            Candidate("同名歌", "第三个歌手", 241, "来源B"),
        };

        var result = LyricsMatcher.Match("同名歌", "原唱歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.True(result.Ambiguous);
        Assert.True(result.Alternatives.Count >= 2);
    }

    [Fact]
    public void 两个候选完全一样时按歌手冲突规则处理()
    {
        // 归一化后曲名歌手时长全都一样，只是来源不同 —— 重复项。
        // 但如果歌手和目标也对不上，仍然该让用户确认（这是歌手冲突规则在起作用）
        var candidates = new[]
        {
            Candidate("同名歌", "同一歌手", 240, "来源A"),
            Candidate("同名歌", "同一歌手", 240, "来源B"),
        };

        // 目标歌手和候选一致 -> 不该提示
        var matching = LyricsMatcher.Match("同名歌", "同一歌手", TimeSpan.FromSeconds(240), candidates);
        Assert.True(matching.HasMatch);
        Assert.False(matching.Ambiguous, "歌手也吻合时不该标记为不确定");

        // 目标歌手和候选完全不同 -> 歌手冲突规则应该触发提示
        var conflicting = LyricsMatcher.Match("同名歌", "完全不同的歌手", TimeSpan.FromSeconds(240), candidates);
        Assert.True(conflicting.HasMatch);
        Assert.True(conflicting.Ambiguous, "歌手对不上时应该提示");
    }

    [Fact]
    public void 重复搜索条目不会挤掉分数接近的不同版本()
    {
        var candidates = Enumerable.Range(0, 5)
            .Select(index => Candidate("同名歌", "同一歌手", 240, $"重复来源{index}"))
            .Append(Candidate("同名歌", "同一歌手", 243, "不同版本"))
            .ToArray();

        var result = LyricsMatcher.Match("同名歌", "同一歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.Same(candidates[0], result.BestCandidate);
        Assert.Equal(2, result.Alternatives.Count);
        Assert.Equal(243, result.Alternatives[1].Candidate.Duration.TotalSeconds);
        Assert.True(result.Ambiguous, "重复条目不能掩盖真正接近的另一个版本");
    }

    [Fact]
    public void 最高分明显领先时不标记为不确定()
    {
        var candidates = new[]
        {
            Candidate("LAST STARDUST", "Aimer", 318),
            Candidate("完全不同的歌名", "别的歌手", 200),
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(318), candidates);

        Assert.False(result.Ambiguous);
    }

    [Fact]
    public void 备选项按分数降序排列且不超过上限()
    {
        var candidates = Enumerable.Range(0, 20)
            .Select(i => Candidate($"同名歌 第{i}版", "同一歌手", 240))
            .ToArray();

        var result = LyricsMatcher.Match("同名歌", "同一歌手", TimeSpan.FromSeconds(240), candidates);

        Assert.True(result.HasMatch);
        Assert.True(result.Alternatives.Count <= 5, $"备选项不该超过 5 个，实际 {result.Alternatives.Count}");

        for (var i = 1; i < result.Alternatives.Count; i++)
        {
            Assert.True(result.Alternatives[i - 1].Score >= result.Alternatives[i].Score,
                "备选项必须按分数降序");
        }
    }

    // ---------------------------------------------------------------
    // 真实场景
    // ---------------------------------------------------------------

    [Fact]
    public void 真实场景_酷狗搜索结果的典型形态()
    {
        // 模拟酷狗返回：文件名是 "歌手 - 曲名" 的形式
        var candidates = new[]
        {
            Candidate("Aimer - LAST STARDUST", "Aimer", 318, "kugou", "[00:25.72]ふりしきる強い雨"),
            Candidate("Aimer - LAST STARDUST (Live)", "Aimer", 425, "kugou"),
        };

        var result = LyricsMatcher.Match("LAST STARDUST", "Aimer", TimeSpan.FromSeconds(318), candidates);

        Assert.True(result.HasMatch);
        Assert.Equal(318, result.BestCandidate!.Duration.TotalSeconds);
    }

    [Fact]
    public void 真实场景_SMTC给出的曲名带括号说明()
    {
        // 播放器有时会把版本说明写在曲名里
        var candidates = new[] { Candidate("起风了", "买辣椒也用券", 325) };

        var result = LyricsMatcher.Match("起风了 (Live)", "买辣椒也用券", TimeSpan.FromSeconds(325), candidates);

        Assert.True(result.HasMatch);
    }

    [Fact]
    public void 真实场景_候选曲名里混进了歌手名()
    {
        var candidates = new[] { Candidate("起风了 买辣椒也用券", "买辣椒也用券", 325) };

        var result = LyricsMatcher.Match("起风了", "买辣椒也用券", TimeSpan.FromSeconds(325), candidates);

        Assert.True(result.HasMatch);
    }
}
