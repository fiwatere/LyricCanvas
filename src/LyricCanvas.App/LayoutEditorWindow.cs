using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LyricCanvas.Core;

// 项目同时开了 UseWPF 和 UseWindowsForms，两边的隐式 using 会撞名，明确指定 WPF 版本
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Button = System.Windows.Controls.Button;
using ComboBox = System.Windows.Controls.ComboBox;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using FontFamily = System.Windows.Media.FontFamily;
using Cursors = System.Windows.Input.Cursors;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using Image = System.Windows.Controls.Image;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MessageBox = System.Windows.MessageBox;
using Orientation = System.Windows.Controls.Orientation;
using Point = System.Windows.Point;
using TextBox = System.Windows.Controls.TextBox;
using VerticalAlignment = System.Windows.VerticalAlignment;

namespace LyricCanvas.App;

/// <summary>
/// 全屏布局编辑器：管理布局方案，并拖拽调整歌词框的位置、大小和角度。
///
/// 设计要点：
///   1. **背景默认纯黑**，可以用"截取屏幕"或"导入图片"铺一张参考图，
///      对着游戏画面摆位置。（截图和调整分成两步，绕开了"编辑器一出现
///      游戏就不是前台、截不到"这个死结。）
///   2. **编辑期间临时解除锁定**，否则框是穿透的、根本拖不动；关闭时恢复。
///   3. **实时预览**：改动的框立刻反映到真正的歌词窗口上，所见即所得。
///
/// 关于背景图：截图会先隐藏所有歌词框再拍，避免拍到文字残影；
/// 截图和导入图片都只保留在内存中，不写入临时目录，也不复制或修改原文件。
/// </summary>
public sealed class LayoutEditorWindow : Window
{
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureLayeredWindows = 0x40000000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr GetDC(IntPtr windowHandle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern int ReleaseDC(IntPtr windowHandle, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr deviceContext, int width, int height);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BitBlt(
        IntPtr destinationContext,
        int destinationX,
        int destinationY,
        int width,
        int height,
        IntPtr sourceContext,
        int sourceX,
        int sourceY,
        uint rasterOperation);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr gdiObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr deviceContext);

    private readonly ConfigService _config;
    private readonly WindowManager _windowManager;
    private readonly Canvas _canvas;

    /// <summary>承载内容的圆角面板。背景色画在它身上，圆角才有效。</summary>
    private readonly Border _backgroundPanel;
    private readonly Border _toolbar;
    private readonly ComboBox _layoutSelector = new();


    private readonly Dictionary<string, Border> _visuals = new(StringComparer.Ordinal);

    // 用 Tag 标记"这个元素属于哪个框"，供窗口级 Preview 处理反查归属。
    // 靠 Name 区分方框本体和缩放小方块。
    private const string FrameTag = "FrameVisual";
    private const string ResizeGripTag = "ResizeGrip";
    private const string RotateHandleTag = "RotateHandle";

    // 当前正在进行的交互（同一时刻只会有一个）
    private FrameConfig? _draggingFrame;
    private Border? _draggingVisual;
    private Point _dragStartPointer;
    private double _dragStartLeft;
    private double _dragStartTop;

    private FrameConfig? _resizingFrame;
    private Border? _resizingVisual;
    private Point _gripStart;
    private FrameConfig? _rotatingFrame;
    private Border? _rotatingVisual;
    private double _rotationStartDegrees;
    private double _rotationStartPointerAngle;
    private bool _suppressLayoutSelection;
    private System.Windows.Threading.DispatcherTimer? _captureTimer;

    /// <summary>编辑前的锁定状态，关闭时恢复。</summary>
    private readonly Dictionary<(string LayoutId, string FrameId), bool> _originalLocked = new();

