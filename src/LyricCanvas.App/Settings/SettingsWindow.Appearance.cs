using System.IO;
using System.Windows;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LyricCanvas.App.Rendering;
using LyricCanvas.Core;
using Border = System.Windows.Controls.Border;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using Colors = System.Windows.Media.Colors;
using MessageBox = System.Windows.MessageBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace LyricCanvas.App.Settings;

/// <summary>歌词显示草稿、字体颜色、动态与播放效果。</summary>
public partial class SettingsWindow
{
    /// <summary>
    /// 内置的歌词颜色预设。点色块在这些颜色之间循环。
    /// 任意颜色和渐变可在颜色编辑器中选择。
    /// </summary>
    private static readonly (string Hex, string Name)[] TextColorPresets =
    {
        ("#FFFFFFFF", "白色"),
        ("#FFFFD866", "暖黄"),
        ("#FF7FE07F", "浅绿"),
        ("#FF6FC8FF", "天蓝"),
        ("#FFFF9ED2", "粉色"),
        ("#FFE0A0FF", "淡紫"),
        ("#FFFF8A65", "橙红"),
        ("#FF202020", "深灰"),
    };

    private static readonly (string Hex, string Name)[] GlowColorPresets =
    {
        ("#FFFFB83D", "金色"),
        ("#FFFF7A24", "橙色"),
        ("#FFFFE08A", "浅金"),
        ("#FF7FC8FF", "蓝色"),
        ("#FFE0A0FF", "淡紫"),
    };

    private static readonly (string Hex, string Name)[] SubTextColorPresets =
    {
        ("#B0FFFFFF", "半透明白"),
        ("#80FFFFFF", "更淡的白"),
        ("#B0FFD866", "淡黄"),
        ("#B06FC8FF", "淡蓝"),
        ("#00FFFFFF", "完全隐藏"),
    };

    private sealed record EffectChoice(LyricEffectMode Mode, string Name)
    {
        public override string ToString() => Name;
    }

    private static readonly EffectChoice[] LyricEffectChoices =
    {
        new(LyricEffectMode.None, "无"),
        new(LyricEffectMode.Shake, "上下晃动"),
        new(LyricEffectMode.Float, "左右晃动"),
        new(LyricEffectMode.Particles, "粒子散开"),
    };


