using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 曲名/歌手归一化的单元测试。
/// </summary>
public class LyricsTextTests
{
    [Theory]
    [InlineData("LAST STARDUST", "last stardust")]
    [InlineData("Last Stardust (Live)", "last stardust")]
    [InlineData("【官方】LAST STARDUST", "last stardust")]
    [InlineData("起风了 (Live)", "起风了")]
    [InlineData("起风了【高音质】", "起风了")]
    [InlineData("Aimer - LAST STARDUST", "aimer last stardust")]
    public void 归一化会去掉括号补充和标点(string input, string expected)
    {
        Assert.Equal(expected, LyricsText.Normalize(input));
    }

    [Fact]
    public void 归一化统一全角与半角()
    {
        // 全角字母数字要能对上半角
        Assert.Equal(LyricsText.Normalize("ABC123"), LyricsText.Normalize("ＡＢＣ１２３"));
    }

    [Fact]
    public void 归一化去掉音标()
    {
        Assert.Equal("cafe", LyricsText.Normalize("café"));
    }

    [Fact]
    public void 归一化处理中文标点()
    {
        Assert.Equal("起风了", LyricsText.Normalize("《起风了》"));
        Assert.Equal("晴天", LyricsText.Normalize("晴天。"));
    }

    [Fact]
    public void 归一化压缩空白()
    {
        Assert.Equal("hello world", LyricsText.Normalize("  hello    world  "));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 空输入归一化成空字符串(string? input)
    {
        Assert.Equal(string.Empty, LyricsText.Normalize(input));
    }

    [Fact]
    public void 归一化后是纯装饰符号时返回空()
    {
        Assert.Equal(string.Empty, LyricsText.Normalize("!!!???"));
    }

    // ---------------------------------------------------------------
    // 相似度
    // ---------------------------------------------------------------

    [Fact]
    public void 完全相同的字符串相似度为1()
    {
        Assert.Equal(1.0, LyricsText.Similarity("起风了", "起风了"));
    }

    [Fact]
    public void 包含关系给出高分但不是满分()
    {
        var score = LyricsText.Similarity("起风了", "起风了 买辣椒也用券");

        Assert.True(score > 0.85, $"包含关系应该给高分，实际 {score}");
        Assert.True(score < 1.0, "包含关系不该给满分");
    }

    [Fact]
    public void 中文曲名用字符二元组能匹配上部分相同的()
    {
        // 这是选二元组而不是按词切分的原因：中文没有空格，
        // 按词切分会把整句当一个词，这种部分相同的情况就匹配不上
        var score = LyricsText.Similarity("起风了", "起风");

        Assert.True(score > 0.5, $"部分相同应该有可观的分数，实际 {score}");
    }

    [Fact]
    public void 完全不同的字符串相似度很低()
    {
        var score = LyricsText.Similarity("起风了", "晴天");

        Assert.True(score < 0.3, $"完全不同的曲名不该得高分，实际 {score}");
    }

    [Fact]
    public void 拉丁字母曲名忽略空格差异()
    {
        Assert.Equal(1.0, LyricsText.Similarity("laststardust", "last stardust"));
    }

    [Fact]
    public void 一个为空时相似度为0()
    {
        Assert.Equal(0.0, LyricsText.Similarity("起风了", ""));
        Assert.Equal(0.0, LyricsText.Similarity("", "起风了"));
    }

    [Fact]
    public void 两个都为空时相似度为1()
    {
        Assert.Equal(1.0, LyricsText.Similarity("", ""));
    }

    // ---------------------------------------------------------------
    // feat. 处理
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("周杰伦 feat. 阿信", "周杰伦")]
    [InlineData("artist ft other", "artist")]
    [InlineData("周杰伦", "周杰伦")]
    public void 去掉feat段(string input, string expected)
    {
        Assert.Equal(expected, LyricsText.StripFeatured(LyricsText.Normalize(input)));
    }
}
