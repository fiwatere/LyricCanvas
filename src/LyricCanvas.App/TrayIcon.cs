using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;

namespace LyricCanvas.App;

/// <summary>
/// 任务栏托盘图标。
///
/// 为什么需要托盘：歌词窗口没有标题栏也没有关闭按钮，设置窗口关掉之后就
/// 没有任何入口能再打开它。托盘图标负责提供"打开设置 / 锁定 / 退出"这三个入口。
///
/// 实现上用了 WinForms 的 NotifyIcon：WPF 自己没有托盘图标控件，
/// 而 NotifyIcon 是现成且稳定的，比用 Shell_NotifyIcon 手搓一遍省事得多。
/// 图标也是代码画出来的，不用附带 .ico 文件。
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _notifyIcon;

    /// <summary>"锁定"菜单项。用字段而不是下标定位，见构造方法里的说明。</summary>
    private ToolStripMenuItem? _lockItem;
    private readonly Icon _icon;
    private bool _disposed;

    /// <summary>双击托盘图标。</summary>
    public event EventHandler? OpenSettingsRequested;

    /// <summary>点了"调整布局"。</summary>
    public event EventHandler? OpenLayoutEditorRequested;

    /// <summary>点了"锁定/解锁"。</summary>
    public event EventHandler? ToggleLockRequested;

    /// <summary>点了"退出"。</summary>
    public event EventHandler? ExitRequested;

    public TrayIcon()
    {
        _icon = LoadIcon();

        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("打开设置");
        openItem.Click += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(openItem);

        var layoutItem = new ToolStripMenuItem("调整布局…");
        layoutItem.Click += (_, _) => OpenLayoutEditorRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(layoutItem);

        menu.Items.Add(new ToolStripSeparator());

        // 用字段记住这个菜单项，不要用 Items[下标] 去取——
        // 那样以后往菜单里加一项就会静默指向错误的项（这个坑刚踩过）
        _lockItem = new ToolStripMenuItem("锁定歌词（鼠标穿透）");
        _lockItem.Click += (_, _) => ToggleLockRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(_lockItem);

        menu.Items.Add(new ToolStripSeparator());

        var exitItem = new ToolStripMenuItem("退出词绘");
        exitItem.Click += (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty);
        menu.Items.Add(exitItem);

        // 让菜单跟随深色主题，不然亮色菜单在深色界面上很刺眼
        menu.Renderer = new DarkMenuRenderer();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icon,
            Text = "词绘（LyricCanvas）- 桌面歌词",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 更新"锁定"菜单项的勾选状态和提示文字。
    ///
    /// 这里的"锁定"是**全体一起切**：菜单只有一项，但框可以有很多个，
    /// 所以勾选状态表示"是不是所有框都锁着"，点击则把所有框设成同一个值。
    /// 需要单独控制某个框时用设置界面。
    /// </summary>
    /// <param name="locked">所有框是否都处于锁定（穿透）状态。</param>
    /// <param name="hotkeyText">当前生效的热键文字，用于托盘提示；没有则传空。</param>
    public void SetLocked(bool locked, string hotkeyText = "")
    {
        var suffix = string.IsNullOrWhiteSpace(hotkeyText) ? "" : $"（{hotkeyText} 打开布局）";

        _notifyIcon.Text = locked
            ? $"词绘 - 已锁定{suffix}"
            : $"词绘 - 桌面歌词{suffix}";

        if (_lockItem is not null)
        {
            _lockItem.Checked = locked;
            _lockItem.Text = locked ? "解锁歌词（恢复鼠标操作）" : "锁定歌词（鼠标穿透）";
        }
    }

    /// <summary>
    /// 让 Explorer 在主窗口启动后重新登记一次图标。
    /// 应用窗口初始化期间如果系统托盘尚未就绪，首次 NotifyIcon 注册可能没能及时出现。
    /// </summary>
    public void EnsureVisible()
    {
        if (_disposed)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Visible = true;
    }

    /// <summary>
    /// 画一个托盘图标：圆角深色底 + 一个音符。
    /// 用代码画而不是带一个 .ico 文件，是为了少一个二进制资源，
    /// 以后想改配色也只要动这里。
    /// </summary>
    /// <summary>
    /// 取托盘图标：优先用 exe 自带的应用图标（和任务栏、资源管理器一致），
    /// 拿不到才退回代码画的那个。
    ///
    /// 为什么要优先用真图标：纯代码画出来的图标在某些系统上会被托盘当成
    /// 无效图标而**静默不显示**（用户报过"托盘里找不到图标"）。
    /// 有 ApplicationIcon 之后这个问题就不存在了。
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var extracted = Icon.ExtractAssociatedIcon(exe);
                if (extracted is not null)
                {
                    return extracted;
                }
            }
        }
        catch (ArgumentException)
        {
        }
        catch (System.IO.FileNotFoundException)
        {
        }

        return CreateIcon();
    }

    private static Icon CreateIcon()
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // 圆角底
            using var background = new SolidBrush(Color.FromArgb(255, 45, 45, 52));
            using var path = RoundedRect(new Rectangle(1, 1, size - 2, size - 2), 7);
            g.FillPath(background, path);
            // 音符：一个实心椭圆 + 一根竖线 + 一个小旗
            var accent = Color.FromArgb(255, 91, 141, 239);
            using var noteBrush = new SolidBrush(accent);
            g.FillEllipse(noteBrush, 7, 18, 9, 7);
            using var stemPen = new Pen(noteBrush, 2.6f);
            g.DrawLine(stemPen, 15, 21, 15, 8);
            g.DrawLine(stemPen, 15, 8, 23, 10.5f);
            g.DrawLine(stemPen, 23, 10.5f, 23, 15);

            // 注意：SolidBrush 的构造函数要的是 Color，不是 Brush
            using var flagBrush = new SolidBrush(accent);
            g.FillEllipse(flagBrush, 19, 13, 8, 6);
        }

        // Bitmap -> Icon：克隆一份再销毁句柄，否则句柄泄漏
        var handle = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var d = radius * 2;

        path.AddArc(bounds.Left, bounds.Top, d, d, 180, 90);
        path.AddArc(bounds.Right - d, bounds.Top, d, d, 270, 90);
        path.AddArc(bounds.Right - d, bounds.Bottom - d, d, d, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - d, d, d, 90, 90);
        path.CloseFigure();

        return path;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        // 必须先隐藏再释放，否则图标会残留在托盘里直到鼠标划过
        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _icon.Dispose();
    }

    /// <summary>把右键菜单画成深色，跟设置窗口的主题一致。</summary>
    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer()
            : base(new DarkColorTable())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item?.Enabled == true
                ? Color.FromArgb(255, 237, 237, 240)
                : Color.FromArgb(255, 106, 106, 114);

            base.OnRenderItemText(e);
        }
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        private static readonly Color Background = Color.FromArgb(255, 44, 44, 49);
        private static readonly Color Border = Color.FromArgb(255, 58, 58, 64);

        public override Color ToolStripDropDownBackground => Background;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Color.FromArgb(255, 91, 141, 239);
        public override Color MenuItemSelected => Color.FromArgb(255, 58, 58, 64);
        public override Color ImageMarginGradientBegin => Background;
        public override Color ImageMarginGradientMiddle => Background;
        public override Color ImageMarginGradientEnd => Background;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }
}
