using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace LyricCanvas.App.Settings;

/// <summary>设置页滚动条的显隐与动画。</summary>
public partial class SettingsWindow
{
    private void SettingsContentScroll_Loaded(object sender, RoutedEventArgs e)
    {
        if (_settingsVerticalScrollBar is not null)
        {
            return;
        }

        SettingsContentScroll.ApplyTemplate();
        _settingsVerticalScrollBar = FindVisualDescendant<ScrollBar>(SettingsContentScroll);
        if (_settingsVerticalScrollBar is null)
        {
            return;
        }

        _settingsVerticalScrollBar.Style = (Style)FindResource("SettingsScrollBarStyle");
        _settingsVerticalScrollBar.Opacity = 0;
        _settingsVerticalScrollBar.IsHitTestVisible = false;
        _settingsVerticalScrollBar.MouseEnter += SettingsScrollBar_MouseEnter;
        _settingsVerticalScrollBar.MouseLeave += SettingsScrollBar_MouseLeave;
    }

    private static T? FindVisualDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match
                && (match is not ScrollBar bar || bar.Orientation == System.Windows.Controls.Orientation.Vertical))
            {
                return match;
            }

            if (FindVisualDescendant<T>(child) is { } descendant)
            {
                return descendant;
            }
        }

        return null;
    }

    private void SettingsContentScroll_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.VerticalChange != 0 || e.ExtentHeightChange != 0 || e.ViewportHeightChange != 0)
        {
            RevealSettingsScrollBar();
        }
    }

    private void SettingsContentScroll_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var pointer = e.GetPosition(SettingsContentScroll);
        var revealZone = Math.Max(14, (_settingsVerticalScrollBar?.ActualWidth ?? 8) + 6);
        if (pointer.X >= SettingsContentScroll.ActualWidth - revealZone)
        {
            RevealSettingsScrollBar();
        }
    }

    private void SettingsScrollBar_MouseEnter(object sender, MouseEventArgs e)
    {
        _settingsScrollBarPointerOver = true;
        RevealSettingsScrollBar(keepVisible: true);
    }

    private void SettingsScrollBar_MouseLeave(object sender, MouseEventArgs e)
    {
        _settingsScrollBarPointerOver = false;
        RevealSettingsScrollBar();
    }

    private void RevealSettingsScrollBar(bool keepVisible = false)
    {
        if (_settingsVerticalScrollBar is not { IsVisible: true } scrollBar)
        {
            return;
        }

        _settingsScrollBarHideTimer.Stop();
        scrollBar.IsHitTestVisible = true;
        AnimateSettingsScrollBarOpacity(1);
        if (!keepVisible && !_settingsScrollBarPointerOver)
        {
            _settingsScrollBarHideTimer.Start();
        }
    }

    private void SettingsScrollBarHideTimer_Tick(object? sender, EventArgs e)
    {
        _settingsScrollBarHideTimer.Stop();
        if (_settingsScrollBarPointerOver || _settingsVerticalScrollBar is not { IsVisible: true } scrollBar)
        {
            return;
        }

        scrollBar.IsHitTestVisible = false;
        AnimateSettingsScrollBarOpacity(0);
    }

    private void AnimateSettingsScrollBarOpacity(double target)
    {
        if (_settingsVerticalScrollBar is not { } scrollBar)
        {
            return;
        }

        var current = scrollBar.Opacity;
        scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
        scrollBar.Opacity = current;
        if (Math.Abs(current - target) < 0.01)
        {
            scrollBar.Opacity = target;
            return;
        }

        var animationVersion = ++_settingsScrollBarAnimationVersion;
        var animation = new DoubleAnimation(current, target, TimeSpan.FromMilliseconds(160))
        {
            FillBehavior = FillBehavior.Stop,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        animation.Completed += (_, _) =>
        {
            if (animationVersion != _settingsScrollBarAnimationVersion)
            {
                return;
            }

            scrollBar.BeginAnimation(UIElement.OpacityProperty, null);
            scrollBar.Opacity = target;
        };
        scrollBar.BeginAnimation(UIElement.OpacityProperty, animation);
    }

    /// <summary>用配置里的值填充所有控件。</summary>
}
