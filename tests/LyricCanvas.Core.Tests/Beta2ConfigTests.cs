using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

public sealed class Beta2ConfigTests
{
    [Fact]
    public void CancellingAStyleDraftDoesNotMutateGlobalGradientOrLayoutFrames()
    {
        using var service = new ConfigService(filePath: null);
        service.Update(config =>
        {
            config.Frames.TextGradientColors = new List<string> { "#FF0000", "#0000FF" };
            config.Frames.PlaybackEffect = PlaybackEffectMode.LeftToRight;
            config.Frames.PlayedTextColor = "#00FF00";
        });

        var draft = service.Current;
        draft.Frames.TextGradientColors[0] = "#FFFFFF";
        draft.Frames.Frames[0].Left = 1234;
        draft.LayoutSchemes[0].Frames[0].Left = 5678;

        var unchanged = service.Current;
        Assert.Equal(new[] { "#FF0000", "#0000FF" }, unchanged.Frames.TextGradientColors);
        Assert.Equal(PlaybackEffectMode.LeftToRight, unchanged.Frames.PlaybackEffect);
        Assert.Equal("#00FF00", unchanged.Frames.PlayedTextColor);
        Assert.NotEqual(1234, unchanged.Frames.Frames[0].Left);
        Assert.NotEqual(5678, unchanged.LayoutSchemes[0].Frames[0].Left);
    }

    [Fact]
    public void CopyingGlobalStyleKeepsTheActiveFrameGeometryAndIsolatesGradient()
    {
        var destination = new FramesConfig { Frames = new List<FrameConfig> { new() { Left = 123 } } };
        var source = new FramesConfig
        {
            TextGradientColors = new List<string> { "#FF0000", "#0000FF" },
            PlaybackEffect = PlaybackEffectMode.LeftToRight,
            PlayedTextColor = "#00FF00",
            UnplayedTextColor = "#777777",
        };

        destination.CopyGlobalStyleFrom(source);
        source.TextGradientColors.Clear();

        Assert.Equal(123, Assert.Single(destination.Frames).Left);
        Assert.Equal(new[] { "#FF0000", "#0000FF" }, destination.TextGradientColors);
        Assert.Equal(PlaybackEffectMode.LeftToRight, destination.PlaybackEffect);
        Assert.Equal("#00FF00", destination.PlayedTextColor);
        Assert.Equal("#777777", destination.UnplayedTextColor);
    }

    [Fact]
    public void InvalidEffectColorsAndModesFallBackWithoutLosingGoodGradientStops()
    {
        using var service = new ConfigService(filePath: null);
        service.Update(config =>
        {
            config.Frames.TextGradientColors = Enumerable.Repeat("invalid", 16)
                .Concat(new[] { "#abc", "#123456", "#80123456" }).ToList();
            config.Frames.PlayedTextColor = "garbage";
            config.Frames.UnplayedTextColor = "#zzzzzz";
            config.Frames.PlaybackEffect = (PlaybackEffectMode)999;
        });

        var frames = service.Current.Frames;
        Assert.Equal(new[] { "#abc", "#123456", "#80123456" }, frames.TextGradientColors);
        Assert.Null(frames.PlayedTextColor);
        Assert.Null(frames.UnplayedTextColor);
        Assert.Equal(PlaybackEffectMode.Fade, frames.PlaybackEffect);
    }

    [Fact]
    public void DuplicateFrameIdsAreRepairedWithinEachLayout()
    {
        using var service = new ConfigService(filePath: null);
        service.Update(config => config.Frames.Frames = new List<FrameConfig>
        {
            new() { Id = "duplicate", Name = "First" },
            new() { Id = "duplicate", Name = "Second" },
        });

        var config = service.Current;
        Assert.Equal("duplicate", config.Frames.Frames[0].Id);
        Assert.NotEqual(config.Frames.Frames[0].Id, config.Frames.Frames[1].Id);
        Assert.Equal(config.Frames.Frames.Select(frame => frame.Id),
            config.LayoutSchemes[0].Frames.Select(frame => frame.Id));
    }

    [Fact]
    public void BrokenJsonStillHasUsableDefaultFramesAndNeverOverwritesTheOriginal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lyriccanvas-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        const string broken = "{ broken";
        try
        {
            File.WriteAllText(path, broken);
            using (var service = new ConfigService(path, autoSave: false))
            {
                Assert.Equal(2, service.Current.Frames.Frames.Count);
                var layout = Assert.Single(service.Current.LayoutSchemes);
                Assert.Equal(layout.Id, service.Current.ActiveLayoutId);
                service.Update(config => config.Frames.FontSize = 64);
                Assert.False(service.SaveNow());
            }

            Assert.Equal(broken, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void GradientPlaybackColorsAndImportedFontReferenceSurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lyriccanvas-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        const string fontReference = "file:///C:/Fonts/Imported/#Example Font";
        try
        {
            using (var service = new ConfigService(path, autoSave: false))
            {
                Assert.True(service.UpdateAndSave(config =>
                {
                    config.Frames.TextGradientColors = new List<string> { "#FF0000", "#0000FF" };
                    config.Frames.FontFamily = fontReference;
                    config.Frames.PlaybackEffect = PlaybackEffectMode.LeftToRight;
                    config.Frames.PlayedTextColor = "#00FF00";
                    config.Frames.UnplayedTextColor = "#777777";
                }));
            }

            using var reopened = new ConfigService(path, autoSave: false);
            var frames = reopened.Current.Frames;
            Assert.Equal(new[] { "#FF0000", "#0000FF" }, frames.TextGradientColors);
            Assert.Equal(fontReference, frames.FontFamily);
            Assert.Equal(PlaybackEffectMode.LeftToRight, frames.PlaybackEffect);
            Assert.Equal("#00FF00", frames.PlayedTextColor);
            Assert.Equal("#777777", frames.UnplayedTextColor);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LegacySingleLayoutMigratesWithoutResettingItsStyleOrGeometry()
    {
        var directory = Path.Combine(Path.GetTempPath(), "lyriccanvas-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.json");
        try
        {
            File.WriteAllText(path, """
                { "version": 6, "frames": {
                    "fontSize": 63,
                    "frames": [{ "id": "old-frame", "left": 321, "top": 456, "rotationDegrees": 15 }]
                }}
                """);
            string migratedLayoutId;
            using (var service = new ConfigService(path, autoSave: false))
            {
                var config = service.Current;
                migratedLayoutId = config.ActiveLayoutId;
                Assert.True(service.IsDirty);
                Assert.Equal(63, config.Frames.FontSize);
                Assert.Equal(migratedLayoutId, Assert.Single(config.LayoutSchemes).Id);
                Assert.True(service.SaveNow());
            }

            using var reopened = new ConfigService(path, autoSave: false);
            var migrated = reopened.Current;
            Assert.Equal(migratedLayoutId, migrated.ActiveLayoutId);
            var frame = Assert.Single(migrated.Frames.Frames);
            Assert.Equal("old-frame", frame.Id);
            Assert.Equal(321, frame.Left);
            Assert.Equal(456, frame.Top);
            Assert.Equal(15, frame.RotationDegrees);
            Assert.Equal(63, migrated.Frames.FontSize);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
