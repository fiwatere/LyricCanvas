using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LyricCanvas.App;

/// <summary>
/// 给窗口加圆角。
///
/// 为什么用系统接口而不是自己画：WPF 里想让窗口四角是圆的，只能
/// `AllowsTransparency=true` + 自己画圆角 Border。但那对设置窗口**行不通**——
/// WPF 明确要求"`AllowsTransparency` 为 true 时 `WindowStyle` 只能是 `None`"，
/// 而设置窗口用 `WindowChrome` 保留了窗口缩放和系统行为，不能设成 `None`。
/// （强行组合会在 `Show()` 时抛 `InvalidOperationException`，实测过。）
///
/// 所以改用 `DwmSetWindowAttribute` 的 `DWMWA_WINDOW_CORNER_PREFERENCE`，
/// 让 DWM 自己把窗口圆角化。好处是不影响 WPF 的渲染方式
/// （窗口保持非分层，present 仍走硬件路径），缩放手感也由系统保证。
///
/// Windows 11 才有这个属性；Windows 10 上调用会失败，此时静默忽略——
/// 系统本来就是直角窗口，不做处理也是对的。
/// </summary>
internal static class WindowCorners
{
    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE 的属性编号。</summary>
    private const int DwmwaWindowCornerPreference = 33;

    /// <summary>DWMWCP_ROUND：圆角（半径由系统决定，比自定的更协调）。</summary>
    private const int DwmwcpRound = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>
    /// 给窗口加圆角。在窗口的 <c>SourceInitialized</c> 之后调用（那时才有原生句柄）。
    /// 拿不到句柄或系统不支持时什么都不做，不会抛异常。
    /// </summary>
    /// <param name="window">目标窗口。null 时直接返回。</param>
    public static void Apply(Window? window)
    {
        if (window is null)
        {
            return;
        }

        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero)
            {
                return;
            }

            var preference = DwmwcpRound;

            // 失败不代表出错：Windows 10 没这个属性，返回非 0 是正常的。
            // 这里刻意不检查返回值、也不记日志——它就是"能圆就圆，不能就算了"。
            _ = DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // 理论上不会发生（dwmapi 从 Vista 起就有），防御一下
        }
        catch (EntryPointNotFoundException)
        {
        }
    }
}
