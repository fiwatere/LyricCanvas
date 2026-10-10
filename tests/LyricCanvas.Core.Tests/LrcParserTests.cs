using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// LRC 解析器的单元测试。
///
/// 这些用例覆盖了实际会遇到的各种歌词文件写法：不同精度的时间戳、
/// 一行挂多个时间戳、全角冒号、脏数据行、空文件等等。
/// 之所以先写测试再做同步功能，是因为解析是纯逻辑，错了很难在界面上看出来。
/// </summary>
public class LrcParserTests
{
    // ---------------------------------------------------------------
    // 基本时间戳格式
    // ---------------------------------------------------------------

    [Fact]
    public void 解析两位小数的标准时间戳()
    {
        var doc = LrcParser.Parse("[00:12.34]第一行");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(12.34), line.Time);
        Assert.Equal("第一行", line.Text);
    }

    [Fact]
    public void 解析三位小数的时间戳()
    {
        var doc = LrcParser.Parse("[01:23.456]毫秒三位");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(83.456), line.Time);
    }

    [Fact]
    public void 解析一位小数的时间戳()
    {
        var doc = LrcParser.Parse("[00:05.5]十分之一秒");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(5.5), line.Time);
    }

    [Fact]
    public void 解析没有小数部分的时间戳()
    {
        var doc = LrcParser.Parse("[00:07]整秒");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(7), line.Time);
    }

    [Fact]
    public void 分钟数超过六十依然有效()
    {
        // 长曲子会出现 [72:30] 这种写法
        var doc = LrcParser.Parse("[72:30.00]第七十二分钟");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromMinutes(72.5), line.Time);
    }

    [Fact]
    public void 解析全角冒号()
    {
        // 中文输入法下很容易打出全角冒号
        var doc = LrcParser.Parse("[00：12.34]全角冒号");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(TimeSpan.FromSeconds(12.34), line.Time);
    }

    [Fact]
    public void 解析时分秒三段的写法()
    {
        var doc = LrcParser.Parse("[01:02:03.000]一小时零两分零三秒");

        var line = Assert.Single(doc.Lines);
        Assert.Equal(new TimeSpan(1, 2, 3), line.Time);
    }

    // ---------------------------------------------------------------
    // 一行多个时间戳
    // ---------------------------------------------------------------

    [Fact]
    public void 一行多个时间戳会展开成多行()
    {
        var doc = LrcParser.Parse("[00:10.00][00:20.00][01:00.00]副歌");

        Assert.Equal(3, doc.Lines.Count);
        Assert.All(doc.Lines, l => Assert.Equal("副歌", l.Text));
        Assert.Equal(TimeSpan.FromSeconds(10), doc.Lines[0].Time);
        Assert.Equal(TimeSpan.FromSeconds(20), doc.Lines[1].Time);
        Assert.Equal(TimeSpan.FromSeconds(60), doc.Lines[2].Time);
    }

    // ---------------------------------------------------------------
    // 排序
    // ---------------------------------------------------------------

    [Fact]
    public void 打乱顺序的歌词会被排好序()
    {
        var doc = LrcParser.Parse("""
            [00:30.00]第三句
            [00:10.00]第一句
            [00:20.00]第二句
            """);

        Assert.Equal(new[] { "第一句", "第二句", "第三句" }, doc.Lines.Select(l => l.Text));
        Assert.True(doc.Lines[0].Time < doc.Lines[1].Time);
        Assert.True(doc.Lines[1].Time < doc.Lines[2].Time);
    }

    // ---------------------------------------------------------------
    // 元信息
    // ---------------------------------------------------------------

    [Fact]
    public void 解析元信息标签()
    {
        var doc = LrcParser.Parse("""
            [ti:测试歌曲]
            [ar:测试歌手]
            [al:测试专辑]
            [by:某网友]
            [00:01.00]正文
            """);

        Assert.Equal("测试歌曲", doc.Title);
        Assert.Equal("测试歌手", doc.Artist);
        Assert.Equal("测试专辑", doc.Album);
        Assert.Equal("某网友", doc.Metadata["by"]);
        Assert.Single(doc.Lines);
    }

    [Fact]
    public void 元信息键名大小写不敏感()
    {
        var doc = LrcParser.Parse("[TI:大写标题]\n[00:01.00]正文");

        Assert.Equal("大写标题", doc.Title);
    }

    [Fact]
    public void 未知的元信息标签也会保留()
    {
        var doc = LrcParser.Parse("[00:01.00]正文\n[mycustom:自定义值]");

        Assert.Equal("自定义值", doc.Metadata["mycustom"]);
    }

    // ---------------------------------------------------------------
    // offset 偏移
    // ---------------------------------------------------------------

    [Fact]
    public void offset为正时歌词提前()
    {
        // 约定：正的 offset 表示歌词整体提前显示，所以时间戳变小
        var doc = LrcParser.Parse("[offset:500]\n[00:10.00]正文");

        Assert.Equal(TimeSpan.FromMilliseconds(500), doc.Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(9500), doc.Lines[0].Time);
    }

    [Fact]
    public void offset为负时歌词延后()
    {
        var doc = LrcParser.Parse("[offset:-500]\n[00:10.00]正文");

        Assert.Equal(TimeSpan.FromMilliseconds(-500), doc.Offset);
        Assert.Equal(TimeSpan.FromMilliseconds(10500), doc.Lines[0].Time);
    }

    [Fact]
    public void offset导致负数时归零()
    {
        var doc = LrcParser.Parse("[offset:5000]\n[00:01.00]正文");

        Assert.Equal(TimeSpan.Zero, doc.Lines[0].Time);
    }

    [Fact]
    public void 没有offset标签时偏移量为零()
    {
        var doc = LrcParser.Parse("[00:10.00]正文");

        Assert.Equal(TimeSpan.Zero, doc.Offset);
    }

    // ---------------------------------------------------------------
    // 脏数据与边界情况
    // ---------------------------------------------------------------

    [Fact]
    public void 空字符串返回空文档()
    {
        Assert.True(LrcParser.Parse(string.Empty).IsEmpty);
        Assert.True(LrcParser.Parse("   \r\n  ").IsEmpty);
    }

    [Fact]
    public void 完全不是歌词的文本不会崩溃()
    {
        var doc = LrcParser.Parse("这是一段随便写的文字\n没有任何时间戳\n!@#$%^&*()");

        Assert.True(doc.IsEmpty);
    }

    [Fact]
    public void 只有时间戳没有文字的行会被忽略()
    {
        var doc = LrcParser.Parse("[00:10.00]\n[00:20.00]有文字");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("有文字", line.Text);
    }

    [Fact]
    public void 坏掉的时间戳所在行被忽略且不影响其他行()
    {
        var doc = LrcParser.Parse("""
            [00:10.00]好行
            [99:99.99]坏行
            [00:20.00]另一个好行
            """);

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal(new[] { "好行", "另一个好行" }, doc.Lines.Select(l => l.Text));
    }

    [Fact]
    public void 未闭合的方括号不会崩溃()
    {
        var doc = LrcParser.Parse("[00:10.00没有闭合\n[00:20.00]正常行");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("正常行", line.Text);
    }

    [Fact]
    public void 支持CRLF换行()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行\r\n[00:20.00]第二行\r\n");

        Assert.Equal(2, doc.Lines.Count);
        Assert.Equal("第一行", doc.Lines[0].Text);
        Assert.Equal("第二行", doc.Lines[1].Text);
    }

    [Fact]
    public void 支持只有CR的换行()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行\r[00:20.00]第二行");

        Assert.Equal(2, doc.Lines.Count);
    }

    [Fact]
    public void 行首行尾的空白会被去掉()
    {
        var doc = LrcParser.Parse("   [00:10.00]   前后有空格   ");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("前后有空格", line.Text);
    }

    [Fact]
    public void 歌词文字里包含方括号也能正确解析()
    {
        var doc = LrcParser.Parse("[00:10.00]前面[中间]后面");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("前面[中间]后面", line.Text);
    }

    [Fact]
    public void 时间戳后没有空格也能解析()
    {
        var doc = LrcParser.Parse("[00:10.00]紧贴着的文字");

        var line = Assert.Single(doc.Lines);
        Assert.Equal("紧贴着的文字", line.Text);
    }

    // ---------------------------------------------------------------
    // 按时间查找当前行
    // ---------------------------------------------------------------

    [Fact]
    public void 播放位置早于第一行时返回负一()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行");

        Assert.Equal(-1, doc.FindLineIndexAt(TimeSpan.FromSeconds(5)));
        Assert.Equal(string.Empty, doc.GetTextAt(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void 命中时间点时返回该行()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行\n[00:20.00]第二行");

        Assert.Equal(0, doc.FindLineIndexAt(TimeSpan.FromSeconds(10)));
        Assert.Equal(1, doc.FindLineIndexAt(TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void 两个时间点之间返回前一行()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行\n[00:20.00]第二行");

        Assert.Equal(0, doc.FindLineIndexAt(TimeSpan.FromSeconds(15)));
        Assert.Equal("第一行", doc.GetTextAt(TimeSpan.FromSeconds(15)));
    }

    [Fact]
    public void 超过最后一行时间时返回最后一行()
    {
        var doc = LrcParser.Parse("[00:10.00]第一行\n[00:20.00]第二行");

        Assert.Equal(1, doc.FindLineIndexAt(TimeSpan.FromSeconds(999)));
        Assert.Equal("第二行", doc.GetTextAt(TimeSpan.FromSeconds(999)));
    }

    [Fact]
    public void 时间点相差一毫秒也能区分()
    {
        var doc = LrcParser.Parse("[00:10.000]第一行\n[00:10.001]第二行");

        Assert.Equal(0, doc.FindLineIndexAt(TimeSpan.FromMilliseconds(10000)));
        Assert.Equal(1, doc.FindLineIndexAt(TimeSpan.FromMilliseconds(10001)));
    }

    [Fact]
    public void 二分查找在大量行上也正确()
    {
        // 造 500 行，每行间隔 1 秒。
        // 注意时间戳要按 分:秒 正常进位，不能写出 [00:75.00] 这种非法格式
        // ——秒数必须小于 60，否则会被解析器当成坏数据丢掉。
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < 500; i++)
        {
            sb.Append('[')
              .Append((i / 60).ToString("00")).Append(':')
              .Append((i % 60).ToString("00")).Append(".00]第").Append(i).Append("行\n");
        }

        var doc = LrcParser.Parse(sb.ToString());
        Assert.Equal(500, doc.Lines.Count);

        // 抽查若干位置
        Assert.Equal(0, doc.FindLineIndexAt(TimeSpan.FromSeconds(0)));
        Assert.Equal(249, doc.FindLineIndexAt(TimeSpan.FromSeconds(249)));
        Assert.Equal(250, doc.FindLineIndexAt(TimeSpan.FromSeconds(250.5)));
        Assert.Equal(499, doc.FindLineIndexAt(TimeSpan.FromSeconds(499)));
        Assert.Equal(499, doc.FindLineIndexAt(TimeSpan.FromHours(10)));
    }

    [Fact]
    public void 空文档查找返回负一()
    {
        Assert.Equal(-1, LrcDocument.Empty.FindLineIndexAt(TimeSpan.FromSeconds(10)));
    }

    // ---------------------------------------------------------------
    // 真实歌词片段
    // ---------------------------------------------------------------

    [Fact]
    public void 解析一段真实的歌词片段()
    {
        var doc = LrcParser.Parse("""
            [ti:起风了]
            [ar:买辣椒也用券]
            [al:起风了]
            [by:小吕]
            [offset:0]
            [00:00.00] 作曲 : 高桥优
            [00:01.00] 作词 : 米果
            [00:02.34] 这一路上走走停停
            [00:06.78] 顺着少年漂流的痕迹
            [00:10.12] 迈出车站的前一刻
            """);

        Assert.Equal("起风了", doc.Title);
        Assert.Equal("买辣椒也用券", doc.Artist);
        Assert.Equal(5, doc.Lines.Count);
        Assert.Equal("这一路上走走停停", doc.Lines[2].Text);
        Assert.Equal(TimeSpan.FromSeconds(2.34), doc.Lines[2].Time);
    }
}
