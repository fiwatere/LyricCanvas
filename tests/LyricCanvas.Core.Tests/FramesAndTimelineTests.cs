using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 取模分配与歌词时间轴的单元测试。
///
/// 这两块是多框方案的核心：
///   - 分配错了，歌词会出现在错误的框里；
///   - 时间轴算错了，淡出时机就不对，最后一句可能永远不消失。
/// </summary>
public class FramesAndTimelineTests
{
    private static FramesConfig WithFrames(int count)
    {
        var config = new FramesConfig();
        for (var i = 0; i < count; i++)
        {
            config.Frames.Add(new FrameConfig { Name = $"框{i + 1}" });
        }

        return config;
    }

    // ---------------------------------------------------------------
    // 取模分配
    // ---------------------------------------------------------------

    [Fact]
    public void 两个框时交替分配单双数句()
    {
        var config = WithFrames(2);

        // 用户要的正是这个：框1 说 1/3/5，框2 说 2/4/6
        Assert.Equal("框1", config.GetFrameForLine(0)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(1)!.Name);
        Assert.Equal("框1", config.GetFrameForLine(2)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(3)!.Name);
        Assert.Equal("框1", config.GetFrameForLine(4)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(5)!.Name);
    }

    [Fact]
    public void 三个框时循环周期变成三()
    {
        var config = WithFrames(3);

        Assert.Equal("框1", config.GetFrameForLine(0)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(1)!.Name);
        Assert.Equal("框3", config.GetFrameForLine(2)!.Name);
        Assert.Equal("框1", config.GetFrameForLine(3)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(4)!.Name);
        Assert.Equal("框3", config.GetFrameForLine(5)!.Name);
    }

    [Fact]
    public void 五个框时也能正确循环()
    {
        var config = WithFrames(5);

        for (var i = 0; i < 20; i++)
        {
            Assert.Equal($"框{(i % 5) + 1}", config.GetFrameForLine(i)!.Name);
        }
    }

    [Fact]
    public void 句数少于框数时多出来的框不参与分配()
    {
        var config = WithFrames(5);

        // 只有 3 句歌词，那么只会用到前 3 个框，框 4、5 闲着
        Assert.Equal("框1", config.GetFrameForLine(0)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(1)!.Name);
        Assert.Equal("框3", config.GetFrameForLine(2)!.Name);
    }

    [Fact]
    public void 停用的框不参与分配()
    {
        var config = WithFrames(3);
        config.Frames[1].Enabled = false;   // 停用中间那个

        // 只剩框1、框3 参与，周期变成 2。
        // 这里正是那个"返回下标会错位"的坑：停用中间那个之后，
        // 启用列表的第 1 个其实是原始列表的第 3 个
        Assert.Equal("框1", config.GetFrameForLine(0)!.Name);
        Assert.Equal("框3", config.GetFrameForLine(1)!.Name);
        Assert.Equal("框1", config.GetFrameForLine(2)!.Name);
        Assert.Equal("框3", config.GetFrameForLine(3)!.Name);
    }

    [Fact]
    public void 一个框都没启用时返回空()
    {
        var config = WithFrames(2);
        config.Frames[0].Enabled = false;
        config.Frames[1].Enabled = false;

        Assert.Null(config.GetFrameForLine(0));
    }

    [Fact]
    public void 没有框时返回空而不是崩溃()
    {
        var config = new FramesConfig();

        Assert.Null(config.GetFrameForLine(0));
    }

    [Fact]
    public void 负数行号返回空()
    {
        var config = WithFrames(2);

        Assert.Null(config.GetFrameForLine(-1));
    }

    [Fact]
    public void 默认创建两个位置错开的框()
    {
        var frames = FramesConfig.CreateDefaultFrames(1920, 1080);

        Assert.Equal(2, frames.Count);
        Assert.True(frames[0].Top < frames[1].Top, "两个默认框应该一上一下，不要完全重叠");
        Assert.All(frames, f => Assert.True(f.Locked, "默认应该是穿透的"));
        Assert.All(frames, f => Assert.True(f.Width > 0 && f.Height > 0));
    }

