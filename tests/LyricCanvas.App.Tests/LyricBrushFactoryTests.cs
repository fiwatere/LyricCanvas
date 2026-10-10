using System.Reflection;
using System.Windows.Media;
using LyricCanvas.Core;

namespace LyricCanvas.App.Tests;

public sealed class LyricBrushFactoryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-color")]
    [InlineData("#invalid")]
    public void InvalidColorsReturnCallerFallback(string? value)
    {
        WpfTestThread.Run(() =>
        {
            var fallback = Color.FromRgb(21, 43, 65);
            var color = Invoke<Color>("ParseColor", new[] { typeof(string), typeof(Color) }, value, fallback);
            Assert.Equal(fallback, color);
        });
    }

    [Fact]
    public void EmptyAndSingleStopGradientsProduceFrozenSolidColors()
    {
        WpfTestThread.Run(() =>
        {
            var fallback = Colors.Gold;
            var empty = CreateGradient(Array.Empty<string>(), fallback);
            Assert.Equal(fallback, Assert.IsType<SolidColorBrush>(empty).Color);
            Assert.True(empty.IsFrozen);

            var single = CreateGradient(new[] { "#FF102030" }, fallback);
            Assert.Equal(Color.FromRgb(0x10, 0x20, 0x30), Assert.IsType<SolidColorBrush>(single).Color);
            Assert.True(single.IsFrozen);
        });
    }

    [Fact]
    public void MultipleStopsAreEvenlySpacedAndInvalidStopUsesFallback()
    {
        WpfTestThread.Run(() =>
        {
            var brush = Assert.IsType<LinearGradientBrush>(CreateGradient(
                new[] { "#FFFF0000", "broken", "#FF0000FF" }, Colors.White));
            Assert.True(brush.IsFrozen);
            Assert.Equal(3, brush.GradientStops.Count);
            Assert.Equal(0, brush.GradientStops[0].Offset);
            Assert.Equal(0.5, brush.GradientStops[1].Offset);
            Assert.Equal(1, brush.GradientStops[2].Offset);
            Assert.Equal(Colors.White, brush.GradientStops[1].Color);
        });
    }

    [Fact]
    public void OpacityCopyIsFrozenAndLeavesGlobalColorUnchanged()
    {
        WpfTestThread.Run(() =>
        {
            var source = CreateGradient(new[] { "#FFFF0000", "#FF0000FF" }, Colors.White);
            var dimmed = Invoke<Brush>("WithOpacity", new[] { typeof(Brush), typeof(double) }, source, 0.3);
            Assert.True(source.IsFrozen);
            Assert.True(dimmed.IsFrozen);
            Assert.NotSame(source, dimmed);
            Assert.Equal(1, source.Opacity);
            Assert.Equal(0.3, dimmed.Opacity);

            var sampled = Invoke<Color>("SampleGradient",
                new[] { typeof(LinearGradientBrush), typeof(double) }, dimmed, 0.5);
            Assert.Equal(Color.FromArgb(76, 128, 0, 128), sampled);
        });
    }

    [Fact]
    public void GlobalGradientTakesPrecedenceOverGlobalSolidColor()
    {
        WpfTestThread.Run(() =>
        {
            var style = new FramesConfig
            {
                TextColor = "#FFFF0000",
                TextGradientColors = new List<string> { "#FF0000FF" },
            };
            var brush = Invoke<Brush>("CreateTextBrush",
                new[] { typeof(FramesConfig), typeof(Color) }, style, Colors.White);
            Assert.Equal(Colors.Blue, Assert.IsType<SolidColorBrush>(brush).Color);
        });
    }

    private static Brush CreateGradient(IReadOnlyList<string> colors, Color fallback)
        => Invoke<Brush>("CreateGradient", new[] { typeof(IReadOnlyList<string>), typeof(Color) }, colors, fallback);

    private static T Invoke<T>(string name, Type[] parameters, params object?[] arguments)
    {
        // Keep production helpers internal; reflection is limited to this rendering regression suite.
        var factory = typeof(OverlayWindow).Assembly.GetType("LyricCanvas.App.Rendering.LyricBrushFactory", throwOnError: true)!;
        var method = factory.GetMethod(name, BindingFlags.Static | BindingFlags.Public,
            binder: null, types: parameters, modifiers: null)!;
        return (T)method.Invoke(null, arguments)!;
    }
}
