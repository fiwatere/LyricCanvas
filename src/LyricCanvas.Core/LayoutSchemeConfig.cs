namespace LyricCanvas.Core;

/// <summary>一套可切换的歌词框布局；外观默认仍继承应用级样式。</summary>
public sealed class LayoutSchemeConfig
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    public string Name { get; set; } = "布局方案";

    public List<FrameConfig> Frames { get; set; } = new();

    public LayoutSchemeConfig Clone() => new()
    {
        Id = Id,
        Name = Name,
        Frames = Frames.Select(frame => frame.Clone()).ToList(),
    };
}