    private void LoadLyricsDisplayControls(AppConfig config)
    {
        var frames = config.Frames;
        OpacitySlider.Value = frames.Opacity;
        OpacityValue.Text = $"{frames.Opacity * 100:0}%";

        FontSizeSlider.Value = frames.FontSize;
        LetterSpacingSlider.Value = frames.LetterSpacing;
        FadeInSlider.Value = frames.FadeInMs;
        FadeOutSlider.Value = frames.FadeOutMs;
        HoldDurationSlider.Value = frames.HoldMs;
        HoldUntilNextFrameLineCheck.IsChecked = frames.HoldUntilNextFrameLine;
        GlowRadiusSlider.Value = frames.GlowRadius;
        GlowOpacitySlider.Value = frames.GlowOpacity;
        EffectIntensitySlider.Value = frames.EffectIntensity;
        EffectPeriodSlider.Value = frames.MotionPeriodMs;
        ParticleSizeSlider.Value = frames.ParticleSize;
        ParticleCountSlider.Value = frames.ParticleCount;
        ParticleSpreadSlider.Value = frames.ParticleSpread;
        ParticleDurationSlider.Value = frames.ParticleDurationMs;
        FontSizeValue.Text = $"{frames.FontSize:0}";
        FadeInValue.Text = FormatPlaybackDuration(frames.FadeInMs);
        FadeOutValue.Text = FormatPlaybackDuration(frames.FadeOutMs);
        HoldDurationValue.Text = frames.HoldMs == 0 ? "不限制" : FormatPlaybackDuration(frames.HoldMs);
        GlowRadiusValue.Text = $"{frames.GlowRadius:0.#}";
        GlowOpacityValue.Text = $"{frames.GlowOpacity * 100:0}%";
        EffectIntensityValue.Text = $"{frames.EffectIntensity * 100:0}%";
        EffectPeriodValue.Text = FormatMotionPeriod(frames.MotionPeriodMs);
        ParticleSizeValue.Text = $"{frames.ParticleSize:0.#} px";
        ParticleCountValue.Text = frames.ParticleCount.ToString();
        ParticleSpreadValue.Text = $"{frames.ParticleSpread:0} px";
        ParticleDurationValue.Text = FormatMotionPeriod(frames.ParticleDurationMs);

        FontFamilyCombo.SelectedValue = _fontChoices.Any(choice =>
            string.Equals(choice.Value, frames.FontFamily, StringComparison.Ordinal))
                ? frames.FontFamily
                : "Microsoft YaHei UI";
        LyricEffectCombo.SelectedValue = frames.LyricEffect;

        UpdateTextColorSwatch(frames);
        UpdateColorSwatch(SubTextColorSwatch, SubTextColorValue, frames.SubTextColor, SubTextColorPresets);
        UpdateColorSwatch(GlowColorSwatch, GlowColorValue, frames.GlowColor, GlowColorPresets);
        PlaybackFadeCheck.IsChecked = frames.PlaybackEffect == PlaybackEffectMode.Fade;
        PlaybackSweepCheck.IsChecked = frames.PlaybackEffect == PlaybackEffectMode.LeftToRight;
        UpdatePlaybackEffectControlAvailability();
        UpdatePlaybackColorControls(frames);
        ShowTrackInfoCheck.IsChecked = frames.ShowTrackInfo;
        UpdateEffectControlAvailability();

        ModeCustomTextRadio.IsChecked = config.ContentMode == ContentMode.CustomText;
        ModeLyricsRadio.IsChecked = config.ContentMode != ContentMode.CustomText;
        CustomTextBox.Text = config.CustomText;
        UpdateCustomTextAvailability();
    }

    private bool HasPendingLyricsSettings()
        => !LyricsSettingsEqual(_lyricsDraft, _lyricsBaseline);

    private static bool LyricsSettingsEqual(AppConfig left, AppConfig right)
    {
        var a = left.Frames;
        var b = right.Frames;
        return left.ContentMode == right.ContentMode
            && string.Equals(left.CustomText, right.CustomText, StringComparison.Ordinal)
            && a.Opacity == b.Opacity
            && a.FontSize == b.FontSize
            && string.Equals(a.FontFamily, b.FontFamily, StringComparison.Ordinal)
            && a.TextColor == b.TextColor
            && a.TextGradientColors.SequenceEqual(b.TextGradientColors, StringComparer.Ordinal)
            && a.PlayedTextColor == b.PlayedTextColor
            && a.UnplayedTextColor == b.UnplayedTextColor
            && a.SubTextColor == b.SubTextColor
            && a.GlowColor == b.GlowColor
            && a.GlowRadius == b.GlowRadius
            && a.GlowOpacity == b.GlowOpacity
            && a.ShowTrackInfo == b.ShowTrackInfo
            && a.LyricEffect == b.LyricEffect
            && a.EffectIntensity == b.EffectIntensity
            && a.MotionDirection == b.MotionDirection
            && a.MotionPeriodMs == b.MotionPeriodMs
            && a.ParticleSize == b.ParticleSize
            && a.ParticleCount == b.ParticleCount
            && a.ParticleSpread == b.ParticleSpread
            && a.ParticleDurationMs == b.ParticleDurationMs
            && a.FadeInMs == b.FadeInMs
            && a.FadeOutMs == b.FadeOutMs
            && a.PlaybackEffect == b.PlaybackEffect
            && a.HoldUntilNextFrameLine == b.HoldUntilNextFrameLine
            && a.LetterSpacing == b.LetterSpacing
            && a.HoldMs == b.HoldMs;
    }

    private void UpdateLyricsDraft(Action<AppConfig> update)
    {
        update(_lyricsDraft);
        _windowManager.PreviewLyricsSettings(_lyricsDraft);
        RefreshLyricsSettingsStatus();
    }

