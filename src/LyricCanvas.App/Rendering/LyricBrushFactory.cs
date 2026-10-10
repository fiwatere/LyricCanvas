using System.Windows.Media;
using LyricCanvas.Core;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using Colors = System.Windows.Media.Colors;
using Point = System.Windows.Point;

namespace LyricCanvas.App.Rendering;

/// <summary>歌词窗口和设置预览共用的颜色、渐变及透明度规则。</summary>
internal static class LyricBrushFactory
{
    public static Color ParseColor(string? value, Color fallback)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        try
        {
            return ColorConverter.ConvertFromString(value) is Color color ? color : fallback;
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException or ArgumentException)
        {
            return fallback;
        }
    }

    public static SolidColorBrush CreateSolid(string? value, Color fallback)
        => CreateSolid(ParseColor(value, fallback));

    public static SolidColorBrush CreateSolid(Color color)
        => Freeze(new SolidColorBrush(color));

    public static Brush CreateTextBrush(FramesConfig style, Color fallback)
        => style.TextGradientColors is { Count: > 0 } colors
            ? CreateGradient(colors, fallback)
            : CreateSolid(style.TextColor, fallback);

    public static Brush CreateGradient(IReadOnlyList<string>? colors, Color fallback)
    {
        if (colors is null || colors.Count == 0)
        {
            return Freeze(new SolidColorBrush(fallback));
        }

        if (colors.Count == 1)
        {
            return CreateSolid(colors[0], fallback);
        }

        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0.5),
            EndPoint = new Point(1, 0.5),
            MappingMode = BrushMappingMode.RelativeToBoundingBox,
        };
        for (var index = 0; index < colors.Count; index++)
        {
            brush.GradientStops.Add(new GradientStop(
                ParseColor(colors[index], fallback),
                (double)index / (colors.Count - 1)));
        }

        return Freeze(brush);
    }

    public static Brush WithOpacity(Brush source, double opacity)
    {
        var copy = source.CloneCurrentValue();
        copy.Opacity = Math.Clamp(double.IsFinite(opacity) ? opacity : 1, 0, 1);
        return Freeze(copy);
    }

    /// <summary>对工厂按位置排序的渐变取色，无须为每个字重新排序或分配集合。</summary>
    public static Color SampleGradient(LinearGradientBrush gradient, double offset)
    {
        var stops = gradient.GradientStops;
        if (stops.Count == 0)
        {
            return Colors.White;
        }

        offset = Math.Clamp(offset, 0, 1);
        if (offset <= stops[0].Offset)
        {
            return ApplyOpacity(stops[0].Color, gradient.Opacity);
        }

        for (var index = 1; index < stops.Count; index++)
        {
            if (offset > stops[index].Offset)
            {
                continue;
            }

            var previous = stops[index - 1];
            var next = stops[index];
            var range = next.Offset - previous.Offset;
            var fraction = range <= 0 ? 1 : (offset - previous.Offset) / range;
            var color = Color.FromArgb(
                (byte)Math.Round(previous.Color.A + (next.Color.A - previous.Color.A) * fraction),
                (byte)Math.Round(previous.Color.R + (next.Color.R - previous.Color.R) * fraction),
                (byte)Math.Round(previous.Color.G + (next.Color.G - previous.Color.G) * fraction),
                (byte)Math.Round(previous.Color.B + (next.Color.B - previous.Color.B) * fraction));
            return ApplyOpacity(color, gradient.Opacity);
        }

        return ApplyOpacity(stops[^1].Color, gradient.Opacity);
    }

    private static Color ApplyOpacity(Color color, double opacity)
        => Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1)), color.R, color.G, color.B);

    private static T Freeze<T>(T brush) where T : Brush
    {
        brush.Freeze();
        return brush;
    }
}
