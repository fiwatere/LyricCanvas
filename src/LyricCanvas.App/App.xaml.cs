using System.Windows;
using LyricCanvas.App.Settings;
using LyricCanvas.Core;

// 项目同时打开了 UseWPF 和 UseWindowsForms（托盘图标要用后者），
// System.Windows.Forms.Application 会和 WPF 的同名类型产生歧义，这里明确指定
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace LyricCanvas.App;

/// <summary>
/// 应用程序入口。
///
/// 程序由三部分组成：
///   - 一组歌词框窗口（由 <see cref="WindowManager"/> 管理，可以有多个）；
///   - 设置窗口（PCL 风格，按需打开）；
///   - 托盘图标 + 全局热键（穿透状态下打开界面的入口）。
///
/// 关键行为：**关闭设置窗口不等于退出程序**，程序继续在托盘里运行，
/// 歌词继续显示。这是桌面叠加类程序的惯例——用户关掉设置是想让它继续工作，
/// 不是想让它消失。真正退出只能从托盘菜单走。
/// 这个行为可以在设置里关掉（"关闭后继续在后台运行"）。
/// </summary>
public partial class App : Application
{
    private ConfigService? _config;
    private WindowManager? _windowManager;
    private TrayIcon? _trayIcon;
    private HotkeyManager? _hotkeyManager;
    private SettingsWindow? _settingsWindow;
    private bool _isRequestingExit;

    /// <summary>单实例互斥体。进程退出时由系统释放。</summary>
    private Mutex? _instanceMutex;

    /// <summary>互斥体名字。加 Local\ 前缀表示限定当前登录会话。</summary>
    private const string InstanceMutexName = @"Local\LyricCanvas.SingleInstance";

    /// <summary>命令行参数，供各窗口查询。</summary>
    internal static string[] CommandLineArgs { get; private set; } = Array.Empty<string>();

    /// <summary>解析一个形如 <c>--name=value</c> 或 <c>--name</c> 的开关。</summary>
    internal static string? GetSwitchValue(string name)
    {
        var prefix = "--" + name;
        foreach (var arg in CommandLineArgs)
        {
            if (arg.StartsWith(prefix + "=", StringComparison.OrdinalIgnoreCase))
            {
                return arg[(prefix.Length + 1)..];
            }

            if (string.Equals(arg, prefix, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }
        }

        return null;
    }

    internal static bool HasSwitch(string name) => GetSwitchValue(name) is not null;

    protected override void OnStartup(StartupEventArgs e)
    {
        CommandLineArgs = e.Args ?? Array.Empty<string>();

        base.OnStartup(e);

        // 单实例保护。
        //
        // 为什么必须有：托盘图标是"每个进程一个"的资源。同时跑两个实例时，
        // 两个 NotifyIcon 会互相顶掉——后注册的那个能显示，先注册的就消失了；
        // 而如果实例是被强制结束的（任务管理器结束进程、或者 dotnet build
        // 之前直接 Stop-Process），图标会变成"僵尸"占着托盘位置，
        // 新实例注册不上，用户就完全看不到图标。
        // 这个坑真实发生过：两个实例同时运行，托盘里一个图标都没有。
        //
        // 允许用 --allow-multiple 跳过（自动化验证需要同时跑多份做对照时用）。
        if (!HasSwitch("allow-multiple"))
        {
            _instanceMutex = new Mutex(initiallyOwned: true, InstanceMutexName, out var isFirstInstance);

            if (!isFirstInstance)
            {
                // 已经有实例在跑：立刻退出。
                //
                // 这里刻意用 Environment.Exit 而不是 Shutdown()：
                // 此时消息循环还没跑起来，Shutdown() 不一定能干净地结束进程，
                // 实测会出现"第二个实例一直不退"的情况。
                // 也刻意不弹提示框——那会阻塞退出，而且用户多半是顺手
                // 又启动了一次，不需要被弹窗教育；托盘图标和任务栏都在。
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Environment.Exit(0);
                return;
            }
        }

        _config = ConfigService.CreateFromCommandLine();

        // 命令行给的歌词目录优先于配置里的值，方便脚本用临时目录跑验证
        var lyricDirOverride = GetSwitchValue("lyric-dir");
        if (!string.IsNullOrWhiteSpace(lyricDirOverride))
        {
            _config.Update(c => c.LyricsDirectory = lyricDirOverride);
        }

        // 首次运行时造出默认的两个框
        EnsureDefaultFrames();

        _windowManager = new WindowManager(_config);
        _windowManager.Start();

        ApplyTopmost(_config.Current.Topmost);
        _config.Changed += (_, config) => ApplyTopmost(config.Topmost);

        _trayIcon = new TrayIcon();
        _trayIcon.OpenSettingsRequested += (_, _) => OpenSettings();
        _trayIcon.OpenLayoutEditorRequested += (_, _) => OpenLayoutEditor();
        _trayIcon.ToggleLockRequested += (_, _) => ToggleLockAllFrames();
        _trayIcon.ExitRequested += (_, _) => RequestExit();

        _ = RegisterHotkey();
        UpdateTrayState();
        _config.Changed += (_, _) => UpdateTrayState();

        // 默认打开设置窗口，否则用户找不到任何入口。
        // 自动化验证脚本会加 --no-settings，避免设置窗口干扰对歌词框的探测。
        if (!HasSwitch("no-settings"))
        {
            OpenSettings();
        }

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.ApplicationIdle,
            new Action(() => _trayIcon?.EnsureVisible()));

        Exit += (_, _) => PersistAll();
        SessionEnding += (_, _) => PersistAll();
    }

