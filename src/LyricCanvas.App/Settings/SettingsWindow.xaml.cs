using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using LyricCanvas.Core;
using MessageBox = System.Windows.MessageBox;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;

namespace LyricCanvas.App.Settings;

/// <summary>
/// 设置窗口沿用当前分组式布局，外壳借鉴 PCL 的标题栏与左侧导航风格。
///
/// 非歌词设置立即写入 ConfigService；歌词显示设置以本地草稿即时预览，但不会写入配置，
/// 直到确认并成功落盘才提交。取消时清除预览，让歌词窗口恢复当前配置。_suppressEvents 用于阻止程序刷新控件时反向写配置。
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly ConfigService _config;

    /// <summary>用来在改完布局后立刻让歌词框同步（比如点了"调整布局"回来）。</summary>
    private readonly WindowManager _windowManager;
    private IReadOnlyList<FontCatalog.Choice> _fontChoices = Array.Empty<FontCatalog.Choice>();

    /// <summary>为 true 时忽略控件事件，防止"程序改控件"被当成"用户改设置"。</summary>
    private bool _suppressEvents;
    private bool _isClosed;

    /// <summary>歌词显示页的草稿。单击确认前不写入共享配置，取消可直接丢弃。</summary>
    private AppConfig _lyricsDraft = new();
    private AppConfig _lyricsBaseline = new();

    private System.Windows.Threading.DispatcherTimer? _lyricsDirDebounce;
    private System.Windows.Threading.DispatcherTimer? _mediaAppAllowListDebounce;
    private readonly DispatcherTimer _mediaDiagnosticsTimer = new();
    private readonly DispatcherTimer _settingsScrollBarHideTimer = new();
    private ScrollBar? _settingsVerticalScrollBar;
    private bool _settingsScrollBarPointerOver;
    private int _settingsScrollBarAnimationVersion;

    /// <summary>给设置窗口加系统圆角（Windows 11）；旧系统上静默跳过。</summary>
    private void OnSourceInitialized(object? sender, EventArgs e) => WindowCorners.Apply(this);
    public SettingsWindow(ConfigService config, WindowManager windowManager)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _windowManager = windowManager ?? throw new ArgumentNullException(nameof(windowManager));

        // 圆角要在拿到原生句柄之后才能设，所以挂在 SourceInitialized 上
        SourceInitialized += OnSourceInitialized;

        InitializeComponent();
        _settingsScrollBarHideTimer.Interval = TimeSpan.FromMilliseconds(1400);
        _settingsScrollBarHideTimer.Tick += SettingsScrollBarHideTimer_Tick;
        _mediaDiagnosticsTimer.Interval = TimeSpan.FromMilliseconds(350);
        _mediaDiagnosticsTimer.Tick += (_, _) => UpdateMediaSourceDiagnostics();

        _fontChoices = FontCatalog.GetChoices();
        FontFamilyCombo.ItemsSource = _fontChoices;
        FontFamilyCombo.DisplayMemberPath = nameof(FontCatalog.Choice.Name);
        FontFamilyCombo.SelectedValuePath = nameof(FontCatalog.Choice.Value);
        LyricEffectCombo.ItemsSource = LyricEffectChoices;
        LyricEffectCombo.DisplayMemberPath = nameof(EffectChoice.Name);
        LyricEffectCombo.SelectedValuePath = nameof(EffectChoice.Mode);

        LoadFromConfig();
        UpdateMediaSourceDiagnostics();
        _mediaDiagnosticsTimer.Start();

        // 外部（比如命令行或别的窗口）改了配置，这里也要跟上
        _config.Changed += OnConfigChanged;
        _windowManager.AudioLyricSyncStatusChanged += OnAudioLyricSyncStatusChanged;
        _windowManager.CurrentTrackChanged += OnCurrentTrackChanged;
        _windowManager.CurrentLyricsResolutionChanged += OnCurrentLyricsResolutionChanged;
        RefreshCurrentLyricOffsetControls();
        UpdateCurrentLyricsResolutionDisplay(_windowManager.CurrentLyricsResolution);
        AudioLyricSyncStatusText.Text = _config.Current.AudioLyricSyncEnabled
            ? _windowManager.AudioLyricSyncStatus
            : "未启用";
        Closing += (_, e) =>
        {
            if (HasPendingLyricsSettings())
            {
                var result = MessageBox.Show(
                    this,
                    "歌词显示页有尚未确认的修改。\n选择“是”保存并关闭，选择“否”放弃修改并关闭，选择“取消”返回设置。",
                    "歌词显示设置尚未确认",
                    MessageBoxButton.YesNoCancel,
                    MessageBoxImage.Question);

                if (result == MessageBoxResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.Yes && !TryConfirmLyricsSettings())
                {
                    e.Cancel = true;
                    return;
                }

                if (result == MessageBoxResult.No)
                {
                    DiscardLyricsSettingsDraft();
                }
            }

            _windowManager.PreviewLyricsSettings(null);
            _lyricsDirDebounce?.Stop();
            _mediaAppAllowListDebounce?.Stop();
            SaveLyricsDirectory();
            SaveMediaAppAllowList();
            if (_config.IsDirty
                && !_config.SaveNow()
                && _config.HasUnpersistedUserChanges)
            {
                if (_config.TryMarkSaveFailureReported())
                {
                    MessageBox.Show(
                        this,
                        $"设置没有保存，窗口仍保持打开。\n配置文件：{_config.FilePath}\n原因：{_config.LastSaveError ?? "未知错误"}",
                        "词绘设置",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                e.Cancel = true;
            }
        };
        Closed += (_, _) =>
        {
            _isClosed = true;
            _config.Changed -= OnConfigChanged;
            _windowManager.AudioLyricSyncStatusChanged -= OnAudioLyricSyncStatusChanged;
            _windowManager.CurrentTrackChanged -= OnCurrentTrackChanged;
            _windowManager.CurrentLyricsResolutionChanged -= OnCurrentLyricsResolutionChanged;
            _settingsScrollBarHideTimer.Stop();
            _mediaDiagnosticsTimer.Stop();
            _lyricsDirDebounce?.Stop();
            _mediaAppAllowListDebounce?.Stop();
        };
    }

    private void LoadFromConfig(bool preservePendingLyricsDraft = false)
    {
        var config = _config.Current;
        var preserveDraft = preservePendingLyricsDraft && HasPendingLyricsSettings();
        if (!preserveDraft)
        {
            _lyricsDraft = config.Clone();
            _lyricsBaseline = config.Clone();
            _windowManager.PreviewLyricsSettings(null);
        }
        var displayConfig = preserveDraft ? _lyricsDraft : config;

        _suppressEvents = true;
        try
        {
            LoadLyricsDisplayControls(displayConfig);
            TopmostCheck.IsChecked = config.Topmost;
            LockedCheck.IsChecked = config.Frames.Frames.Count > 0 && config.Frames.Frames.All(f => f.Locked);
            UpdateLockHint(LockedCheck.IsChecked == true);

            FetchNetworkCheck.IsChecked = config.FetchLyricsFromNetwork;
            AudioLyricSyncCheck.IsChecked = config.AudioLyricSyncEnabled;
            MediaAppAllowListBox.Text = config.MediaAppAllowList;

            // 快捷键与后台运行
            HotkeyKeyCombo.ItemsSource = HotkeyKeyChoices;
            HotkeyEnabledCheck.IsChecked = config.HotkeyEnabled;
            HotkeyCtrlCheck.IsChecked = config.HotkeyCtrl;
            HotkeyAltCheck.IsChecked = config.HotkeyAlt;
            HotkeyShiftCheck.IsChecked = config.HotkeyShift;
            HotkeyKeyCombo.SelectedItem = HotkeyKeyChoices.Contains(config.HotkeyKey)
                ? config.HotkeyKey
                : HotkeyKeyChoices[0];

            // 关于页：把"怎么用"讲清楚，因为程序默认穿透、入口不明显
            AboutFeaturesText.Text =
                "歌词框可以有多个，每句按「句号 % 框数」轮流显示，所以两个框会交替出现。" +
                "可以保存多套布局；在「调整布局」里拖动方框改位置、拖右下角改大小、拖顶部圆点旋转。" +
                "字体、颜色和动态效果会全局保存；支持上下晃动、左右晃动和可调参数的粒子散开。" +
                "歌词框默认鼠标穿透，不会挡住游戏操作。";

            RunInBackgroundCheck.IsChecked = config.RunInBackground;

            LyricsDirectoryBox.Text = config.LyricsDirectory;
            UpdateLyricsDirectoryHint();
            UpdateCacheInfo();
            RefreshLyricsSettingsStatus();

            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "未知";
            VersionText.Text = $"测试版 {version}";
            ConfigPathBox.Text = _config.FilePath;
        }
        finally
        {
            _suppressEvents = false;
        }
    }

    private void OnConfigChanged(object? sender, AppConfig config)
    {
        // 配置变化可能来自别的线程
        Dispatcher.InvokeAsync(() =>
        {
            if (_isClosed)
            {
                return;
            }

            // 使用执行时的配置，避免排队的旧通知覆盖较新的控件状态。
            config = _config.Current;

            // 只同步那些不依赖用户正在操作的控件的显示
            _suppressEvents = true;
            try
            {
                if (!HasPendingLyricsSettings())
                {
                    _lyricsDraft = config.Clone();
                    _lyricsBaseline = config.Clone();
                    _windowManager.PreviewLyricsSettings(null);
                    LoadLyricsDisplayControls(config);
                }

                if (_mediaAppAllowListDebounce is not { IsEnabled: true })
                {
                    MediaAppAllowListBox.Text = config.MediaAppAllowList;
                }

                if (_lyricsDirDebounce is not { IsEnabled: true })
                {
                    LyricsDirectoryBox.Text = config.LyricsDirectory;
                    UpdateLyricsDirectoryHint();
                }

                RefreshLyricsSettingsStatus();

                var allLocked = config.Frames.Frames.Count > 0 && config.Frames.Frames.All(f => f.Locked);
                if (LockedCheck.IsChecked != allLocked)
                {
                    LockedCheck.IsChecked = config.Frames.Frames.Count > 0 && config.Frames.Frames.All(f => f.Locked);
                }
                UpdateLockHint(allLocked);
            }
            finally
            {
                _suppressEvents = false;
            }
        });
    }

    // ---------------------------------------------------------------
    // 导航
    // ---------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PanelLyrics is null)
        {
            // 初始化期间控件还没建好，忽略
            return;
        }

        PanelLyrics.Visibility = NavLyrics.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelWindow.Visibility = NavWindow.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelSource.Visibility = NavSource.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelHotkey.Visibility = NavHotkey.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = NavAbout.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;

        if (NavSource.IsChecked == true)
        {
            UpdateLyricsDirectoryHint();
        }
    }

    // ---------------------------------------------------------------
    // 歌词显示
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    // 窗口行为
    // ---------------------------------------------------------------

    private void TopmostCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _config.Update(c => c.Topmost = TopmostCheck.IsChecked == true);
    }

    // ---------------------------------------------------------------
    // 自动获取与缓存
    // ---------------------------------------------------------------

    // ---------------------------------------------------------------
    // 布局编辑器入口
    // ---------------------------------------------------------------

    /// <summary>
    /// 打开全屏布局编辑器。
    ///
    /// 这是编辑器的第二个入口（另一个是托盘菜单，还有全局热键）。
    /// 放在设置界面里很有必要：程序默认鼠标穿透、歌词框点不到，
    /// 如果只靠托盘和热键，新用户会完全不知道位置该怎么调。
    /// </summary>
    private void OpenLayoutEditor_Click(object sender, RoutedEventArgs e)
    {
        // 用完整类型名而不是 Application：项目同时开了 WinForms，
        // 两个 Application 类型会撞名
        (System.Windows.Application.Current as App)?.OpenLayoutEditor();
    }

    // ---------------------------------------------------------------
    // 快捷键与后台运行
    // ---------------------------------------------------------------

    /// <summary>
    /// 可以让用户选作热键主键的按键。
    /// 不开放全部按键：字母和数字够用，而且避免用户选到
    /// F12（调试器占用）、Pause 之类会出问题的键。
    /// </summary>
    private static readonly string[] HotkeyKeyChoices =
    {
        "E", "L", "K", "J", "H", "G", "T", "Y", "U", "O", "P",
        "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D0",
        "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11",
    };

    private void HotkeyEnabledCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _config.Update(c => c.HotkeyEnabled = HotkeyEnabledCheck.IsChecked == true);
        ApplyHotkey();
    }

    private void HotkeyModifier_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        _config.Update(c =>
        {
            c.HotkeyCtrl = HotkeyCtrlCheck.IsChecked == true;
            c.HotkeyAlt = HotkeyAltCheck.IsChecked == true;
            c.HotkeyShift = HotkeyShiftCheck.IsChecked == true;
        });

        ApplyHotkey();
    }

    private void HotkeyKeyCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressEvents || HotkeyKeyCombo.SelectedItem is not string key)
        {
            return;
        }

        _config.Update(c => c.HotkeyKey = key);
        ApplyHotkey();
    }

    /// <summary>
    /// 改完热键立刻重新注册，并给出反馈。
    ///
    /// 必须告诉用户"注册成功没有"：热键可能被别的程序占用了，
    /// 这种情况下注册会失败，但用户按了没反应会以为是程序坏了。
    /// </summary>
    private void ApplyHotkey()
    {
        var app = System.Windows.Application.Current as App;
        var ok = app?.RegisterHotkey() ?? false;

        UpdateHotkeyHint(ok);
    }

    private void UpdateHotkeyHint(bool registered)
    {
        if (HotkeyHintText is null)
        {
            return;
        }

        var config = _config.Current;

        if (!config.HotkeyEnabled)
        {
            HotkeyHintText.Text = "已关闭。关闭后只能用托盘菜单打开布局编辑器。";
            return;
        }

        if (string.IsNullOrEmpty(config.HotkeyKey)
            || (!config.HotkeyCtrl && !config.HotkeyAlt && !config.HotkeyShift))
        {
            HotkeyHintText.Text = "当前组合不完整：至少要勾一个 Ctrl / Alt / Shift，否则会和普通按键冲突。";
            return;
        }

        var text = BuildHotkeyText(config);

        HotkeyHintText.Text = registered
            ? $"当前生效：{text}。这个组合键可以用。"
            : $"当前设置：{text}，但注册失败了——这个组合多半已经被别的程序占用，换一个试试。";
    }

    private static string BuildHotkeyText(AppConfig config)
    {
        var parts = new List<string>();
        if (config.HotkeyCtrl) { parts.Add("Ctrl"); }
        if (config.HotkeyAlt) { parts.Add("Alt"); }
        if (config.HotkeyShift) { parts.Add("Shift"); }
        parts.Add(config.HotkeyKey);
        return string.Join(" + ", parts);
    }

    private void RunInBackgroundCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var enabled = RunInBackgroundCheck.IsChecked == true;
        _config.Update(c => c.RunInBackground = enabled);
    }

    private void LockedCheck_Click(object sender, RoutedEventArgs e)
    {
        if (_suppressEvents)
        {
            return;
        }

        var locked = LockedCheck.IsChecked == true;
        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                frame.Locked = locked;
            }
        });
        UpdateLockHint(locked);
    }

    private void UpdateLockHint(bool locked)
    {
        if (LockHintText is null)
        {
            return;
        }

        // 说明必须和实际行为一致。
        // 这里曾经写着"未锁定：可以按住歌词拖动位置"——那是错的，
        // 歌词窗口的鼠标拖动早已移除（位置统一在布局编辑器里调），
        // 留着这句话会让用户以为解锁就能拖，白白浪费时间。
        LockHintText.Text = locked
            ? "已锁定：鼠标点击会穿过歌词落到下面的窗口，玩游戏时不会挡住操作。"
            : "未锁定：歌词窗口会接收鼠标点击，但**仍然不能拖动**——"
              + "位置请用「调整布局」的全屏编辑器来调。锁定可以避免误点。";
    }

    // ---------------------------------------------------------------
    // 歌词来源
    // ---------------------------------------------------------------

    private void OpenConfigDirectory_Click(object sender, RoutedEventArgs e)
    {
        var directory = Path.GetDirectoryName(_config.FilePath);
        if (!string.IsNullOrEmpty(directory))
        {
            OpenInExplorer(directory);
        }
    }

    private static void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
            else
            {
                // 目录还不存在时也把它建出来，省得用户自己找
                Directory.CreateDirectory(path);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or ArgumentException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show($"打不开目录：{ex.Message}", "词绘", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void ReloadConfig_Click(object sender, RoutedEventArgs e)
    {
        // 刷新当前内存配置对应的控件；磁盘文件需退出应用后编辑，再启动读取。
        LoadFromConfig(preservePendingLyricsDraft: true);
    }

    // ---------------------------------------------------------------
    // 标题栏按钮
    // ---------------------------------------------------------------

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();
}
