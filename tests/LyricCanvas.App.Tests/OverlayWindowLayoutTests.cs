using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LyricCanvas.Core;

namespace LyricCanvas.App.Tests;

public sealed class OverlayWindowLayoutTests
{
    [Fact]
    public void LongLineUsesSameReducedFontSizeOnBothHighlightLayers()
    {
        WpfTestThread.Run(() =>
        {
            var frame = CreateFrame(width: 220);
            var style = CreateStyle();
            var window = new OverlayWindow(frame.Id);
            try
            {
                window.ApplyFrame(frame, style);
                window.ShowLine("A long lyric line that cannot fit at the configured font size", fadeInMs: 0);

                var unplayed = Field<TextBlock>(window, "_lyricText");
                var played = Field<TextBlock>(window, "_playedLyricText");
                Assert.True(unplayed.FontSize < style.FontSize);
                Assert.Equal(unplayed.FontSize, played.FontSize);
            }
            finally
            {
                window.Close();
            }
        });
    }

    [Theory]
    [InlineData(20)]
    [InlineData(-2)]
    public void CompleteHighlightWithLetterSpacingMatchesTheMeasuredRow(double spacing)
    {
        WpfTestThread.Run(() =>
        {
            var frame = CreateFrame(width: 1000);
            var style = CreateStyle();
            style.LetterSpacing = spacing;
            var window = new OverlayWindow(frame.Id);
            try
            {
                const string text = "Wide歌词";
                window.ApplyFrame(frame, style);
                window.ShowLine(text, fadeInMs: 0);
                var content = Assert.IsType<Grid>(window.Content);
                content.Measure(new Size(frame.Width, frame.Height));
                content.Arrange(new Rect(0, 0, frame.Width, frame.Height));
                content.UpdateLayout();

                var line = new LyricLineTiming(0, text, TimeSpan.Zero, TimeSpan.FromSeconds(4));
                window.UpdatePlaybackProgress(line, line.End, style, frame);

                var row = Field<StackPanel>(window, "_lyricRow");
                var clip = Field<RectangleGeometry>(window, "_playedClip");
                Assert.True(row.DesiredSize.Width > 0);
                Assert.Equal(row.DesiredSize.Width, clip.Rect.Width, precision: 6);
            }
            finally
            {
                window.Close();
            }
        });
    }

    private static FramesConfig CreateStyle() => new()
    {
        FontSize = 44,
        FontFamily = "Segoe UI",
        GlowRadius = 0,
        GlowOpacity = 0,
        LyricEffect = LyricEffectMode.None,
        PlaybackEffect = PlaybackEffectMode.LeftToRight,
    };

    private static FrameConfig CreateFrame(double width) => new()
    {
        Id = "layout-test",
        Left = 0,
        Top = 0,
        Width = width,
        Height = 100,
    };

    private static T Field<T>(OverlayWindow window, string name)
        => (T)typeof(OverlayWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
}