    // ---------------------------------------------------------------
    // 歌词时间轴
    // ---------------------------------------------------------------

    private static LrcDocument Doc(params (double Seconds, string Text)[] lines)
    {
        var text = string.Join("\n", lines.Select(l =>
            $"[{TimeSpan.FromSeconds(l.Seconds):mm\\:ss\\.ff}]{l.Text}"));
        return LrcParser.Parse(text);
    }

    [Fact]
    public void 每句的结束时间等于下一句的开始时间()
    {
        var doc = Doc((1, "第一句"), (5, "第二句"), (10, "第三句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(60));

        Assert.Equal(3, timings.Count);
        Assert.Equal(TimeSpan.FromSeconds(1), timings[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(5), timings[0].End);
        Assert.Equal(TimeSpan.FromSeconds(5), timings[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(10), timings[1].End);
    }

    [Fact]
    public void 最后一句用歌曲总时长收尾()
    {
        var doc = Doc((1, "第一句"), (5, "最后一句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(120));

        Assert.Equal(TimeSpan.FromSeconds(120), timings[1].End);
        Assert.Equal(TimeSpan.FromSeconds(115), timings[1].Duration);
    }

    [Fact]
    public void 拿不到总时长时最后一句有保底时长()
    {
        var doc = Doc((1, "第一句"), (5, "最后一句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.Zero);

        Assert.True(timings[1].End > timings[1].Start, "最后一句必须有结束时间，不能永远挂着");
        Assert.True(timings[1].Duration >= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void 两个时间戳相同时不会产生零长度区间()
    {
        // 脏数据：两句时间戳完全一样
        var doc = LrcParser.Parse("[00:05.00]甲\n[00:05.00]乙");
        var timings = LyricTimeline.Build(doc, TimeSpan.Zero);

        Assert.Equal(2, timings.Count);
        Assert.All(timings, t => Assert.True(t.Duration > TimeSpan.Zero, "零长度区间会导致这一句永远看不到"));
    }

    [Fact]
    public void 空歌词返回空序列()
    {
        Assert.Empty(LyricTimeline.Build(LrcDocument.Empty, TimeSpan.FromSeconds(100)));
    }

    [Fact]
    public void 查找某时刻正在显示的那句()
    {
        var doc = Doc((1, "第一句"), (5, "第二句"), (10, "第三句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(20));

        Assert.Equal("第一句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(1))!.Text);
        Assert.Equal("第一句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(4.9))!.Text);
        Assert.Equal("第二句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(5))!.Text);
        Assert.Equal("第三句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(15))!.Text);
    }

    [Fact]
    public void 还没到第一句时返回空()
    {
        var doc = Doc((5, "第一句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(20));

        Assert.Null(LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void 超过最后一句的结束时间后返回空()
    {
        // 最后一句 5 秒开始、20 秒结束，21 秒时不该还有内容
        var doc = Doc((1, "第一句"), (5, "最后一句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(20));

        Assert.Null(LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(21)));
    }

    [Fact]
    public void 句与句之间的空隙不显示任何内容()
    {
        // 手工造一个有间隙的区间序列：第一句 1~2 秒，第二句 5~6 秒
        var timings = new[]
        {
            new LyricLineTiming(0, "第一句", TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)),
            new LyricLineTiming(1, "第二句", TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(6)),
        };

        Assert.NotNull(LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(1.5)));
        Assert.Null(LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(3)));
        Assert.NotNull(LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(5.5)));
    }

    [Fact]
    public void 时间去查表在大量行上也正确()
    {
        var lines = Enumerable.Range(0, 300)
            .Select(i => ((double)i, $"第{i}句"))
            .ToArray();
        var doc = Doc(lines);
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(600));

        Assert.Equal(300, timings.Count);

        // 每句的区间是 [i, i+1)，抽查几个位置
        Assert.Equal("第0句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(0.5))!.Text);
        Assert.Equal("第150句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(150.5))!.Text);
        Assert.Equal("第299句", LyricTimeline.FindAt(timings, TimeSpan.FromSeconds(299.5))!.Text);
    }

    // ---------------------------------------------------------------
    // 分配与时间轴配合
    // ---------------------------------------------------------------

    [Fact]
    public void 两句歌词分到两个不同的框()
    {
        var config = WithFrames(2);
        var doc = Doc((1, "第一句"), (5, "第二句"));
        var timings = LyricTimeline.Build(doc, TimeSpan.FromSeconds(30));

        Assert.Equal("框1", config.GetFrameForLine(timings[0].Index)!.Name);
        Assert.Equal("框2", config.GetFrameForLine(timings[1].Index)!.Name);
    }
    /// <summary>
    /// Clone 必须保留**所有**样式字段。
    ///
    /// 为什么专门测这个：AppConfig.Clone 是手工逐个字段复制的，
    /// 加新样式字段时很容易漏掉——漏了之后症状是"设置里改了没反应"，
    /// 而且极难排查（真值在配置里是对的，只是克隆时丢了）。
    /// 这个 bug 真实发生过：LetterSpacing 漏了，导致字间距永远不生效。
    /// </summary>
    [Fact]
    public void Clone_保留所有样式字段()
    {
        var original = new AppConfig();
        original.Frames.FontSize = 33;
        original.Frames.FontFamily = "SimSun";
        original.Frames.TextColor = "#FF112233";
        original.Frames.SubTextColor = "#FF445566";
        original.Frames.Opacity = 0.42;
        original.Frames.ShowTrackInfo = false;
        original.Frames.FadeInMs = 111;
        original.Frames.FadeOutMs = 222;
        original.Frames.LetterSpacing = 7.5;
        original.Frames.HoldMs = 333;

        var copy = original.Clone();

        Assert.Equal(33, copy.Frames.FontSize);
        Assert.Equal("SimSun", copy.Frames.FontFamily);
        Assert.Equal("#FF112233", copy.Frames.TextColor);
        Assert.Equal("#FF445566", copy.Frames.SubTextColor);
        Assert.Equal(0.42, copy.Frames.Opacity);
        Assert.False(copy.Frames.ShowTrackInfo);
        Assert.Equal(111, copy.Frames.FadeInMs);
        Assert.Equal(222, copy.Frames.FadeOutMs);
        Assert.Equal(7.5, copy.Frames.LetterSpacing);
        Assert.Equal(333, copy.Frames.HoldMs);
    }

    /// <summary>
    /// FramesConfig.Clone 同样要保留所有字段（它和 AppConfig.Clone 是两处独立的复制）。
    /// </summary>
    [Fact]
    public void FramesConfig_Clone_保留所有样式字段()
    {
        var style = new FramesConfig
        {
            FontSize = 28,
            FontFamily = "KaiTi",
            TextColor = "#FFAABBCC",
            SubTextColor = "#FFDDEEFF",
            Opacity = 0.7,
            ShowTrackInfo = false,
            FadeInMs = 11,
            FadeOutMs = 22,
            LetterSpacing = 3.25,
            HoldMs = 44,
        };

        var copy = style.Clone();

        Assert.Equal(28, copy.FontSize);
        Assert.Equal("KaiTi", copy.FontFamily);
        Assert.Equal("#FFAABBCC", copy.TextColor);
        Assert.Equal("#FFDDEEFF", copy.SubTextColor);
        Assert.Equal(0.7, copy.Opacity);
        Assert.False(copy.ShowTrackInfo);
        Assert.Equal(11, copy.FadeInMs);
        Assert.Equal(22, copy.FadeOutMs);
        Assert.Equal(3.25, copy.LetterSpacing);
        Assert.Equal(44, copy.HoldMs);
    }
}
