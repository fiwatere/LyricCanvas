using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyricCanvas.App;

/// <summary>
/// 全局热键。
///
/// 为什么必须有它：歌词窗口一旦进入"锁定"状态就会鼠标穿透，用户点不到它，
/// 如果只能从设置窗口解锁，那设置窗口本身也关掉之后就彻底没法解锁了。
/// 所以需要一个不依赖鼠标、也不依赖窗口焦点的开关。
///
/// 实现方式：RegisterHotKey 把组合键注册到窗口句柄上，系统会把
/// WM_HOTKEY 消息投递到那个窗口的消息循环，这里挂上 Hook 处理即可。
/// </summary>
internal sealed class HotkeyManager : IDisposable
{
    private const int WmHotkey = 0x0312;

    // 修饰键
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;
    private bool _disposed;

    public HotkeyManager(Window window)
    {
        _handle = new WindowInteropHelper(window).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle)
                  ?? throw new InvalidOperationException("拿不到窗口的 HwndSource，无法注册全局热键。");

        _source.AddHook(WndProc);
    }

    /// <summary>
    /// 注册一个全局热键。
    /// </summary>
    /// <param name="key">主键，例如 <see cref="System.Windows.Input.Key.L"/>。</param>
    /// <param name="callback">按下时执行的动作。</param>
    /// <param name="ctrl">是否需要 Ctrl。</param>
    /// <param name="alt">是否需要 Alt。</param>
    /// <param name="shift">是否需要 Shift。</param>
    /// <returns>注册成功返回 true。被别的程序占用时会返回 false。</returns>
    public bool Register(System.Windows.Input.Key key, Action callback, bool ctrl = true, bool alt = true, bool shift = false)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        uint modifiers = ModNoRepeat;
        if (ctrl)
        {
            modifiers |= ModControl;
        }

        if (alt)
        {
            modifiers |= ModAlt;
        }

        if (shift)
        {
            modifiers |= ModShift;
        }

        var id = _nextId++;
        var virtualKey = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);

        if (!RegisterHotKey(_handle, id, modifiers, virtualKey))
        {
            // 组合键已被别的程序注册。不是致命问题，界面照常能用，
            // 只是这一个快捷键失效，所以这里不抛异常也不弹窗。
            return false;
        }

        _actions[id] = callback;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch
            {
                // 热键回调里出问题不该拖垮整程序
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _actions.Keys)
        {
            UnregisterHotKey(_handle, id);
        }

        _actions.Clear();
        _source.RemoveHook(WndProc);
    }
}