    public LayoutEditorWindow(ConfigService config, WindowManager windowManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));

        Title = "调整歌词布局";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Topmost = true;
        ShowInTaskbar = false;

        // 覆盖整个虚拟屏幕（多显示器时也能覆盖全）
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        // 窗口自己的背景必须是透明的，圆角才露得出来——
        // 否则窗口矩形和圆角面板之间那四个角会是方的实心色块。
        Background = Brushes.Transparent;

        _canvas = new Canvas();
        var hintText = new TextBlock
        {
            Text = "拖动歌词框调整位置，拖右下角改大小，拖顶部圆点旋转。"
                 + "  背景可以用「截取屏幕」或「导入图片」铺上去。  按 Esc 或「完成」结束。",
            Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
            FontSize = 15,
            Margin = new Thickness(16, 60, 16, 0),
            TextWrapping = TextWrapping.Wrap,
            IsHitTestVisible = false,
        };

        _toolbar = BuildToolbar();

        var inner = new Grid();
        inner.Children.Add(_canvas);
        inner.Children.Add(hintText);
        inner.Children.Add(_toolbar);

        // 圆角面板承载全部内容。黑色背景画在这一层而不是窗口上，
        // 这样边角才能被裁成圆的。
        _backgroundPanel = new Border
        {
            CornerRadius = new CornerRadius(12),
            Background = Brushes.Black,
            Child = inner,
        };

        Content = _backgroundPanel;

        // 用窗口级 Preview 路由接住鼠标输入，再按命中位置查找所属方框
        AttachUnifiedMouseHandling();

        // 编辑期间必须能拖，所以临时全部解锁
        UnlockAllForEditing();
        BuildVisuals();

        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                Close();
            }
        };

        // 测试钩子：用**代码**模拟一次拖动，绕开鼠标输入。
        //
        // 存在的理由：本机用 SendInput 合成鼠标时，"按下"事件经常投递不到程序里
        // （日志里能看到按了之后 e.LeftButton 仍是 Released），
        // 导致没法用自动化验证拖动。这个钩子直接走"拖动成功后应该发生的事"，
        // 用它来区分两件事：
        //   - 钩子能让歌词框移动 -> 配置传到窗口这条路是通的，
        //     问题只在鼠标事件有没有触发；
        //   - 钩子也不能移动 -> 问题在配置到窗口的同步上，跟鼠标无关。
        // 只在显式设置环境变量时生效，正常使用完全不受影响。
        if (Environment.GetEnvironmentVariable("LYRICDESKTOP_EDITOR_SELFTEST") == "1")
        {
            Loaded += (_, _) => RunDragSelfTest();
        }

        // 编辑器是全屏覆盖窗口，圆角由内层的圆角 Border 负责（系统圆角对它没意义，
        // 因为窗口铺满整屏）。这里不加 DwmSetWindowAttribute，避免无谓调用。

        Closing += (_, _) => RestoreLocked();
        Closed += (_, _) =>
        {
            _captureTimer?.Stop();
            _captureTimer = null;
        };
    }

    /// <summary>
    /// 顶部工具条。
    /// </summary>
    private Border BuildToolbar()
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };

        panel.Children.Add(new TextBlock
        {
            Text = "布局方案",
            Foreground = Brushes.White,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        });

        _layoutSelector.Width = 160;
        _layoutSelector.Height = 32;
        _layoutSelector.Margin = new Thickness(0, 0, 8, 0);
        _layoutSelector.DisplayMemberPath = nameof(LayoutSchemeConfig.Name);
        _layoutSelector.SelectedValuePath = nameof(LayoutSchemeConfig.Id);
        _layoutSelector.SelectionChanged += LayoutSelector_SelectionChanged;
        RefreshLayoutSelector();
        panel.Children.Add(_layoutSelector);

        panel.Children.Add(MakeButton("新增方案", AddLayoutScheme));
        panel.Children.Add(MakeButton("重命名", RenameLayoutScheme));
        panel.Children.Add(MakeButton("删除方案", RemoveLayoutScheme));

        panel.Children.Add(MakeButton("截取屏幕", CaptureScreen));
        panel.Children.Add(MakeButton("导入图片", ImportImage));
        panel.Children.Add(MakeButton("清除背景", ClearBackground));
        panel.Children.Add(MakeButton("加一个框", AddFrame));
        panel.Children.Add(MakeButton("删掉最后一个", RemoveLastFrame));
        panel.Children.Add(MakeButton("完成", Close));

        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE0, 0x1F, 0x1F, 0x22)),
            Padding = new Thickness(12, 8, 12, 8),
            VerticalAlignment = VerticalAlignment.Top,
            Child = panel,
        };
    }

    private Button MakeButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 0),
            Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x3A, 0x3A, 0x40)),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            FontSize = 13,
        };

        button.Click += (_, _) => action();
        return button;
    }

    // ---------------------------------------------------------------
    // 视觉元素
    // ---------------------------------------------------------------

    private void RefreshLayoutSelector()
    {
        var config = _config.Current;
        _suppressLayoutSelection = true;
        try
        {
            _layoutSelector.ItemsSource = config.LayoutSchemes;
            _layoutSelector.SelectedValue = config.ActiveLayoutId;
        }
        finally
        {
            _suppressLayoutSelection = false;
        }
    }

    private void LayoutSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressLayoutSelection || _layoutSelector.SelectedValue is not string id
            || string.Equals(id, _config.Current.ActiveLayoutId, StringComparison.Ordinal))
        {
            return;
        }

        _config.Update(c => c.ActiveLayoutId = id);
        UnlockCurrentFramesForEditing();
        RefreshLayoutSelector();
        BuildVisuals();
        _windowManager.SyncFrames();
        SaveConfigOrWarn();
    }

    private void AddLayoutScheme()
    {
        var config = _config.Current;
        if (config.LayoutSchemes.Count >= 20)
        {
            ShowMessage("最多保存 20 套布局方案。", MessageBoxImage.Information);
            return;
        }

        var name = PromptForLayoutName("新增布局方案", "布局方案");
        if (name is null || !ValidateLayoutName(name))
        {
            return;
        }

        var id = Guid.NewGuid().ToString("N")[..8];
        var frames = config.Frames.Frames.Select(frame =>
        {
            var copy = frame.Clone();
            // 同一框在不同布局方案中保留身份。WindowManager 因此可以直接更新
            // 坐标，而不必关闭并重建歌词窗口（重建会让正在播放的动态效果重启）。
            copy.Locked = _originalLocked.TryGetValue((config.ActiveLayoutId, frame.Id), out var locked)
                ? locked
                : frame.Locked;
            return copy;
        }).ToList();

        _config.Update(c =>
        {
            c.LayoutSchemes.Add(new LayoutSchemeConfig { Id = id, Name = name, Frames = frames });
            c.ActiveLayoutId = id;
        });

        UnlockCurrentFramesForEditing();
        RefreshLayoutSelector();
        BuildVisuals();
        _windowManager.SyncFrames();
        SaveConfigOrWarn();
    }

    private void RenameLayoutScheme()
    {
        var config = _config.Current;
        var active = config.LayoutSchemes.FirstOrDefault(layout => layout.Id == config.ActiveLayoutId);
        if (active is null)
        {
            return;
        }

        var name = PromptForLayoutName("重命名布局方案", active.Name);
        if (name is null || !ValidateLayoutName(name, active.Id))
        {
            return;
        }

        _config.Update(c =>
        {
            var target = c.LayoutSchemes.FirstOrDefault(layout => layout.Id == active.Id);
            if (target is not null)
            {
                target.Name = name;
            }
        });
        RefreshLayoutSelector();
        SaveConfigOrWarn();
    }

    private void RemoveLayoutScheme()
    {
        var config = _config.Current;
        if (config.LayoutSchemes.Count <= 1)
        {
            ShowMessage("至少保留一套布局方案。", MessageBoxImage.Information);
            return;
        }

        var activeId = config.ActiveLayoutId;
        var active = config.LayoutSchemes.FirstOrDefault(layout => layout.Id == activeId);
        if (active is null)
        {
            return;
        }

        var replacementId = config.LayoutSchemes.First(layout => layout.Id != activeId).Id;
        var answer = ShowMessage(
            $"删除布局方案“{active.Name}”？",
            MessageBoxImage.Question,
            MessageBoxButton.YesNo);
        if (answer != MessageBoxResult.Yes)
        {
            return;
        }

        _config.Update(c =>
        {
            c.LayoutSchemes.RemoveAll(layout => layout.Id == activeId);
            c.ActiveLayoutId = replacementId;
        });

        UnlockCurrentFramesForEditing();
        RefreshLayoutSelector();
        BuildVisuals();
        _windowManager.SyncFrames();
        SaveConfigOrWarn();
    }

    private void SaveConfigOrWarn()
    {
        if (_config.SaveNow())
        {
            return;
        }

        if (!_config.TryMarkSaveFailureReported())
        {
            return;
        }

        var reason = _config.LastSaveError;
        ShowMessage(
            $"布局或设置没有保存。\n配置文件：{_config.FilePath}\n原因：{reason ?? "未知错误"}",
            MessageBoxImage.Warning);
    }

    private bool ValidateLayoutName(string name, string? exceptId = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowMessage("方案名称不能为空。", MessageBoxImage.Information);
            return false;
        }

        if (_config.Current.LayoutSchemes.Any(layout =>
                layout.Id != exceptId && string.Equals(layout.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
        {
            ShowMessage("已经有同名方案，请换一个名称。", MessageBoxImage.Information);
            return false;
        }

        return true;
    }

    private MessageBoxResult ShowMessage(
        string message,
        MessageBoxImage image,
        MessageBoxButton buttons = MessageBoxButton.OK)
    {
        return IsLoaded
            ? MessageBox.Show(this, message, Title, buttons, image)
            : MessageBox.Show(message, Title, buttons, image);
    }

    private string? PromptForLayoutName(string title, string initialName)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 360,
            Height = 150,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = this,
            ShowInTaskbar = false,
            Background = new SolidColorBrush(Color.FromRgb(0x24, 0x25, 0x2A)),
        };

        var input = new TextBox
        {
            Text = initialName,
            Margin = new Thickness(0, 8, 0, 12),
            Padding = new Thickness(8, 5, 8, 5),
            FontSize = 14,
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        var accept = new Button { Content = "确定", MinWidth = 72, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "取消", MinWidth = 72, IsCancel = true };
        accept.Click += (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(input.Text))
            {
                dialog.DialogResult = true;
            }
        };
        buttons.Children.Add(accept);
        buttons.Children.Add(cancel);

        var content = new StackPanel { Margin = new Thickness(16) };
        content.Children.Add(new TextBlock { Text = "输入便于识别的方案名称。", Foreground = Brushes.White });
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private void BuildVisuals()
    {
        _canvas.Children.Clear();
        _visuals.Clear();

        var index = 0;
        foreach (var frame in _config.Current.Frames.Frames)
        {
            if (!frame.Enabled)
            {
                continue;
            }

            index++;
            var visual = CreateFrameVisual(frame, index);
            _visuals[frame.Id] = visual;
            _canvas.Children.Add(visual);
        }
    }

    /// <summary>
    /// 造一个可拖拽、可缩放的框。
    /// 用 Canvas + 直接设 Left/Top，比 Grid 更适合"绝对定位"这种场景。
    /// </summary>
    private Border CreateFrameVisual(FrameConfig frame, int ordinal)
    {
        var label = new TextBlock
        {
            Text = frame.GetDisplayName(ordinal - 1),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            FontSize = 11,
            Margin = new Thickness(6, 2, 0, 0),
        };

        var content = new Grid();
        content.Children.Add(label);

        // 框里放一段**示例文本**，并且按真实的字号、字体、颜色渲染。
        //
        // 为什么不是写死一句"歌词会显示在这一块里"：
        // 调布局时最需要判断的是"这么大的框，歌词放进去够不够、好不好看"。
        // 用配置里的真实样式渲染一句长度接近真实歌词的话，才能一眼看出
        // 字号合不合适、会不会被截断。这是用户提出来的建议，确实比固定提示有用。
        //
        // 中英混排是为了顺便能看出字体对两种文字的观感差异。
        var style = _config.Current.Frames;
        var sampleText = new TextBlock
        {
            Text = "示例歌词 Sample Lyric",
            Foreground = ParseBrush(frame.TextColor ?? style.TextColor),
            FontFamily = FontCatalog.CreateFontFamily(style.FontFamily),
            FontWeight = FontWeights.Bold,
            FontSize = Math.Max(8, frame.FontSize ?? style.FontSize),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
            Margin = new Thickness(4, 0, 4, 0),
            IsHitTestVisible = false,
        };
        if (style.GlowRadius > 0.1 && style.GlowOpacity > 0.001)
        {
            var glowBrush = ParseBrush(style.GlowColor) as SolidColorBrush;
            sampleText.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                Color = glowBrush?.Color ?? Color.FromRgb(0xFF, 0xB8, 0x3D),
                BlurRadius = style.GlowRadius,
                ShadowDepth = 0,
                Opacity = style.GlowOpacity,
                RenderingBias = System.Windows.Media.Effects.RenderingBias.Performance,
            };
        }

        content.Children.Add(sampleText);

        var visual = new Border
        {
            Name = FrameTag,
            Tag = frame,
            Width = frame.Width,
            Height = frame.Height,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = Math.Abs(frame.RotationDegrees) < 0.001
                ? System.Windows.Media.Transform.Identity
                : new RotateTransform(frame.RotationDegrees),
            Background = new SolidColorBrush(Color.FromArgb(0x22, 0x5B, 0x8D, 0xEF)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x8D, 0xEF)),
            BorderThickness = new Thickness(2),
            Cursor = Cursors.SizeAll,
            Child = content,
        };

        Canvas.SetLeft(visual, frame.Left - SystemParameters.VirtualScreenLeft);
        Canvas.SetTop(visual, frame.Top - SystemParameters.VirtualScreenTop);

        // 让占位文字不参与命中测试：否则它会在方框中心"接住"命中，
        // 虽然事件最终仍会冒泡到 Border，但会让 InputHitTest 的排查结论误导人。
        // 直接把它排除掉，命中的就一定是 Border 本身。
        label.IsHitTestVisible = false;
        foreach (var child in content.Children.OfType<TextBlock>())
        {
            child.IsHitTestVisible = false;
        }

        content.Children.Add(new Border
        {
            Name = RotateHandleTag,
            Tag = frame,
            Width = 16,
            Height = 16,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.FromRgb(0xFF, 0xD8, 0x66)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x5B, 0x8D, 0xEF)),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 4, 0, 0),
            Cursor = Cursors.Hand,
            ToolTip = "拖动旋转歌词框",
        });

        content.Children.Add(new Border
        {
            Name = ResizeGripTag,
            Tag = frame,
            Width = 14,
            Height = 14,
            Background = Brushes.White,
            BorderBrush = new SolidColorBrush(Color.FromArgb(0xFF, 0x5B, 0x8D, 0xEF)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Cursor = Cursors.SizeNWSE,
        });

        return visual;
    }

    /// <summary>
    /// 拖动与缩放的统一实现。
    ///
    /// 事件挂在 Window 的 Preview 路由上：实测窗口能收到 PreviewMouseDown，
    /// 但根面板和方框的冒泡 MouseLeftButtonDown 都收不到。
    /// 在事件进入子元素前处理，既绕开这段失效的冒泡路由，也能保留工具条按钮的正常点击。
    /// </summary>
    private void AttachUnifiedMouseHandling()
    {
        PreviewMouseDown += OnEditorMouseDown;
        PreviewMouseMove += OnEditorMouseMove;
        PreviewMouseUp += OnEditorMouseUp;
        _backgroundPanel.LostMouseCapture += (_, _) => EndInteraction(save: false);
    }

    private void OnEditorMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        var hit = e.OriginalSource as DependencyObject;
        var position = e.GetPosition(_canvas);
        var rotateOwner = FindOwner(hit, RotateHandleTag);
        var gripOwner = FindOwner(hit, ResizeGripTag);
        var owner = FindOwner(hit, FrameTag);

        // 某些输入只把窗口本身作为 OriginalSource。用 WPF 的内容命中测试补查，
        // 但不穿过工具栏查找，避免按钮上方恰好有歌词框时误启动拖动。
        if (rotateOwner is null && gripOwner is null && owner is null && !IsToolbarHit(hit))
        {
            var originalHit = hit;
            hit = _canvas.InputHitTest(position) as DependencyObject;
            rotateOwner = FindOwner(hit, RotateHandleTag);
            gripOwner = FindOwner(hit, ResizeGripTag);
            owner = FindOwner(hit, FrameTag);
            Log($"DOWN-HIT source={originalHit?.GetType().Name} fallback={hit?.GetType().Name} pos={position}");
        }

        if (rotateOwner is not null)
        {
            _rotatingFrame = rotateOwner;
            _rotatingVisual = FindVisual(rotateOwner);
            if (_rotatingVisual is null)
            {
                EndInteraction(save: false);
                return;
            }

            var center = GetFrameCenter(_rotatingVisual);
            _rotationStartDegrees = rotateOwner.RotationDegrees;
            _rotationStartPointerAngle = GetPointerAngle(position, center);
            Log($"DOWN-ROTATE 框={rotateOwner.Id} 当前角度={rotateOwner.RotationDegrees:0.#}°");
        }
        else if (gripOwner is not null)
        {
            _resizingFrame = gripOwner;
            _resizingVisual = FindVisual(gripOwner);
            _gripStart = position;
            Log($"DOWN-GRIP 框={gripOwner.Id}");
        }
        else
        {
            if (owner is null)
            {
                return;   // 点在空白处，不处理
            }

            _draggingFrame = owner;
            _draggingVisual = FindVisual(owner);
            if (_draggingVisual is null)
            {
                _draggingFrame = null;
                return;
            }

            _dragStartPointer = position;
            _dragStartLeft = Canvas.GetLeft(_draggingVisual);
            _dragStartTop = Canvas.GetTop(_draggingVisual);
            Log($"DOWN  框={owner.Id} 命中元素={hit?.GetType().Name}");
        }

        if (!_backgroundPanel.CaptureMouse())
        {
            Log("CAPTURE-FAILED");
            EndInteraction(save: false);
            return;
        }

        e.Handled = true;
    }

    private bool IsToolbarHit(DependencyObject? node)
    {
        while (node is not null)
        {
            if (ReferenceEquals(node, _toolbar))
            {
                return true;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return false;
    }

    private void OnEditorMouseMove(object sender, MouseEventArgs e)
    {
        if (_draggingFrame is null && _resizingFrame is null && _rotatingFrame is null)
        {
            return;
        }

        var position = e.GetPosition(_canvas);

        if (_draggingFrame is not null && _draggingVisual is not null)
        {
            var left = _dragStartLeft + position.X - _dragStartPointer.X;
            var top = _dragStartTop + position.Y - _dragStartPointer.Y;

            Canvas.SetLeft(_draggingVisual, left);
            Canvas.SetTop(_draggingVisual, top);

            ApplyFrameGeometry(
                _draggingFrame,
                left + SystemParameters.VirtualScreenLeft,
                top + SystemParameters.VirtualScreenTop,
                null,
                null);

            Log($"MOVE  框={_draggingFrame.Id} 新位置=({left + SystemParameters.VirtualScreenLeft},{top + SystemParameters.VirtualScreenTop})");
        }
        else if (_resizingFrame is not null && _resizingVisual is not null)
        {
            var deltaX = position.X - _gripStart.X;
            var deltaY = position.Y - _gripStart.Y;
            _gripStart = position;

            var radians = _resizingFrame.RotationDegrees * Math.PI / 180;
            var localDeltaX = Math.Cos(radians) * deltaX + Math.Sin(radians) * deltaY;
            var localDeltaY = -Math.Sin(radians) * deltaX + Math.Cos(radians) * deltaY;

            var newWidth = Math.Max(80, _resizingVisual.Width + localDeltaX);
            var newHeight = Math.Max(30, _resizingVisual.Height + localDeltaY);

            _resizingVisual.Width = newWidth;
            _resizingVisual.Height = newHeight;

            ApplyFrameGeometry(_resizingFrame, null, null, newWidth, newHeight);

            Log($"RESIZE 框={_resizingFrame.Id} 新尺寸=({newWidth:0},{newHeight:0})");
        }
        else if (_rotatingFrame is not null && _rotatingVisual is not null)
        {
            var center = GetFrameCenter(_rotatingVisual);
            var pointerAngle = GetPointerAngle(position, center);
            var angle = NormalizeAngle(_rotationStartDegrees + pointerAngle - _rotationStartPointerAngle);
            _rotatingVisual.RenderTransform = Math.Abs(angle) < 0.001
                ? System.Windows.Media.Transform.Identity
                : new RotateTransform(angle);
            ApplyFrameGeometry(_rotatingFrame, null, null, null, null, angle);
        }
    }

    private void OnEditorMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            EndInteraction(save: true);
        }
    }

    private void EndInteraction(bool save)
    {
        if (_draggingFrame is null && _resizingFrame is null && _rotatingFrame is null)
        {
            return;
        }

        Log($"UP    {(_draggingFrame?.Id ?? _resizingFrame?.Id ?? _rotatingFrame?.Id)} 保存={save}");

        _draggingFrame = null;
        _draggingVisual = null;
        _resizingFrame = null;
        _resizingVisual = null;
        _rotatingFrame = null;
        _rotatingVisual = null;

        _backgroundPanel.ReleaseMouseCapture();

        if (save)
        {
            // 拖动过程是每帧写配置（自动保存有防抖），松手时再明确落盘一次，
            // 保证位置一定存住
            SaveConfigOrWarn();
        }
    }

    /// <summary>
    /// 把一个框的位置/尺寸写回配置。
    ///
    /// 必须走 _config.Update，不能直接改 frame 对象的属性——
    /// ConfigService.Current 每次返回的是**深拷贝**，
    /// 在副本上改的值写不回真配置（歌词窗口不会动，也存不到磁盘）。
    /// 传 null 表示这一项不改。
    /// </summary>
    private void ApplyFrameGeometry(
        FrameConfig frame,
        double? left,
        double? top,
        double? width,
        double? height,
        double? rotationDegrees = null)
    {
        _config.Update(c =>
        {
            var target = c.Frames.Frames.FirstOrDefault(f => f.Id == frame.Id);
            if (target is null)
            {
                return;
            }

            if (left is { } l) { target.Left = l; }
            if (top is { } t) { target.Top = t; }
            if (width is { } w) { target.Width = w; }
            if (height is { } h) { target.Height = h; }
            if (rotationDegrees is { } rotation) { target.RotationDegrees = rotation; }
        });

        // 本地对象同步一份，方便后续读取时值是一致的
        if (left is { } l2) { frame.Left = l2; }
        if (top is { } t2) { frame.Top = t2; }
        if (width is { } w2) { frame.Width = w2; }
        if (height is { } h2) { frame.Height = h2; }
        if (rotationDegrees is { } r2) { frame.RotationDegrees = r2; }
    }

    private static Point GetFrameCenter(Border visual)
        => new(Canvas.GetLeft(visual) + visual.Width / 2, Canvas.GetTop(visual) + visual.Height / 2);

    private static double GetPointerAngle(Point pointer, Point center)
        => Math.Atan2(pointer.Y - center.Y, pointer.X - center.X) * 180 / Math.PI;

    private static double NormalizeAngle(double angle)
    {
        var normalized = (angle + 180) % 360;
        if (normalized < 0)
        {
            normalized += 360;
        }

        return normalized - 180;
    }

    /// <summary>
    /// 从命中的元素往上找，看它属于哪个方框。
    /// 用 Tag 标记归属，而不是靠引用比较父链——后者在
    /// 元素层次调整后会失效。
    /// </summary>
    private static FrameConfig? FindOwner(DependencyObject? node, string tag)
    {
        while (node is not null)
        {
            if (node is FrameworkElement fe && fe.Tag is FrameConfig frame && fe.Name == tag)
            {
                return frame;
            }

            node = VisualTreeHelper.GetParent(node);
        }

        return null;
    }

    /// <summary>按配置对象找回它对应的方框视觉元素。</summary>
    private Border? FindVisual(FrameConfig frame)
        => _visuals.TryGetValue(frame.Id, out var visual) ? visual : null;




    // ---------------------------------------------------------------
    // 增删框
    // ---------------------------------------------------------------

    private void AddFrame()
    {
        var frames = _config.Current.Frames;

        _config.Update(c =>
        {
            if (c.Frames.Frames.Count >= FramesConfig.MaxFrameCount)
            {
                return;
            }

            var index = c.Frames.Frames.Count;
            c.Frames.Frames.Add(new FrameConfig
            {
                Name = $"歌词框 {index + 1}",
                Left = SystemParameters.VirtualScreenLeft + 100 + (index * 40),
                Top = SystemParameters.VirtualScreenTop + 120 + (index * 60),
                Width = 700,
                Height = 80,
                Locked = true,
            });
        });

        UnlockCurrentFramesForEditing();
        BuildVisuals();
        _windowManager.SyncFrames();
    }

    private void RemoveLastFrame()
    {
        if (_config.Current.Frames.Frames.Count <= 1)
        {
            ShowMessage("至少要保留一个歌词框。", MessageBoxImage.Information);
            return;
        }

        _config.Update(c =>
        {
            if (c.Frames.Frames.Count > 1)
            {
                c.Frames.Frames.RemoveAt(c.Frames.Frames.Count - 1);
            }
        });

        BuildVisuals();
        _windowManager.SyncFrames();
    }

    // ---------------------------------------------------------------
    // 背景图
    // ---------------------------------------------------------------

    /// <summary>
    /// 截取屏幕当背景。
    ///
    /// 先最小化自己、等一会儿再拍，给用户时间切回要参考的那个程序
    /// （比如游戏）。这是截图类软件的标准做法。
    /// </summary>
    private void CaptureScreen()
    {
        if (_captureTimer is not null)
        {
            return;
        }

        WindowState = WindowState.Minimized;
        Hide();

        // 给用户留出切窗口的时间
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1.5),
        };
        _captureTimer = timer;

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _captureTimer = null;
            Exception? captureError = null;
            BitmapSource? capturedImage = null;

            try
            {
                capturedImage = CaptureScreenCore();
            }
            catch (Exception ex)
            {
                captureError = ex;
            }
            finally
            {
                try
                {
                    if (IsLoaded)
                    {
                        WindowState = WindowState.Normal;
                        Show();
                        Activate();
                    }
                }
                catch (Exception ex)
                {
                    captureError ??= ex;
                }
            }

            if (captureError is not null)
            {
                ShowMessage($"截屏失败：{captureError.Message}", MessageBoxImage.Warning);
                return;
            }

            if (capturedImage is not null)
            {
                try
                {
                    SetBackgroundImage(capturedImage);
                }
                catch (Exception ex)
                {
                    ShowMessage($"截屏成功，但无法显示背景图：{ex.Message}", MessageBoxImage.Warning);
                }
            }
        };

        timer.Start();
    }

    private BitmapSource CaptureScreenCore()
    {
        // 先临时隐藏所有歌词框，免得把文字残影拍进背景图
        var hidden = new List<Window>();
        try
        {
            foreach (var window in _windowManager.Windows.Values)
            {
                if (window.IsVisible)
                {
                    window.Hide();
                    hidden.Add(window);
                }
            }

            // 给系统一点时间处理隐藏，否则可能拍到还没消失的窗口
            System.Threading.Thread.Sleep(120);

            // WPF 的 SystemParameters 使用 DIP；屏幕拷贝使用设备像素。
            // 用 WinForms 屏幕边界避免高 DPI 下把逻辑尺寸误当成物理像素。
            var screenBounds = System.Windows.Forms.Screen.AllScreens
                .Select(screen => screen.Bounds)
                .ToArray();
            if (screenBounds.Length == 0)
            {
                throw new InvalidOperationException("系统没有报告可用显示器，无法截取屏幕。");
            }

            var bounds = System.Drawing.Rectangle.FromLTRB(
                screenBounds.Min(screen => screen.Left),
                screenBounds.Min(screen => screen.Top),
                screenBounds.Max(screen => screen.Right),
                screenBounds.Max(screen => screen.Bottom));
            if (bounds.Width <= 0 || bounds.Height <= 0 || (long)bounds.Width * bounds.Height > 80_000_000)
            {
                throw new InvalidOperationException("虚拟屏幕范围无效或过大，无法安全截取。");
            }

            var screenDc = GetDC(IntPtr.Zero);
            if (screenDc == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法访问屏幕绘图上下文。");
            }

            IntPtr memoryDc = IntPtr.Zero;
            IntPtr bitmapHandle = IntPtr.Zero;
            IntPtr previousBitmap = IntPtr.Zero;
            try
            {
                memoryDc = CreateCompatibleDC(screenDc);
                if (memoryDc == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法创建屏幕截图缓冲区。");
                }

                bitmapHandle = CreateCompatibleBitmap(screenDc, bounds.Width, bounds.Height);
                if (bitmapHandle == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法分配屏幕截图位图。");
                }

                previousBitmap = SelectObject(memoryDc, bitmapHandle);
                if (previousBitmap == IntPtr.Zero || previousBitmap == new IntPtr(-1))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "无法选中屏幕截图位图。");
                }

                if (!BitBlt(memoryDc, 0, 0, bounds.Width, bounds.Height,
                        screenDc, bounds.Left, bounds.Top, SrcCopy | CaptureLayeredWindows))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 无法复制当前屏幕内容。");
                }

                var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    bitmapHandle,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                image.Freeze();

                return image;
            }
            finally
            {
                if (memoryDc != IntPtr.Zero)
                {
                    if (previousBitmap != IntPtr.Zero && previousBitmap != new IntPtr(-1))
                    {
                        SelectObject(memoryDc, previousBitmap);
                    }

                    DeleteDC(memoryDc);
                }

                if (bitmapHandle != IntPtr.Zero)
                {
                    DeleteObject(bitmapHandle);
                }

                ReleaseDC(IntPtr.Zero, screenDc);
            }
        }
        finally
        {
            Exception? restoreError = null;
            foreach (var window in hidden)
            {
                try
                {
                    if (window.IsLoaded)
                    {
                        window.Show();
                    }
                }
                catch (Exception ex)
                {
                    // 一个窗口状态异常不能阻止其它歌词窗口继续恢复。
                    restoreError ??= ex;
                }
            }

            if (restoreError is not null)
            {
                throw new InvalidOperationException("恢复歌词窗口时发生错误。", restoreError);
            }
        }
    }

    private void ImportImage()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择一张图片当背景",
            Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            SetBackgroundImage(LoadBackgroundImage(dialog.FileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or NotSupportedException or FormatException or ArgumentException)
        {
            ShowMessage($"图片读取失败：{ex.Message}", MessageBoxImage.Warning);
        }
    }

    private void ClearBackground() => _backgroundPanel.Background = Brushes.Black;

    private static BitmapImage LoadBackgroundImage(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        var image = new BitmapImage();
        image.BeginInit();
        image.StreamSource = stream;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private void SetBackgroundImage(BitmapSource image)
    {
        _backgroundPanel.Background = new ImageBrush(image)
        {
            Stretch = Stretch.Fill,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
        };
    }

    // ---------------------------------------------------------------
    // 锁定状态
    // ---------------------------------------------------------------

    private void UnlockAllForEditing()
    {
        _originalLocked.Clear();

        UnlockCurrentFramesForEditing();
    }

    private void UnlockCurrentFramesForEditing()
    {
        var config = _config.Current;
        foreach (var frame in config.Frames.Frames)
        {
            _originalLocked.TryAdd((config.ActiveLayoutId, frame.Id), frame.Locked);
        }

        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                frame.Locked = false;
            }
        });
    }

    /// <summary>
    /// 把调试信息写到 %TEMP%\lyricdesktop-editor.log。
    ///
    /// 只在环境变量 LYRICDESKTOP_EDITOR_LOG=1 时写，平时一个字节都不落盘。
    /// 为什么要日志而不是断点：这类"拖不动"的问题，关键信息是
    /// "鼠标事件到底有没有到那个元素上"，靠读代码猜不出来。
    /// </summary>
    /// <summary>
    /// 把 "#AARRGGBB" 这样的颜色字符串转成画刷。
    /// 解析失败就退回白色，不让一个写错的颜色把编辑器搞崩。
    /// </summary>
    private static Brush ParseBrush(string? hex)
    {
        if (string.IsNullOrWhiteSpace(hex))
        {
            return Brushes.White;
        }

        try
        {
            var converted = ColorConverter.ConvertFromString(hex);
            if (converted is Color color)
            {
                return new SolidColorBrush(color);
            }
        }
        catch (FormatException)
        {
        }

        return Brushes.White;
    }

    /// <summary>
    /// 自检：模拟一次拖动，验证"改配置 → 歌词框跟着动"这条路通不通。
    /// 由 LYRICDESKTOP_EDITOR_SELFTEST=1 触发，结果写进编辑器日志。
    /// </summary>
    private void RunDragSelfTest()
    {
        var frame = _config.Current.Frames.Frames.FirstOrDefault();
        if (frame is null)
        {
            Log("SELFTEST  没有可用的框，跳过");
            return;
        }

        Log($"SELFTEST  开始  框={frame.Id} 原位置=({frame.Left},{frame.Top})");

        // 走的是和鼠标拖动**完全一样**的那段逻辑：通过 Update 写回配置
        _config.Update(c =>
        {
            var target = c.Frames.Frames.FirstOrDefault(f => f.Id == frame.Id);
            if (target is not null)
            {
                target.Left += 143;
                target.Top += 77;
            }
        });

        Log($"SELFTEST  已改配置为 ({frame.Left},{frame.Top})，等 1.5 秒看窗口有没有跟过去");

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500),
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            if (_windowManager.Windows.TryGetValue(frame.Id, out var window))
            {
                // 注意：要从 _config.Current 重新取，因为 Current 返回的是深拷贝，
                // 用旧的 frame 变量读出来的还是改动前的值（会让日志看起来像失败）
                var live = _config.Current.Frames.Frames.FirstOrDefault(f => f.Id == frame.Id);
                var expectedLeft = live?.Left ?? double.NaN;
                var expectedTop = live?.Top ?? double.NaN;
                var matched = Math.Abs(window.Left - expectedLeft) < 2
                              && Math.Abs(window.Top - expectedTop) < 2;

                Log($"SELFTEST  歌词窗口位置=({window.Left},{window.Top}) " +
                    $"配置位置=({expectedLeft},{expectedTop}) 一致={matched}");
                Log(matched
                    ? "SELFTEST  结论：配置改动能传到窗口，拖动逻辑的下游是通的"
                    : "SELFTEST  结论：配置改了但窗口没跟上，问题在同步环节");
            }
            else
            {
                Log($"SELFTEST  结论：找不到框 {frame.Id} 对应的窗口");
            }
        };

        timer.Start();
    }

    private static void Log(string message)
    {
        if (Environment.GetEnvironmentVariable("LYRICDESKTOP_EDITOR_LOG") != "1")
        {
            return;
        }

        try
        {
            var path = Path.Combine(Path.GetTempPath(), "lyricdesktop-editor.log");
            File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
        }
        catch (IOException)
        {
        }
    }

    private void RestoreLocked()
    {
        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                if (_originalLocked.TryGetValue((c.ActiveLayoutId, frame.Id), out var locked))
                {
                    frame.Locked = locked;
                }
            }

            foreach (var layout in c.LayoutSchemes)
            {
                foreach (var frame in layout.Frames)
                {
                    if (_originalLocked.TryGetValue((layout.Id, frame.Id), out var locked))
                    {
                        frame.Locked = locked;
                    }
                }
            }
        });

        _windowManager.SyncFrames();
        SaveConfigOrWarn();
    }

}
