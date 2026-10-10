using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Animation;
using LyricCanvas.App.Rendering;
using LyricCanvas.Core;

// 项目同时开了 UseWPF 和 UseWindowsForms（托盘图标要用后者），两边的隐式 using
// 会让下面这些名字产生歧义，这里用别名明确指定 WPF 版本
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using FontFamily = System.Windows.Media.FontFamily;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

using Border = System.Windows.Controls.Border;
using Grid = System.Windows.Controls.Grid;
using StackPanel = System.Windows.Controls.StackPanel;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LyricCanvas.App;

/// <summary>
/// 一个歌词框（屏幕上的一个文字窗口）。
///
/// 这个类**只管画**：给它文字就淡入显示，说撤就淡出消失。
/// "该显示第几句歌词"由 WindowManager 决定，"这个框在哪、多大"来自配置。
/// 这样以后换分配策略（比如随机位置出现）完全不用动这里。
///
/// 淡入淡出的实现要点：动画直接作用在 **Window.Opacity（整窗 alpha）** 上，
/// 而不是逐像素做透明度动画。调研结论是前者走系统托管路径、能避开每帧回读；
/// 后者在分层窗口上代价大得多。
/// </summary>
public sealed class OverlayWindow : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    private readonly Grid _root;
    private readonly TextBlock _lyricText;
    private readonly TextBlock _playedLyricText;
    private readonly TextBlock _trackText;
    private readonly System.Windows.Controls.Canvas _particlesCanvas = new()
    {
        IsHitTestVisible = false,
        ClipToBounds = false,
    };
    private readonly TranslateTransform _effectTranslation = new();
    private readonly Grid _lyricLayer = new() { ClipToBounds = false };
    private readonly RectangleGeometry _playedClip = new(new Rect(0, 0, 0, 0));
    private Brush _baseLyricBrush = System.Windows.Media.Brushes.White;
    private Brush _playedLyricBrush = System.Windows.Media.Brushes.White;
    private Brush _unplayedLyricBrush = System.Windows.Media.Brushes.White;
    private double _playbackTextOffset;
    private bool _playbackSweepEnabled;
    private Brush? _appliedBaseBrush;
    private string? _appliedPlayedTextColor;
    private string? _appliedUnplayedTextColor;
    private bool _appliedPlaybackSweep;
    private double[]? _prefixTextWidths;
    private double[]? _spacedTextWidths;
    private Typeface? _measuredTypeface;
    private double _metricsPixelsPerDip;
    /// <summary>
    /// 文字内容的承载层，背景保持完全透明。
    ///
    /// 历史上它叫 _dragArea——那时窗口支持鼠标拖动，这块区域是拖动手柄。
    /// 拖动功能已移除，名字跟着改掉，免得后人以为这里还能拖。
    /// </summary>
    private readonly Border _contentArea;


    private string _currentText = string.Empty;
    private bool _isVisibleContent;
    private bool _isFadingOut;
    private int _opacityAnimationVersion;
    private PendingLine? _pendingLine;
    private LyricEffectMode _appliedLyricEffect = LyricEffectMode.None;
    private double _appliedEffectIntensity = -1;
    private LyricMotionDirection _appliedMotionDirection = LyricMotionDirection.Vertical;
    private int _appliedMotionPeriodMs = -1;
    private Color _particleColor = Color.FromRgb(0xFF, 0xB8, 0x3D);
    private double _appliedParticleSize = -1;
    private int _appliedParticleCount = -1;
    private double _appliedParticleSpread = -1;
    private int _appliedParticleDurationMs = -1;
    private Color _appliedParticleColor = Colors.Transparent;

    private sealed record PendingLine(string Text, int FadeInMs, Action? OnFadeInCompleted);

    /// <summary>框的标识，位置回写时用来对应到配置。</summary>
    public string FrameId { get; }

    /// <summary>未旋转歌词框的逻辑几何，窗口外框会为旋转和光晕扩展。</summary>
    public double FrameLeft { get; private set; }
    public double FrameTop { get; private set; }
    public double FrameWidth { get; private set; }
    public double FrameHeight { get; private set; }



    public OverlayWindow(string frameId)
    {
        FrameId = frameId;

        Title = "词绘";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        // 透明窗口只绘制文字和效果；鼠标穿透由 WS_EX_TRANSPARENT 单独控制，
        // 不再用非零 alpha 的整窗底色制造纯黑背景上可见的薄雾。
        Background = System.Windows.Media.Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SnapsToDevicePixels = true;
        Opacity = 0;

        // 文字外层用一个 Grid 承载，方便整体居中
        _lyricText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        _playedLyricText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 44,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        // 代码创建的控件不会像 XAML 里那样自动获得 x:Name，
        // AutomationId 要显式设，否则自动化验证读不到文字
        System.Windows.Automation.AutomationProperties.SetAutomationId(_lyricText, "LyricText");

        // 默认（字间距为 0）就是单个 TextBlock 装整句
        _lyricLayer.RenderTransform = _effectTranslation;
        _lyricRow.Children.Add(_lyricText);
        _playedLyricRow.Children.Add(_playedLyricText);
        _playedLyricRow.Clip = _playedClip;
        _lyricLayer.Children.Add(_lyricRow);
        _lyricLayer.Children.Add(_playedLyricRow);

        _trackText = new TextBlock
        {
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 16,
            Foreground = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF)),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.NoWrap,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            VerticalAlignment = System.Windows.VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 4),
            IsHitTestVisible = false,
        };

        System.Windows.Automation.AutomationProperties.SetAutomationId(_trackText, "TrackText");

        var contentLayer = new Grid
        {
            IsHitTestVisible = false,
        };
        contentLayer.Children.Add(_lyricLayer);
        contentLayer.Children.Add(_particlesCanvas);

        _contentArea = new Border
        {
            Background = System.Windows.Media.Brushes.Transparent,
            Child = contentLayer,
        };

        _root = new Grid
        {
            // 背景像素保持完全透明；鼠标穿透由窗口扩展样式控制，不再用可见像素填充歌词框区域。
            Background = System.Windows.Media.Brushes.Transparent,
        };
        _root.Children.Add(_contentArea);
        _root.Children.Add(_trackText);

        Content = _root;


    }

    /// <summary>
    /// 按配置摆放这个框并应用样式。
    /// </summary>
    /// <param name="frame">这个框的配置。</param>
    /// <param name="style">全局样式（框自己没有覆盖时用它）。</param>
    public void ApplyFrame(FrameConfig frame, FramesConfig style)
    {
        FrameLeft = frame.Left;
        FrameTop = frame.Top;
        FrameWidth = Math.Max(60, frame.Width);
        FrameHeight = Math.Max(24, frame.Height);

        var surface = CalculateSurfaceBounds(
            FrameLeft,
            FrameTop,
            FrameWidth,
            FrameHeight,
            frame.RotationDegrees,
            style.GlowRadius,
            style.LyricEffect == LyricEffectMode.Particles
                ? style.ParticleSpread + style.ParticleSize + 2
                : 0);
        Left = surface.Left;
        Top = surface.Top;
        Width = surface.Width;
        Height = surface.Height;

        // HWND 的裁剪边界是矩形；先按旋转后的外接矩形扩大窗口，再把逻辑内容
        // 放回中心，才能让旋转后的四角和文字光晕完整显示。
        _root.Width = FrameWidth;
        _root.Height = FrameHeight;
        _root.HorizontalAlignment = System.Windows.HorizontalAlignment.Center;
        _root.VerticalAlignment = System.Windows.VerticalAlignment.Center;
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = Math.Abs(frame.RotationDegrees) < 0.001
            ? Transform.Identity
            : new RotateTransform(frame.RotationDegrees);

        var fontSize = frame.FontSize ?? style.FontSize;
        _configuredFontSize = fontSize;
        _lyricText.FontSize = fontSize;
        _playedLyricText.FontSize = fontSize;


        // 排查用：确认"设置里改了样式"到底有没有应用到这个窗口上。
        // 环境变量 LYRICDESKTOP_STYLE_LOG=1 时写日志，平时不落盘。
        if (Environment.GetEnvironmentVariable("LYRICDESKTOP_STYLE_LOG") == "1")
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lyricdesktop-style.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  框={frame.Id} 字号={fontSize} 不透明度={OpacityTarget} " +
                    $"字色={(frame.TextColor ?? style.TextColor)}{Environment.NewLine}");
            }
            catch (System.IO.IOException)
            {
            }
        }
        var lyricFont = FontCatalog.CreateFontFamily(style.FontFamily);
        _lyricText.FontFamily = lyricFont;
        _playedLyricText.FontFamily = lyricFont;
        _baseLyricBrush = frame.TextColor is not null
            ? ParseBrush(frame.TextColor, Colors.White)
            : LyricBrushFactory.CreateTextBrush(style, Colors.White);
        ApplyPlaybackBrushes(style);

        _lyricLayer.Effect = style.GlowRadius <= 0.1 || style.GlowOpacity <= 0.001
            ? null
            : new DropShadowEffect
            {
                Color = ParseColor(style.GlowColor, Color.FromRgb(0xFF, 0xB8, 0x3D)),
                BlurRadius = style.GlowRadius,
                ShadowDepth = 0,
                Opacity = style.GlowOpacity,
                RenderingBias = RenderingBias.Performance,
            };

        _trackText.FontFamily = lyricFont;
        _trackText.FontSize = Math.Max(10, fontSize * 0.36);
        _trackText.Foreground = ParseBrush(style.SubTextColor, Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));
        _trackText.Visibility = style.ShowTrackInfo ? Visibility.Visible : Visibility.Collapsed;

        // 完全不透明时的目标值：全局不透明度 × 这个框自己的覆盖值
        var newTarget = Math.Clamp(frame.Opacity ?? style.Opacity, 0.05, 1.0);

        if (Math.Abs(newTarget - OpacityTarget) > 0.001)
        {
            OpacityTarget = newTarget;

            // 关键：光改 OpacityTarget 是不够的。
            //
            // 淡入淡出用 BeginAnimation 设置动画，动画结束后 FillBehavior.HoldEnd
            // 会把窗口的 Opacity **锁在动画的终值**上——直接给 Opacity 赋值是无效的。
            // 所以目标值一变，必须重新走一次动画把它带过去，
            // 否则改透明度设置时窗口不会有任何反应（这就是"透明度没效果"的原因）。
            //
            // 正在淡出（当前不可见）的框不动它，免得把已经消失的文字又拉回来。
            if (_isVisibleContent)
            {
                FadeTo(newTarget, style.FadeInMs);
            }
        }

        // 字间距必须在**末尾**处理：前面那些行才开始设置字号、字体、颜色，
        // 而 ApplyLetterSpacing 要用这些属性去生成每个字的 TextBlock。
        // 放在开头会拿到还没配好的样式，而且那时 _currentText 还是空的——
        // 结果就是"设了字间距但排版永远不重做"（这个 bug 就是这么来的）。
        if (Math.Abs(_letterSpacing - style.LetterSpacing) > 0.01)
        {
            _letterSpacing = style.LetterSpacing;
        }

        ApplyLetterSpacing();
        if (_isVisibleContent)
        {
            FitTextToWidth();
        }
        ApplyGlyphForegrounds(_lyricRow, _unplayedLyricBrush);
        ApplyGlyphForegrounds(_playedLyricRow, _playedLyricBrush);
        UpdatePlayedClip();

        var particleColor = ParseColor(style.GlowColor, Color.FromRgb(0xFF, 0xB8, 0x3D));
        ApplyLyricEffect(
            style.LyricEffect,
            style.EffectIntensity,
            style.MotionDirection,
            style.MotionPeriodMs,
            style.ParticleSize,
            style.ParticleCount,
            style.ParticleSpread,
            style.ParticleDurationMs,
            particleColor);

        ApplyLocked(frame.Locked);
    }

    /// <summary>完全不透明时的目标不透明度。淡入就动画到这个值。</summary>
    public double OpacityTarget { get; private set; } = 1.0;

    /// <summary>计算歌词内容、旋转外接矩形和光晕余量共同需要的窗口范围。</summary>
    public static Rect CalculateSurfaceBounds(
        double left,
        double top,
        double width,
        double height,
        double rotationDegrees,
        double glowRadius,
        double effectPadding = 0)
    {
        var radians = rotationDegrees * Math.PI / 180;
        var cos = Math.Abs(Math.Cos(radians));
        var sin = Math.Abs(Math.Sin(radians));
        var rotatedWidth = width * cos + height * sin;
        var rotatedHeight = width * sin + height * cos;
        var padding = Math.Max(
            glowRadius <= 0.1 ? 1 : Math.Ceiling(glowRadius * 2),
            Math.Max(0, Math.Ceiling(effectPadding)));

        return new Rect(
            left + (width - rotatedWidth) / 2 - padding,
            top + (height - rotatedHeight) / 2 - padding,
            rotatedWidth + padding * 2,
            rotatedHeight + padding * 2);
    }

    /// <summary>设置曲名小字。</summary>
    public void SetTrackText(string text)
    {
        if (!string.Equals(_trackText.Text, text, StringComparison.Ordinal))
        {
            _trackText.Text = text;
        }
    }

    /// <summary>用户在设置里选的原始字号。压缩只在这个值的基础上临时下调。</summary>
    private double _configuredFontSize = 44;

    /// <summary>设置字号。逐字排版时每个字块都要跟着改。</summary>
    private void SetFontSize(double size)
    {
        _lyricText.FontSize = size;
        _playedLyricText.FontSize = size;
        InvalidateTextMetrics();

        if (Math.Abs(_letterSpacing) < 0.01)
        {
            return;
        }

        foreach (var child in _lyricRow.Children.OfType<TextBlock>())
        {
            child.FontSize = size;
        }
        foreach (var child in _playedLyricRow.Children.OfType<TextBlock>())
        {
            child.FontSize = size;
        }
    }

    /// <summary>当前配置的字间距，用于把文字做疏密处理。</summary>
    private double _letterSpacing;

    /// <summary>
    /// 歌词的横向容器。字间距为 0 时里面只有一个 `_lyricText`；
    /// 需要字间距时换成逐字排版的多个 TextBlock。
    /// </summary>
    private readonly StackPanel _lyricRow = new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private readonly StackPanel _playedLyricRow = new()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    /// <summary>压缩字号的下限比例：不低于原始字号的六成。</summary>
    private const double MinFontScale = 0.6;

    /// <summary>
    /// 淡入显示一句歌词。同一句重复调用不会重启动画；淡出期间的新歌词只保留最新一条。
    /// </summary>
    public void ShowLine(
        string text,
        int fadeInMs,
        bool interruptFadeOut = false,
        Action? onFadeInCompleted = null)
    {
        text ??= string.Empty;

        if (_isFadingOut && !interruptFadeOut)
        {
            _pendingLine = new PendingLine(text, fadeInMs, onFadeInCompleted);
            return;
        }

        if (interruptFadeOut)
        {
            _pendingLine = null;
            _isFadingOut = false;
        }

        if (_isVisibleContent && string.Equals(_currentText, text, StringComparison.Ordinal))
        {
            onFadeInCompleted?.Invoke();
            return;
        }

        _currentText = text ?? string.Empty;
        _lyricText.Text = _currentText;
        _playedLyricText.Text = _currentText;
        _isVisibleContent = true;
        _isFadingOut = false;
        _playbackTextOffset = 0;
        UpdatePlayedClip();

        ApplyLetterSpacing();
        FitTextToWidth();
        ApplyGlyphForegrounds(_lyricRow, _unplayedLyricBrush);
        ApplyGlyphForegrounds(_playedLyricRow, _playedLyricBrush);
        UpdatePlayedClip();
        StartVisibleEffect();
        FadeTo(OpacityTarget, fadeInMs, onFadeInCompleted);
    }

    /// <summary>按当前播放器位置更新从左至右的已播放区域，不重启动画。</summary>
    public void UpdatePlaybackProgress(
        LyricLineTiming? line,
        TimeSpan position,
        FramesConfig style,
        FrameConfig frame)
    {
        _playbackSweepEnabled = style.PlaybackEffect == PlaybackEffectMode.LeftToRight
            && line is not null
            && string.Equals(line.Text, _currentText, StringComparison.Ordinal);
        _playbackTextOffset = _playbackSweepEnabled && line is not null
            ? LyricTimeline.GetTextOffsetAt(line, position)
            : 0;
        ApplyPlaybackBrushes(style);
        UpdatePlayedClip();
    }

    private void ApplyPlaybackBrushes(FramesConfig style)
    {
        if (ReferenceEquals(_appliedBaseBrush, _baseLyricBrush)
            && _appliedPlaybackSweep == _playbackSweepEnabled
            && string.Equals(_appliedPlayedTextColor, style.PlayedTextColor, StringComparison.Ordinal)
            && string.Equals(_appliedUnplayedTextColor, style.UnplayedTextColor, StringComparison.Ordinal))
        {
            return;
        }

        _appliedBaseBrush = _baseLyricBrush;
        _appliedPlaybackSweep = _playbackSweepEnabled;
        _appliedPlayedTextColor = style.PlayedTextColor;
        _appliedUnplayedTextColor = style.UnplayedTextColor;

        if (!_playbackSweepEnabled)
        {
            _unplayedLyricBrush = _baseLyricBrush;
            _playedLyricBrush = _baseLyricBrush;
        }
        else
        {
            _playedLyricBrush = string.IsNullOrWhiteSpace(style.PlayedTextColor)
                ? _baseLyricBrush
                : ParseBrush(style.PlayedTextColor, Colors.White);
            _unplayedLyricBrush = string.IsNullOrWhiteSpace(style.UnplayedTextColor)
                ? LyricBrushFactory.WithOpacity(_baseLyricBrush, 0.3)
                : ParseBrush(style.UnplayedTextColor, Colors.White);
        }

        _lyricText.Foreground = _unplayedLyricBrush;
        _playedLyricText.Foreground = _playedLyricBrush;
        ApplyGlyphForegrounds(_lyricRow, _unplayedLyricBrush);
        ApplyGlyphForegrounds(_playedLyricRow, _playedLyricBrush);
    }

    private void ApplyGlyphForegrounds(StackPanel row, Brush brush)
    {
        if (Math.Abs(_letterSpacing) < 0.01 || row.Children.Count != _currentText.Length)
        {
            return;
        }

        if (brush is not LinearGradientBrush gradient)
        {
            foreach (TextBlock glyph in row.Children)
            {
                glyph.Foreground = brush;
            }
            return;
        }

        var widths = GetSpacedTextWidths();
        var totalWidth = widths[^1];
        for (var index = 0; index < row.Children.Count; index++)
        {
            var middle = totalWidth <= 0 ? 0 : (widths[index] + widths[index + 1]) / (2 * totalWidth);
            ((TextBlock)row.Children[index]).Foreground = LyricBrushFactory.CreateSolid(
                LyricBrushFactory.SampleGradient(gradient, middle));
        }
    }

    private void UpdatePlayedClip()
    {
        if (!_playbackSweepEnabled || _currentText.Length == 0 || _playbackTextOffset <= 0)
        {
            _playedClip.Rect = new Rect(0, 0, 0, 0);
            return;
        }

        RefreshTextMetricsDpi();
        var characterOffset = Math.Clamp(_playbackTextOffset, 0, _currentText.Length);
        var clipWidth = Math.Abs(_letterSpacing) >= 0.01
            ? MeasureSpacedTextOffset(characterOffset)
            : MeasureTextOffset(characterOffset);
        var height = Math.Max(_playedLyricRow.ActualHeight, FrameHeight);
        var clip = new Rect(0, -height, Math.Max(0, clipWidth), height * 3);
        if (_playedClip.Rect != clip)
        {
            _playedClip.Rect = clip;
        }
    }

    private double MeasureSpacedTextOffset(double characterOffset)
    {
        var widths = GetSpacedTextWidths();
        var whole = Math.Clamp((int)Math.Floor(characterOffset), 0, widths.Length - 1);
        var fraction = Math.Clamp(characterOffset - whole, 0, 1);
        return whole < widths.Length - 1
            ? widths[whole] + (widths[whole + 1] - widths[whole]) * fraction
            : widths[whole];
    }

    private double MeasureTextOffset(double characterOffset)
    {
        var whole = Math.Clamp((int)Math.Floor(characterOffset), 0, _currentText.Length);
        var fraction = Math.Clamp(characterOffset - whole, 0, 1);
        var prefixWidth = GetPrefixTextWidth(whole);
        if (whole >= _currentText.Length || fraction <= 0)
        {
            return prefixWidth;
        }

        var nextWidth = GetPrefixTextWidth(whole + 1);
        return prefixWidth + Math.Max(0, nextWidth - prefixWidth) * fraction;
    }

    private void InvalidateTextMetrics()
    {
        _prefixTextWidths = null;
        _spacedTextWidths = null;
        _measuredTypeface = null;
    }

    private void RefreshTextMetricsDpi()
    {
        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (_metricsPixelsPerDip != pixelsPerDip)
        {
            _metricsPixelsPerDip = pixelsPerDip;
            InvalidateTextMetrics();
        }
    }

    private double[] GetSpacedTextWidths()
    {
        RefreshTextMetricsDpi();
        if (_spacedTextWidths is not null)
        {
            return _spacedTextWidths;
        }

        _lyricRow.Measure(new Size(double.PositiveInfinity, Math.Max(FrameHeight, 1)));
        var widths = new double[_lyricRow.Children.Count + 1];
        for (var index = 0; index < _lyricRow.Children.Count; index++)
        {
            // FrameworkElement.DesiredSize already includes Margin. Counting it again
            // makes both the highlight boundary and gradient stop positions drift.
            widths[index + 1] = widths[index] + ((TextBlock)_lyricRow.Children[index]).DesiredSize.Width;
        }

        _spacedTextWidths = widths;
        return widths;
    }

    private double GetPrefixTextWidth(int length)
    {
        if (length == 0)
        {
            return 0;
        }

        if (_prefixTextWidths is null)
        {
            _prefixTextWidths = new double[_currentText.Length + 1];
            Array.Fill(_prefixTextWidths, double.NaN);
            _prefixTextWidths[0] = 0;
        }

        if (!double.IsNaN(_prefixTextWidths[length]))
        {
            return _prefixTextWidths[length];
        }

        _measuredTypeface ??= new Typeface(
            _lyricText.FontFamily,
            _lyricText.FontStyle,
            _lyricText.FontWeight,
            _lyricText.FontStretch);
        var formatted = new FormattedText(
            _currentText[..length],
            System.Globalization.CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,
            _measuredTypeface,
            _lyricText.FontSize,
            System.Windows.Media.Brushes.Black,
            _metricsPixelsPerDip);
        return _prefixTextWidths[length] = formatted.WidthIncludingTrailingWhitespace;
    }

    /// <summary>
    /// 按当前字间距排版当前这句歌词。
    ///
    /// 实现方式是**逐字排版**：给每个字生成一个 TextBlock，横向排在一行里，
    /// 用每个字的 Margin 精确控制像素间距。
    ///
    /// 为什么不用"插入细空格字符"那种偷懒做法：
    /// 细空格宽度是字体决定的、无法精确控制，实测把字间距设成 20
    /// 也只让文字宽了几个像素，用户根本看不出区别。
    /// 逐字排版才能做到"设多少就是多少像素"。
    ///
    /// 间距为 0 时走单个 TextBlock 的普通路径（性能更好，也不用重建视觉树）。
    /// </summary>
    private void ApplyLetterSpacing()
    {
        InvalidateTextMetrics();
        var raw = _currentText ?? string.Empty;

        if (Environment.GetEnvironmentVariable("LYRICDESKTOP_LS_LOG") == "1")
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "lyricdesktop-ls.log"),
                    $"{DateTime.Now:HH:mm:ss.fff}  ApplyLetterSpacing 间距={_letterSpacing} 文本长度={raw.Length} 行内元素={_lyricRow.Children.Count}{Environment.NewLine}");
            }
            catch (System.IO.IOException) { }
        }
        if (Math.Abs(_letterSpacing) < 0.01)
        {
            // 普通路径：一个 TextBlock 装整句
            _lyricRow.Children.Clear();
            _lyricRow.Children.Add(_lyricText);
            _playedLyricRow.Children.Clear();
            _playedLyricRow.Children.Add(_playedLyricText);

            if (!string.Equals(_lyricText.Text, raw, StringComparison.Ordinal))
            {
                _lyricText.Text = raw;
            }

            _lyricText.FontStretch = FontStretches.Normal;
            _lyricText.Margin = new Thickness(0);
            _playedLyricText.FontStretch = FontStretches.Normal;
            _playedLyricText.Margin = new Thickness(0);
            return;
        }

        // 逐字路径：先清掉旧的字块（保留 _lyricText 本身备用）
        _lyricRow.Children.Clear();
        _playedLyricRow.Children.Clear();

        var half = _letterSpacing / 2.0;

        foreach (var ch in raw)
        {
            var glyph = new TextBlock
            {
                Text = ch.ToString(),
                FontFamily = _lyricText.FontFamily,
                FontSize = _lyricText.FontSize,
                FontWeight = _lyricText.FontWeight,
                Foreground = _lyricText.Foreground,
                IsHitTestVisible = false,
                // 用左右各一半的间距，保证字与字之间正好是设定的像素数
                Margin = new Thickness(half, 0, half, 0),
            };

            _lyricRow.Children.Add(glyph);
            _playedLyricRow.Children.Add(new TextBlock
            {
                Text = ch.ToString(),
                FontFamily = _playedLyricText.FontFamily,
                FontSize = _playedLyricText.FontSize,
                FontWeight = _playedLyricText.FontWeight,
                Foreground = _playedLyricBrush,
                IsHitTestVisible = false,
                Margin = new Thickness(half, 0, half, 0),
            });
        }

        // 间距为负时再配合字形压缩，否则字会叠在一起看不清
        if (_letterSpacing < 0)
        {
            foreach (var child in _lyricRow.Children.OfType<TextBlock>())
            {
                child.FontStretch = FontStretch.FromOpenTypeStretch(3);   // SemiCondensed
            }
            foreach (var child in _playedLyricRow.Children.OfType<TextBlock>())
            {
                child.FontStretch = FontStretch.FromOpenTypeStretch(3);
            }
        }

    }
    /// <summary>长句临时缩小字号，短句恢复配置字号；最多缩到原字号的六成。</summary>
    private void FitTextToWidth()
    {
        var available = FrameWidth - 24;   // 留一点左右内边距，别贴着边框
        if (available <= 0 || _configuredFontSize <= 0)
        {
            return;
        }

        // 逐字排版时，宽度取决于 _lyricRow 整体；字间距为 0 时看 _lyricText
        var letterSpacingOn = Math.Abs(_letterSpacing) >= 0.01;
        var target = letterSpacingOn ? (FrameworkElement)_lyricRow : _lyricText;

        var floor = _configuredFontSize * MinFontScale;
        SetFontSize(_configuredFontSize);

        // 用 WPF 的测量机制算文字实际要多宽，不需要真的去渲染
        target.Measure(new Size(double.PositiveInfinity, FrameHeight));

        if (target.DesiredSize.Width <= available)
        {
            return;
        }

        // 按比例估算一次，再逐档微调——比二分法简单，而且够用
        var estimated = _configuredFontSize * (available / target.DesiredSize.Width);

        for (var size = Math.Max(floor, estimated); size >= floor; size -= 1)
        {
            SetFontSize(size);
            target.Measure(new Size(double.PositiveInfinity, FrameHeight));

            if (target.DesiredSize.Width <= available)
            {
                return;
            }
        }

        // 压到下限还是装不下，就保持下限字号，由截断显示省略号
        SetFontSize(floor);
    }

    /// <summary>淡出消失。</summary>
    public void FadeOut(int fadeOutMs, bool clearPending = true)
    {
        if (clearPending)
        {
            _pendingLine = null;
        }

        if (_isFadingOut || (!_isVisibleContent && Opacity <= 0.001))
        {
            return;
        }

        _isVisibleContent = false;
        _isFadingOut = true;

        // 文字的切换要等淡出结束再做，否则会看到"新文字突然出现又淡出"
        FadeTo(0.0, fadeOutMs, onCompleted: () =>
        {
            _isFadingOut = false;
            if (!_isVisibleContent)
            {
                _lyricText.Text = string.Empty;
                _playedLyricText.Text = string.Empty;
                _currentText = string.Empty;
                InvalidateTextMetrics();
                _playbackTextOffset = 0;
                _playbackSweepEnabled = false;
                UpdatePlayedClip();
                _particlesCanvas.Children.Clear();
                StopMotionEffect();
            }

            var pending = _pendingLine;
            _pendingLine = null;
            if (pending is not null)
            {
                ShowLine(pending.Text, pending.FadeInMs, onFadeInCompleted: pending.OnFadeInCompleted);
            }
        });
    }


    /// <summary>
    /// 用 Storyboard 动画整窗不透明度。
    /// 用 DoubleAnimation 而不是自己写定时器：动画跑在 WPF 的渲染线程上，
    /// 不受界面定时器节拍限制，看起来才顺滑。
    /// </summary>
    private void FadeTo(double target, int durationMs, Action? onCompleted = null)
    {
        var from = Opacity;
        var version = ++_opacityAnimationVersion;
        BeginAnimation(OpacityProperty, null);
        Opacity = from;

        if (durationMs <= 0)
        {
            Opacity = target;
            if (version == _opacityAnimationVersion)
            {
                onCompleted?.Invoke();
            }

            return;
        }

        var animation = new DoubleAnimation
        {
            From = from,
            To = target,
            Duration = TimeSpan.FromMilliseconds(durationMs),
            FillBehavior = FillBehavior.HoldEnd,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };

        if (onCompleted is not null)
        {
            animation.Completed += (_, _) =>
            {
                if (version == _opacityAnimationVersion)
                {
                    onCompleted();
                }
            };
        }

        BeginAnimation(OpacityProperty, animation);
    }

    private static Brush ParseBrush(string hex, Color fallback)
        => LyricBrushFactory.CreateSolid(hex, fallback);

    /// <summary>让文字随效果轻微移动；粒子只在每句歌词出现时生成一小簇。</summary>
    private void ApplyLyricEffect(
        LyricEffectMode effect,
        double intensity,
        LyricMotionDirection direction,
        int periodMs,
        double particleSize,
        int particleCount,
        double particleSpread,
        int particleDurationMs,
        Color particleColor)
    {
        if (!Enum.IsDefined(effect))
        {
            effect = LyricEffectMode.None;
        }

        intensity = Math.Clamp(double.IsFinite(intensity) ? intensity : 0.55, 0.1, 1.0);
        if (!Enum.IsDefined(direction))
        {
            direction = LyricMotionDirection.Vertical;
        }

        periodMs = Math.Clamp(periodMs, 200, 5000);
        particleSize = Math.Clamp(double.IsFinite(particleSize) ? particleSize : 6, 2, 18);
        particleCount = Math.Clamp(particleCount, 4, 32);
        particleSpread = Math.Clamp(double.IsFinite(particleSpread) ? particleSpread : 72, 12, 220);
        particleDurationMs = Math.Clamp(particleDurationMs, 300, 5000);
        var isMotionEffect = effect is LyricEffectMode.Shake or LyricEffectMode.Float;
        var motionSettingsChanged = isMotionEffect
            && (_appliedMotionDirection != direction || _appliedMotionPeriodMs != periodMs);
        var particleSettingsChanged = effect == LyricEffectMode.Particles
            && (_appliedParticleSize != particleSize
                || _appliedParticleCount != particleCount
                || _appliedParticleSpread != particleSpread
                || _appliedParticleDurationMs != particleDurationMs
                || _appliedParticleColor != particleColor);
        if (_appliedLyricEffect == effect
            && Math.Abs(_appliedEffectIntensity - intensity) < 0.001
            && !motionSettingsChanged
            && !particleSettingsChanged)
        {
            return;
        }

        StopMotionEffect();
        _particlesCanvas.Children.Clear();
        _appliedLyricEffect = effect;
        _appliedEffectIntensity = intensity;
        _appliedMotionDirection = direction;
        _appliedMotionPeriodMs = periodMs;
        _appliedParticleSize = particleSize;
        _appliedParticleCount = particleCount;
        _appliedParticleSpread = particleSpread;
        _appliedParticleDurationMs = particleDurationMs;
        _appliedParticleColor = particleColor;
        _particleColor = particleColor;

        if (_isVisibleContent)
        {
            StartVisibleEffect();
        }
    }

    private void StartVisibleEffect()
    {
        StopMotionEffect();
        var amplitude = 2 + 5 * Math.Clamp(_appliedEffectIntensity, 0.1, 1.0);
        var axis = _appliedMotionDirection == LyricMotionDirection.Horizontal
            ? TranslateTransform.XProperty
            : TranslateTransform.YProperty;
        // WPF AutoReverse doubles the forward duration, so half the configured round-trip period is one leg.
        var halfCycle = TimeSpan.FromMilliseconds(_appliedMotionPeriodMs / 2.0);
        switch (_appliedLyricEffect)
        {
            case LyricEffectMode.Shake:
                StartMotion(axis, -amplitude * 0.45, amplitude * 0.45, halfCycle);
                break;
            case LyricEffectMode.Float:
                StartMotion(axis, -amplitude, amplitude, halfCycle);
                break;
            case LyricEffectMode.Particles:
                SpawnParticleBurst();
                break;
        }
    }

    private void StopMotionEffect()
    {
        _effectTranslation.BeginAnimation(TranslateTransform.XProperty, null);
        _effectTranslation.BeginAnimation(TranslateTransform.YProperty, null);
        _effectTranslation.X = 0;
        _effectTranslation.Y = 0;
    }

    private void StartMotion(DependencyProperty property, double from, double to, TimeSpan duration)
    {
        var animation = new DoubleAnimation(from, to, duration)
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        };

        _effectTranslation.BeginAnimation(property, animation);
    }

    private void SpawnParticleBurst()
    {
        _particlesCanvas.Children.Clear();
        if (_appliedLyricEffect != LyricEffectMode.Particles || !_isVisibleContent)
        {
            return;
        }

        var particleCount = _appliedParticleCount;
        var size = _appliedParticleSize;
        var spread = _appliedParticleSpread;
        var burstDuration = _appliedParticleDurationMs;
        var textWidth = Math.Min(FrameWidth - 24, Math.Max(0, _lyricRow.DesiredSize.Width));
        var textHeight = Math.Min(FrameHeight - 8, Math.Max(_lyricText.FontSize, _lyricRow.DesiredSize.Height));
        if (Math.Abs(_letterSpacing) < 0.01)
        {
            textWidth = Math.Min(FrameWidth - 24, Math.Max(0, _lyricText.DesiredSize.Width));
            textHeight = Math.Min(FrameHeight - 8, Math.Max(_lyricText.FontSize, _lyricText.DesiredSize.Height));
        }

        var centerX = FrameWidth / 2;
        var centerY = FrameHeight / 2;
        var halfWidth = Math.Max(8, textWidth / 2 + 5);
        var halfHeight = Math.Max(8, textHeight / 2 + 5);
        var particleBrush = LyricBrushFactory.CreateSolid(
            Color.FromArgb(0xEE, _particleColor.R, _particleColor.G, _particleColor.B));

        for (var i = 0; i < particleCount; i++)
        {
            var angle = Math.PI * 2 * i / particleCount;
            var directionX = Math.Cos(angle);
            var directionY = Math.Sin(angle);
            var edgeDistance = Math.Min(
                halfWidth / Math.Max(0.001, Math.Abs(directionX)),
                halfHeight / Math.Max(0.001, Math.Abs(directionY)));
            var startX = centerX + directionX * edgeDistance;
            var startY = centerY + directionY * edgeDistance;
            var particle = new System.Windows.Shapes.Ellipse
            {
                Width = size,
                Height = size,
                Fill = particleBrush,
                Opacity = 0,
                IsHitTestVisible = false,
                RenderTransform = new TranslateTransform(),
            };
            System.Windows.Controls.Canvas.SetLeft(particle, startX - size / 2);
            System.Windows.Controls.Canvas.SetTop(particle, startY - size / 2);
            _particlesCanvas.Children.Add(particle);

            var delayMs = particleCount <= 1 ? 0 : 60.0 * i / (particleCount - 1);
            var duration = TimeSpan.FromMilliseconds(Math.Max(1, burstDuration - delayMs));
            var fade = new DoubleAnimationUsingKeyFrames
            {
                BeginTime = TimeSpan.FromMilliseconds(delayMs),
                Duration = duration,
                FillBehavior = FillBehavior.Stop,
            };
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new EasingDoubleKeyFrame(0.9, KeyTime.FromPercent(0.16)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            fade.Completed += (_, _) => _particlesCanvas.Children.Remove(particle);
            particle.BeginAnimation(UIElement.OpacityProperty, fade);

            var translation = (TranslateTransform)particle.RenderTransform;
            var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            translation.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(0, directionX * spread, duration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                });
            translation.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(0, directionY * spread, duration)
                {
                    BeginTime = TimeSpan.FromMilliseconds(delayMs),
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.Stop,
                });
        }
    }

    private static Color ParseColor(string hex, Color fallback)
        => LyricBrushFactory.ParseColor(hex, fallback);

    // ---------------------------------------------------------------
    // 锁定（鼠标穿透）
    // ---------------------------------------------------------------

    /// <summary>
    /// 加 WS_EX_TRANSPARENT 让系统命中测试跳过这个窗口，鼠标事件落到它下面。
    /// 锁定时连拖动也一并禁掉——否则"锁定"名不副实。
    /// </summary>
    public void ApplyLocked(bool locked)
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle != IntPtr.Zero)
        {
            var exStyle = GetWindowLong(handle, GwlExStyle);
            var newStyle = locked
                ? exStyle | WsExLayered | WsExTransparent
                : exStyle & ~WsExTransparent;

            if (newStyle != exStyle)
            {
                SetWindowLong(handle, GwlExStyle, newStyle);
            }
        }


    }

}
