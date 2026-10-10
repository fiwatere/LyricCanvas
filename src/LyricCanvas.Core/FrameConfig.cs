namespace LyricCanvas.Core;

/// <summary>
/// 一个歌词框（屏幕上的一个文字窗口）的配置。
///
/// 多框设计的关键：**每个框只描述"在哪、多大、朝哪个方向、长什么样"**，
/// 至于"显示第几句歌词"是由分配策略决定的，不存在这里。
/// 这样以后换分配策略（比如随机出现）不用动配置结构。
/// </summary>
public sealed class FrameConfig
{
    /// <summary>框的稳定标识。窗口重建、位置回写都靠它对应。</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    /// <summary>是否启用这个框。关掉就销毁它的窗口。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>在编辑器列表里显示的名字。</summary>
    public string Name { get; set; } = string.Empty;

    // ---------------------------------------------------------------
    // 位置与尺寸（设备无关像素，相对于虚拟屏幕左上角）
    // ---------------------------------------------------------------

    public double Left { get; set; }

    public double Top { get; set; }

    /// <summary>框的宽度。文字超出这个宽度时会先压缩字号、再截断。</summary>
    public double Width { get; set; } = 600;

    /// <summary>框的高度。文字在框内垂直居中。</summary>
    public double Height { get; set; } = 80;

    /// <summary>绕歌词框中心旋转的角度。正数顺时针，范围由配置服务规整。</summary>
    public double RotationDegrees { get; set; }

    // ---------------------------------------------------------------
    // 外观（留空表示跟随全局样式）
    // ---------------------------------------------------------------

    /// <summary>字号。null 表示跟随全局设置。</summary>
    public double? FontSize { get; set; }

    /// <summary>文字颜色（#AARRGGBB 或 #RRGGBB）。null 表示跟随全局设置。</summary>
    public string? TextColor { get; set; }

    /// <summary>这个框的不透明度 0.05~1.0。null 表示跟随全局设置。</summary>
    public double? Opacity { get; set; }

    /// <summary>是否锁定（鼠标穿透）。默认开启，避免挡住游戏操作。</summary>
    public bool Locked { get; set; } = true;

    /// <summary>复制一份。</summary>
    public FrameConfig Clone() => (FrameConfig)MemberwiseClone();

    /// <summary>
    /// 给一个框取个便于识别的默认名字。
    /// </summary>
    public string GetDisplayName(int index)
        => string.IsNullOrWhiteSpace(Name) ? $"歌词框 {index + 1}" : Name;
}