    private void RefreshLyricsSettingsStatus()
    {
        if (LyricsSettingsStatus is not null)
        {
            LyricsSettingsStatus.Text = HasPendingLyricsSettings()
                ? "有尚未确认的歌词显示更改。"
                : "歌词显示设置已保存。";
        }
    }

    private static void CopyLyricsSettings(AppConfig source, AppConfig destination)
    {
        CopyGlobalStyle(source.Frames, destination.Frames);
        destination.ContentMode = source.ContentMode;
        destination.CustomText = source.CustomText;
    }

    private static void CopyGlobalStyle(FramesConfig source, FramesConfig destination)
        => destination.CopyGlobalStyleFrom(source);

    private void ConfirmLyricsSettings_Click(object sender, RoutedEventArgs e)
    {
        TryConfirmLyricsSettings();
    }

    private bool TryConfirmLyricsSettings()
    {
        if (!HasPendingLyricsSettings())
        {
            LyricsSettingsStatus.Text = "当前没有待确认的修改。";
            return true;
        }

        var draft = _lyricsDraft.Clone();
        if (_config.UpdateAndSave(config => CopyLyricsSettings(draft, config)))
        {
            _lyricsDraft = _config.Current;
            _lyricsBaseline = _lyricsDraft.Clone();
            _windowManager.PreviewLyricsSettings(null);
            LyricsSettingsStatus.Text = "歌词显示设置已确认并保存。";
            return true;
        }

        LyricsSettingsStatus.Text = "保存失败；修改尚未应用，请修复权限后重试或取消。";
        if (_config.TryMarkSaveFailureReported())
        {
            MessageBox.Show(
                this,
                $"歌词显示设置没有保存。\n配置文件：{_config.FilePath}\n原因：{_config.LastSaveError ?? "未知错误"}",
                "词绘设置",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }

        return false;
    }

    private void CancelLyricsSettings_Click(object sender, RoutedEventArgs e)
        => DiscardLyricsSettingsDraft();

    private void DiscardLyricsSettingsDraft()
    {
        _lyricsDraft = _config.Current;
        _lyricsBaseline = _lyricsDraft.Clone();
        _windowManager.PreviewLyricsSettings(null);
        _suppressEvents = true;
        try
        {
            LoadLyricsDisplayControls(_lyricsDraft);
        }
        finally
        {
            _suppressEvents = false;
        }

        LyricsSettingsStatus.Text = "已取消未确认的修改。";
    }


    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (OpacityValue is null)
        {
            return;
        }

        OpacityValue.Text = $"{e.NewValue * 100:0}%";

        if (_suppressEvents)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.Opacity = e.NewValue);
    }

    private void FontSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FontSizeValue is null)
        {
            return;
        }

        FontSizeValue.Text = $"{e.NewValue:0}";

        if (_suppressEvents)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.FontSize = Math.Round(e.NewValue));
    }

    /// <summary>
    /// 字间距。0 是默认；负数更紧凑、正数更松散。
    /// 显示成"+"是为了让用户一眼看出这是相对默认值的偏移。
    /// </summary>
    private void LetterSpacingSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LetterSpacingValue is null)
        {
            return;
        }

        var value = Math.Round(e.NewValue, 1);
        LetterSpacingValue.Text = value > 0 ? $"+{value:0.#}" : $"{value:0.#}";

        if (_suppressEvents)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.LetterSpacing = value);
    }

    private void FadeInSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FadeInValue is null)
        {
            return;
        }

        var durationMs = (int)Math.Round(e.NewValue);
        FadeInValue.Text = FormatPlaybackDuration(durationMs);
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.FadeInMs = durationMs);
        }
    }

    private void FadeOutSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FadeOutValue is null)
        {
            return;
        }

        var durationMs = (int)Math.Round(e.NewValue);
        FadeOutValue.Text = FormatPlaybackDuration(durationMs);
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.FadeOutMs = durationMs);
        }
    }

    private void HoldDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressEvents || HoldDurationValue is null)
        {
            return;
        }

        var durationMs = (int)Math.Round(HoldDurationSlider.Value / 50.0) * 50;
        HoldDurationValue.Text = durationMs == 0 ? "不限制" : FormatPlaybackDuration(durationMs);
        UpdateLyricsDraft(c => c.Frames.HoldMs = durationMs);
    }

    private void HoldUntilNextFrameLineCheck_Click(object sender, RoutedEventArgs e)
    {
        if (!_suppressEvents)
        {
            var enabled = HoldUntilNextFrameLineCheck.IsChecked == true;
            UpdateLyricsDraft(c => c.Frames.HoldUntilNextFrameLine = enabled);
        }
    }

    private static string FormatPlaybackDuration(int durationMs)
        => $"{durationMs} ms";

    private void GlowRadiusSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlowRadiusValue is null)
        {
            return;
        }

        var value = Math.Round(e.NewValue, 1);
        GlowRadiusValue.Text = value.ToString("0.#");
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.GlowRadius = value);
        }
    }

    private void GlowOpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GlowOpacityValue is null)
        {
            return;
        }

        var value = Math.Round(e.NewValue, 2);
        GlowOpacityValue.Text = $"{value * 100:0}%";
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.GlowOpacity = value);
        }
    }

    private void LyricEffectCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || LyricEffectCombo.SelectedValue is not LyricEffectMode mode)
        {
            return;
        }

        UpdateLyricsDraft(c =>
        {
            c.Frames.LyricEffect = mode;
            if (mode is LyricEffectMode.Shake or LyricEffectMode.Float)
            {
                c.Frames.MotionDirection = mode == LyricEffectMode.Float
                    ? LyricMotionDirection.Horizontal
                    : LyricMotionDirection.Vertical;
            }
        });
        UpdateEffectControlAvailability();
    }

    private void EffectPeriodSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EffectPeriodValue is null)
        {
            return;
        }

        var periodMs = (int)Math.Round(e.NewValue / 100) * 100;
        EffectPeriodValue.Text = FormatMotionPeriod(periodMs);
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.MotionPeriodMs = periodMs);
        }
    }

    private static string FormatMotionPeriod(int periodMs)
        => $"{periodMs / 1000.0:0.0} 秒";

    private void ParticleSizeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParticleSizeValue is null)
        {
            return;
        }

        var size = Math.Round(e.NewValue, 1);
        ParticleSizeValue.Text = $"{size:0.#} px";
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.ParticleSize = size);
        }
    }

    private void ParticleCountSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParticleCountValue is null)
        {
            return;
        }

        var count = (int)Math.Round(e.NewValue);
        ParticleCountValue.Text = count.ToString();
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.ParticleCount = count);
        }
    }

    private void ParticleSpreadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParticleSpreadValue is null)
        {
            return;
        }

        var spread = Math.Round(e.NewValue);
        ParticleSpreadValue.Text = $"{spread:0} px";
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.ParticleSpread = spread);
        }
    }

    private void ParticleDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ParticleDurationValue is null)
        {
            return;
        }

        var durationMs = (int)Math.Round(e.NewValue / 100) * 100;
        ParticleDurationValue.Text = FormatMotionPeriod(durationMs);
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.ParticleDurationMs = durationMs);
        }
    }

    private void UpdateEffectControlAvailability()
    {
        if (MotionPeriodRow is null || MotionIntensityRow is null || ParticleSettingsPanel is null)
        {
            return;
        }

        var effect = _lyricsDraft.Frames.LyricEffect;
        var motionEnabled = effect is LyricEffectMode.Shake or LyricEffectMode.Float;
        MotionPeriodRow.Visibility = motionEnabled ? Visibility.Visible : Visibility.Collapsed;
        MotionIntensityRow.Visibility = motionEnabled ? Visibility.Visible : Visibility.Collapsed;
        ParticleSettingsPanel.Visibility = effect == LyricEffectMode.Particles
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private void EffectIntensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (EffectIntensityValue is null)
        {
            return;
        }

        var value = Math.Round(e.NewValue, 2);
        EffectIntensityValue.Text = $"{value * 100:0}%";
        if (!_suppressEvents)
        {
            UpdateLyricsDraft(c => c.Frames.EffectIntensity = value);
        }
    }

    private void FontFamilyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || FontFamilyCombo.SelectedItem is not FontCatalog.Choice choice)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.FontFamily = choice.Value);
    }

    private void ImportFontButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "导入字体",
            Filter = "字体文件 (*.ttf;*.otf)|*.ttf;*.otf",
            Multiselect = true,
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var imported = new List<FontCatalog.Choice>();
        var failures = new List<string>();
        foreach (var file in dialog.FileNames)
        {
            try
            {
                imported.AddRange(FontCatalog.Import(file));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
                                       or ExternalException)
            {
                failures.Add($"{Path.GetFileName(file)}：{ex.Message}");
            }
        }

        _fontChoices = FontCatalog.GetChoices();
        FontFamilyCombo.ItemsSource = _fontChoices;
        if (imported.Count > 0)
        {
            _suppressEvents = true;
            FontFamilyCombo.SelectedValue = imported[0].Value;
            _suppressEvents = false;
            UpdateLyricsDraft(c => c.Frames.FontFamily = imported[0].Value);
        }
        if (failures.Count > 0)
        {
            MessageBox.Show(this, "以下字体未能导入：" + Environment.NewLine + string.Join(Environment.NewLine, failures),
                "词绘", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ShowTrackInfoCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.ShowTrackInfo = ShowTrackInfoCheck.IsChecked == true);
    }

    private void TextColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        if (_lyricsDraft.Frames.TextGradientColors.Count > 0)
        {
            EditTextGradient_Click(sender, new RoutedEventArgs());
        }
        else
        {
            CycleColor(isSubText: false);
        }
    }

    private void EditTextGradient_Click(object sender, RoutedEventArgs e)
    {
        var frames = _lyricsDraft.Frames;
        var dialog = new GradientColorsWindow(frames.TextGradientColors, frames.TextColor)
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        UpdateLyricsDraft(c => c.Frames.TextGradientColors = dialog.SelectedColors.ToList());
        UpdateTextColorSwatch(_lyricsDraft.Frames);
    }

    private void PlaybackEffectCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var selectFade = ReferenceEquals(sender, PlaybackFadeCheck)
            ? PlaybackFadeCheck.IsChecked == true
            : PlaybackSweepCheck.IsChecked != true;

        _suppressEvents = true;
        PlaybackFadeCheck.IsChecked = selectFade;
        PlaybackSweepCheck.IsChecked = !selectFade;
        _suppressEvents = false;

        var mode = selectFade ? PlaybackEffectMode.Fade : PlaybackEffectMode.LeftToRight;
        UpdateLyricsDraft(c => c.Frames.PlaybackEffect = mode);
        UpdatePlaybackEffectControlAvailability();
    }

    private void UpdatePlaybackEffectControlAvailability()
    {
        var fadeSelected = _lyricsDraft.Frames.PlaybackEffect == PlaybackEffectMode.Fade;
        FadePlaybackOptions.Visibility = fadeSelected ? Visibility.Visible : Visibility.Collapsed;
        SweepPlaybackOptions.Visibility = fadeSelected ? Visibility.Collapsed : Visibility.Visible;
    }

    private void PlaybackColorCustomCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        if (ReferenceEquals(sender, PlayedColorCustomCheck))
        {
            var customColor = PlayedColorCustomCheck.IsChecked == true;
            UpdateLyricsDraft(c => c.Frames.PlayedTextColor = customColor
                ? c.Frames.PlayedTextColor ?? c.Frames.TextColor
                : null);
        }
        else
        {
            var customColor = UnplayedColorCustomCheck.IsChecked == true;
            UpdateLyricsDraft(c => c.Frames.UnplayedTextColor = customColor
                ? c.Frames.UnplayedTextColor ?? DimColor(c.Frames.TextColor, 0.3)
                : null);
        }

        UpdatePlaybackColorControls(_lyricsDraft.Frames);
    }

    private void PlaybackColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        var isPlayed = ReferenceEquals(sender, PlayedColorSwatch);
        var customCheck = isPlayed ? PlayedColorCustomCheck : UnplayedColorCustomCheck;
        if (customCheck.IsChecked != true)
        {
            return;
        }

        var current = isPlayed
            ? _lyricsDraft.Frames.PlayedTextColor
            : _lyricsDraft.Frames.UnplayedTextColor;
        var color = ParseColor(current ?? _lyricsDraft.Frames.TextColor);
        using var dialog = new System.Windows.Forms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(color.A, color.R, color.G, color.B),
        };

        if (dialog.ShowDialog() != System.Windows.Forms.DialogResult.OK)
        {
            return;
        }

        var selected = $"#{dialog.Color.A:X2}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        UpdateLyricsDraft(c =>
        {
            if (isPlayed)
            {
                c.Frames.PlayedTextColor = selected;
            }
            else
            {
                c.Frames.UnplayedTextColor = selected;
            }
        });
        UpdatePlaybackColorControls(_lyricsDraft.Frames);
    }

    private void UpdatePlaybackColorControls(FramesConfig frames)
    {
        var playedCustom = !string.IsNullOrWhiteSpace(frames.PlayedTextColor);
        var unplayedCustom = !string.IsNullOrWhiteSpace(frames.UnplayedTextColor);
        PlayedColorCustomCheck.IsChecked = playedCustom;
        UnplayedColorCustomCheck.IsChecked = unplayedCustom;
        PlayedColorSwatch.Cursor = playedCustom ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        UnplayedColorSwatch.Cursor = unplayedCustom ? System.Windows.Input.Cursors.Hand : System.Windows.Input.Cursors.Arrow;
        PlayedColorSwatch.IsHitTestVisible = playedCustom;
        UnplayedColorSwatch.IsHitTestVisible = unplayedCustom;

        PlayedColorSwatch.Background = playedCustom
            ? new SolidColorBrush(ParseColor(frames.PlayedTextColor!))
            : CreateGlobalTextBrush(frames);
        UnplayedColorSwatch.Background = unplayedCustom
            ? new SolidColorBrush(ParseColor(frames.UnplayedTextColor!))
            : DimBrush(CreateGlobalTextBrush(frames), 0.3);
        PlayedColorValue.Text = playedCustom ? frames.PlayedTextColor : "跟随全局颜色";
        UnplayedColorValue.Text = unplayedCustom ? frames.UnplayedTextColor : "跟随全局色并淡化";
    }

    private void UpdateTextColorSwatch(FramesConfig frames)
    {
        var colors = frames.TextGradientColors;
        TextColorSwatch.Background = colors.Count == 0
            ? new SolidColorBrush(ParseColor(frames.TextColor))
            : CreateGradientBrush(colors);
        TextColorValue.Text = colors.Count switch
        {
            0 => ColorName(frames.TextColor, TextColorPresets),
            1 => $"纯色 {colors[0]}",
            _ => $"渐变（{colors.Count} 色）",
        };
    }

    private static Brush CreateGlobalTextBrush(FramesConfig frames)
        => LyricBrushFactory.CreateTextBrush(frames, Colors.White);

    private static Brush CreateGradientBrush(IReadOnlyList<string> colors)
        => LyricBrushFactory.CreateGradient(colors, Colors.White);

    private static Brush DimBrush(Brush source, double opacity)
        => LyricBrushFactory.WithOpacity(source, opacity);

    private static string DimColor(string colorHex, double opacity)
    {
        var color = ParseColor(colorHex);
        var alpha = (byte)Math.Round(color.A * Math.Clamp(opacity, 0, 1));
        return $"#{alpha:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
    }

    private static string ColorName(string hex, (string Hex, string Name)[] presets)
    {
        var index = Array.FindIndex(presets, preset =>
            string.Equals(preset.Hex, hex, StringComparison.OrdinalIgnoreCase));
        return index >= 0 ? $"{presets[index].Name}（{hex}）" : hex;
    }

    private void SubTextColorSwatch_Click(object sender, MouseButtonEventArgs e)
        => CycleColor(isSubText: true);

    private void GlowColorSwatch_Click(object sender, MouseButtonEventArgs e)
    {
        var current = _lyricsDraft.Frames.GlowColor;
        var index = Array.FindIndex(GlowColorPresets, preset =>
            string.Equals(preset.Hex, current, StringComparison.OrdinalIgnoreCase));
        var next = GlowColorPresets[(index + 1) % GlowColorPresets.Length];
        UpdateLyricsDraft(c => c.Frames.GlowColor = next.Hex);
        UpdateColorSwatch(GlowColorSwatch, GlowColorValue, next.Hex, GlowColorPresets);
    }

    private void CycleColor(bool isSubText)
    {
        var presets = isSubText ? SubTextColorPresets : TextColorPresets;
        var current = isSubText ? _lyricsDraft.Frames.SubTextColor : _lyricsDraft.Frames.TextColor;

        var index = Array.FindIndex(presets, p => string.Equals(p.Hex, current, StringComparison.OrdinalIgnoreCase));
        var next = presets[(index + 1) % presets.Length];

        UpdateLyricsDraft(c =>
        {
            if (isSubText)
            {
                c.Frames.SubTextColor = next.Hex;
            }
            else
            {
                c.Frames.TextColor = next.Hex;
                c.Frames.TextGradientColors.Clear();
            }
        });

        if (isSubText)
        {
            UpdateColorSwatch(SubTextColorSwatch, SubTextColorValue, next.Hex, SubTextColorPresets);
        }
        else
        {
            UpdateTextColorSwatch(_lyricsDraft.Frames);
        }
    }

    private static void UpdateColorSwatch(
        Border swatch,
        TextBlock label,
        string hex,
        (string Hex, string Name)[] presets)
    {
        var color = ParseColor(hex);
        swatch.Background = new SolidColorBrush(color);

        var match = Array.FindIndex(presets, p => string.Equals(p.Hex, hex, StringComparison.OrdinalIgnoreCase));
        label.Text = match >= 0 ? $"{presets[match].Name}（{hex}）" : hex;
    }

    /// <summary>把 #AARRGGBB / #RRGGBB 解析成颜色；解析不了就退回白色。</summary>
    private static Color ParseColor(string hex)
        => LyricBrushFactory.ParseColor(hex, Colors.White);


    // ---------------------------------------------------------------
    // 显示内容（歌词 / 自定义文本）
    // ---------------------------------------------------------------

    /// <summary>
    /// 切换"显示内容"。
    ///
    /// 这个处理器挂在两个 RadioButton 的 Checked 事件上，会被触发两次
    /// （选中的那个报一次，取消选中的那个也报一次），
    /// 所以必须判断"现在到底哪个被选中"，不能直接用事件源。
    /// </summary>
    private void ContentMode_Checked(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents || CustomTextBox is null)
        {
            return;
        }

        var mode = ModeCustomTextRadio.IsChecked == true
            ? ContentMode.CustomText
            : ContentMode.Lyrics;

        UpdateLyricsDraft(c => c.ContentMode = mode);
        UpdateCustomTextAvailability();
    }

    /// <summary>自定义文本只更新本地草稿，直到用户确认后才应用和保存。</summary>
    private void CustomTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        UpdateLyricsDraft(c => c.CustomText = CustomTextBox.Text);
    }

    /// <summary>没选"自定义文本"时把输入框置灰，免得用户以为填了没生效。</summary>
    private void UpdateCustomTextAvailability()
    {
        if (CustomTextBox is null)
        {
            return;
        }

        var enabled = ModeCustomTextRadio.IsChecked == true;
        CustomTextBox.IsEnabled = enabled;
        CustomTextBox.Opacity = enabled ? 1.0 : 0.5;
    }

}
