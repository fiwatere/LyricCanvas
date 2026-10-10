using System.Text;
using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

public sealed class EnhancedLrcTests
{
    [Fact]
    public void EnhancedLyricsKeepWordStartsAndExplicitEndWithoutShowingTags()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>Hello <00:11.50>world<00:12.00>");

        var line = Assert.Single(document.Lines);
        Assert.Equal("Hello world", line.Text);
        Assert.Collection(line.Segments!,
            first =>
            {
                Assert.Equal("Hello ", first.Text);
                Assert.Equal(TimeSpan.FromSeconds(10), first.Start);
                Assert.Equal(TimeSpan.FromSeconds(11.5), first.End);
            },
            last =>
            {
                Assert.Equal("world", last.Text);
                Assert.Equal(TimeSpan.FromSeconds(11.5), last.Start);
                Assert.Equal(TimeSpan.FromSeconds(12), last.End);
            });
    }

    [Fact]
    public void RepeatedChorusAndOffsetMoveBothWordBoundaries()
    {
        var document = LrcParser.Parse(
            "[offset:500]\n[00:10.00][00:20.00]<00:10.00>A<00:11.00>B<00:12.00>");

        Assert.Equal(new[] { 9.5, 19.5 }, document.Lines.Select(line => line.Time.TotalSeconds));
        Assert.Collection(document.Lines[1].Segments!,
            first =>
            {
                Assert.Equal(TimeSpan.FromSeconds(19.5), first.Start);
                Assert.Equal(TimeSpan.FromSeconds(20.5), first.End);
            },
            last =>
            {
                Assert.Equal(TimeSpan.FromSeconds(20.5), last.Start);
                Assert.Equal(TimeSpan.FromSeconds(21.5), last.End);
            });
    }

    [Fact]
    public void PositiveOffsetClampsLineAndWordsAtSongStart()
    {
        var document = LrcParser.Parse(
            "[offset:1500]\n[00:01.00]<00:01.00>A<00:02.00>B<00:03.00>");

        var line = Assert.Single(document.Lines);
        Assert.Equal(TimeSpan.Zero, line.Time);
        Assert.Equal(TimeSpan.Zero, line.Segments![0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(500), line.Segments[0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(1500), line.Segments[1].End);
    }

    [Fact]
    public void TextBeforeTheFirstWordTagRemainsPartOfTheLine()
    {
        var document = LrcParser.Parse("[00:01.00]Hello <00:02.00>world");

        var line = Assert.Single(document.Lines);
        Assert.Equal("Hello world", line.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Segments![0].Start);
        Assert.Null(line.Segments[1].End);
    }

    [Fact]
    public void InvalidInlineTagsRemainVisibleText()
    {
        var line = Assert.Single(LrcParser.Parse("[00:01.00]A<00:99.00>B<bold>C").Lines);

        Assert.Equal("A<00:99.00>B<bold>C", line.Text);
        Assert.Null(line.Segments);
    }

    [Fact]
    public void BackwardWordTagsDoNotMoveHighlightBackInTime()
    {
        var line = Assert.Single(LrcParser.Parse(
            "[00:10.00]<00:10.00>A<00:12.00>B<00:11.00>C<00:13.00>").Lines);

        Assert.Equal("ABC", line.Text);
        Assert.Equal(new[] { 10.0, 12.0, 12.0 }, line.Segments!.Select(segment => segment.Start.TotalSeconds));
    }

    [Fact]
    public void Utf8BomDoesNotDiscardTheFirstLyricLine()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lyriccanvas-lrc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "bom.lrc");
        try
        {
            File.WriteAllText(path, "[00:01.00]First\n[00:02.00]Second", new UTF8Encoding(true));

            Assert.Equal(new[] { "First", "Second" }, LrcParser.ParseFile(path).Lines.Select(line => line.Text));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChineseLyricsDecodeFromGbkOrUtf16Files(bool utf16)
    {
        var directory = Path.Combine(Path.GetTempPath(), "lyriccanvas-lrc-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "chinese.lrc");
        try
        {
            var encoding = utf16 ? Encoding.Unicode : CodePagesEncodingProvider.Instance.GetEncoding(936)!;
            File.WriteAllText(path, "[00:01.00]这是一句中文歌词", encoding);

            var line = Assert.Single(LrcParser.ParseFile(path).Lines);
            Assert.Equal("这是一句中文歌词", line.Text);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("[offset:9223372036854775807]")]
    [InlineData("[offset:-9223372036854775808]")]
    [InlineData("[999999999:00:00]Overflow")]
    public void OutOfRangeTimeTagsDoNotBreakOtherLyrics(string malformed)
    {
        var document = LrcParser.Parse(malformed + "\n[00:01.00]Good");

        var line = Assert.Single(document.Lines);
        Assert.Equal("Good", line.Text);
        Assert.Equal(TimeSpan.FromSeconds(1), line.Time);
    }

    [Fact]
    public void WordSweepFinishesAtItsExplicitEndBeforeTheNextLine()
    {
        var document = LrcParser.Parse(
            "[00:10.00]<00:10.00>Hi<00:12.00> all<00:14.00>\n[00:20.00]Next");
        var line = LyricTimeline.Build(document, TimeSpan.FromSeconds(30))[0];

        Assert.Equal(0, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(9)));
        Assert.Equal(1, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(11)));
        Assert.Equal(2, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(12)));
        Assert.Equal(4, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(13)));
        Assert.Equal(6, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(14)));
        Assert.Equal(6, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(19)));
        Assert.Equal(6, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void SweepUsesLineEndWhenTheLastWordHasNoEndTag()
    {
        var document = LrcParser.Parse("[00:10.00]<00:10.00>Hi<00:12.00> all\n[00:20.00]Next");
        var line = LyricTimeline.Build(document, TimeSpan.FromSeconds(30))[0];

        Assert.Equal(4, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(16)));
        Assert.Equal(6, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void PlainLrcSweepRewindsImmediatelyWhenThePlayerSeeksBack()
    {
        var line = new LyricLineTiming(0, "0123456789", TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(20));

        Assert.Equal(8, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(18)));
        Assert.Equal(2, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(12)));
        Assert.Equal(0, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(10)));
        Assert.Equal(10, LyricTimeline.GetTextOffsetAt(line, TimeSpan.FromSeconds(20)));
    }

    [Fact]
    public void SimultaneousOriginalAndTranslationShareTheNextDifferentTimestamp()
    {
        var document = LrcParser.Parse("[00:05.00]Original\n[00:05.00]Translation\n[00:07.00]Next");
        var lines = LyricTimeline.Build(document, TimeSpan.FromSeconds(20));

        Assert.Equal(TimeSpan.FromSeconds(7), lines[0].End);
        Assert.Equal(TimeSpan.FromSeconds(7), lines[1].End);
        Assert.Equal("Translation", LyricTimeline.FindAt(lines, TimeSpan.FromSeconds(5))!.Text);
        Assert.Equal("Next", LyricTimeline.FindAt(lines, TimeSpan.FromSeconds(7))!.Text);
    }

    [Fact]
    public void EnhancedLyricsCanBeFoundAgainAfterTheSongLoops()
    {
        var document = LrcParser.Parse("[00:01.00]<00:01.00>Start<00:03.00>\n[00:05.00]Last");
        var lines = LyricTimeline.Build(document, TimeSpan.FromSeconds(10));

        Assert.Null(LyricTimeline.FindAt(lines, TimeSpan.FromSeconds(10)));
        var replay = LyricTimeline.FindAt(lines, TimeSpan.FromSeconds(2));
        Assert.NotNull(replay);
        Assert.Equal("Start", replay.Text);
        Assert.Equal(2.5, LyricTimeline.GetTextOffsetAt(replay, TimeSpan.FromSeconds(2)));
    }
}