    /// <summary>配置里一个框都没有时，补上默认的两个。</summary>
    private void EnsureDefaultFrames()
    {
        if (_config is null || _config.Current.Frames.Frames.Count > 0)
        {
            return;
        }

        // 用当前屏幕尺寸摆默认位置而不是写死 1920x1080，
        // 否则小屏幕上默认框可能有一半在屏幕外
        var width = SystemParameters.PrimaryScreenWidth;
        var height = SystemParameters.PrimaryScreenHeight;

        _config.Update(c => c.Frames.Frames = FramesConfig.CreateDefaultFrames(width, height));
    }

    private void ApplyTopmost(bool topmost)
    {
        if (_windowManager is null)
        {
            return;
        }

        foreach (var window in _windowManager.Windows.Values)
        {
            window.Topmost = topmost;
        }
    }

    // ---------------------------------------------------------------
    // 窗口入口
    // ---------------------------------------------------------------

    /// <summary>打开设置窗口；已经开着就拉到前面。</summary>
    internal void OpenSettings()
    {
        if (_config is null || _windowManager is null)
        {
            return;
        }

        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_config, _windowManager);
            _settingsWindow.Closed += (_, _) =>
            {
                _settingsWindow = null;

                // 用户选择"关闭即退出"时，关掉设置窗口就等于退出程序
                if (!_isRequestingExit && !_config.Current.RunInBackground)
                {
                    RequestExit();
                }
            };
            _settingsWindow.Show();
        }
        else
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
            {
                _settingsWindow.WindowState = WindowState.Normal;
            }

            _settingsWindow.Activate();
        }
    }

    /// <summary>打开全屏布局编辑器。</summary>
    internal void OpenLayoutEditor()
    {
        if (_config is null || _windowManager is null)
        {
            return;
        }

        // 编辑期间要能拖动歌词框，所以先全部临时解锁；关闭时恢复
        var editor = new LayoutEditorWindow(_config, _windowManager);
        editor.ShowDialog();
    }

    // ---------------------------------------------------------------
    // 热键
    // ---------------------------------------------------------------

    /// <summary>
    /// 注册全局热键。热键能被用户改，改完要重新注册，所以这里有重建逻辑。
    /// </summary>
    /// <returns>注册成功返回 true；被别的程序占用或未启用时返回 false。</returns>
    internal bool RegisterHotkey()
    {
        if (_config is null || _windowManager is null)
        {
            return false;
        }

        _hotkeyManager?.Dispose();
        _hotkeyManager = null;

        var config = _config.Current;
        if (!config.HotkeyEnabled)
        {
            return false;
        }

        if (!Enum.TryParse<System.Windows.Input.Key>(config.HotkeyKey, ignoreCase: true, out var key))
        {
            // 配置里写了个认不出来的键名，退回默认的 E
            key = System.Windows.Input.Key.E;
        }

        // 热键要挂在一个真实窗口上，随便挑一个歌词框即可
        var host = _windowManager.Windows.Values.FirstOrDefault();
        if (host is null)
        {
            return false;
        }

        _hotkeyManager = new HotkeyManager(host);
        return _hotkeyManager.Register(
            key,
            OpenLayoutEditor,
            ctrl: config.HotkeyCtrl,
            alt: config.HotkeyAlt,
            shift: config.HotkeyShift);
    }

    /// <summary>
    /// 托盘菜单的"锁定/解锁"：把所有框一起切。
    ///
    /// 菜单只有一项，而框可以有很多个，所以语义定为**全体一起切**：
    /// 当前是不是全锁着决定点击后的目标状态。要单独控制某个框，
    /// 用设置界面里的那个开关（同样是全体，但能看清当前状态）。
    /// </summary>
    private void ToggleLockAllFrames()
    {
        if (_config is null)
        {
            return;
        }

        var allLocked = _config.Current.Frames.Frames.Count > 0
                        && _config.Current.Frames.Frames.All(f => f.Locked);
        var target = !allLocked;

        _config.Update(c =>
        {
            foreach (var frame in c.Frames.Frames)
            {
                frame.Locked = target;
            }
        });

        _windowManager?.SyncFrames();
        UpdateTrayState();
    }

    /// <summary>
    /// 刷新托盘图标的提示文字与"锁定"项的勾选状态。
    ///
    /// 必须跟着配置变化刷新：锁定状态可能是用户在设置界面里改的，
    /// 如果不刷新，托盘菜单的勾会停留在旧状态，看起来像程序没反应。
    /// </summary>
    private void UpdateTrayState()
    {
        if (_trayIcon is null || _config is null)
        {
            return;
        }

        var frames = _config.Current.Frames.Frames;
        var allLocked = frames.Count > 0 && frames.All(f => f.Locked);

        _trayIcon.SetLocked(allLocked, BuildHotkeyText(_config.Current));
    }

    /// <summary>把当前热键拼成可读文字，给托盘提示用。</summary>
    private static string BuildHotkeyText(AppConfig config)
    {
        if (!config.HotkeyEnabled)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        if (config.HotkeyCtrl) { parts.Add("Ctrl"); }
        if (config.HotkeyAlt) { parts.Add("Alt"); }
        if (config.HotkeyShift) { parts.Add("Shift"); }
        parts.Add(config.HotkeyKey);
        return string.Join("+", parts);
    }

    /// <summary>把布局和配置落盘。</summary>
    private void PersistAll()
    {
        _windowManager?.PersistLayout();
        _config?.SaveNow();
    }

    /// <summary>真正退出程序。由托盘菜单调用。</summary>
    internal void RequestExit()
    {
        if (_isRequestingExit)
        {
            return;
        }

        _isRequestingExit = true;
        try
        {
            // 让设置窗口先走正常 Closing 流程，处理未确认草稿、防抖输入和保存失败。
            // WPF 的 Shutdown 会忽略窗口 Closing 中的取消，因此不能先 Shutdown 再询问。
            if (_settingsWindow is not null)
            {
                _settingsWindow.Close();
                if (_settingsWindow is not null)
                {
                    return;
                }
            }

            _windowManager?.PersistLayout();
            if (_config is not null
                && _config.IsDirty
                && !_config.SaveNow()
                && _config.HasUnpersistedUserChanges)
            {
                if (_config.TryMarkSaveFailureReported())
                {
                    MessageBox.Show(
                        $"设置没有保存，程序仍在运行。\n配置文件：{_config.FilePath}\n原因：{_config.LastSaveError ?? "未知错误"}",
                        "词绘",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                return;
            }

            Shutdown();
        }
        finally
        {
            _isRequestingExit = false;
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        _hotkeyManager?.Dispose();
        _trayIcon?.Dispose();
        _windowManager?.Dispose();
        _config?.Dispose();

        base.OnExit(e);
    }
}
