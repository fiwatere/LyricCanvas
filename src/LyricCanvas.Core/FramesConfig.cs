namespace LyricCanvas.Core;

/// <summary>
/// 全局歌词样式以及当前活动布局方案的框列表。
///
/// 配置分成两层：
///   - **全局样式**（字号、颜色、透明度、字体）：改一次所有框一起变，免得挨个调；
///   - **每个框的位置、尺寸和旋转角度**：各管各的。
/// 单个框可以覆盖全局样式（<see cref="FrameConfig.FontSize"/> 等填了值就用自己的），
/// 这样"想统一"和"想单独调"两种情况都能满足。
/// </summary>
public enum LyricEffectMode
{
    None,
    Shake,
    Float,
    Particles,
}

/// <summary>保留供旧配置迁移使用的方向值。</summary>
public enum LyricMotionDirection
{
    Vertical,
    Horizontal,
}

/// <summary>歌词切换时采用的播放效果。</summary>
public enum PlaybackEffectMode
{
    Fade,
    LeftToRight,
}

public sealed class FramesConfig
{
    /// <summary>默认框数量：两个框交替，一个说单数句、一个说双数句。</summary>
    public const int DefaultFrameCount = 2;

    /// <summary>框数上限。每个框是一个独立的透明窗口，太多没有意义也费资源。</summary>
    public const int MaxFrameCount = 10;

    /// <summary>全局文字渐变允许保存的色标数量上限。</summary>
    public const int MaxTextGradientColors = 16;

    /// <summary>所有框。顺序就是分配顺序（第 1 个框拿第 1、1+N、1+2N… 句）。</summary>
    public List<FrameConfig> Frames { get; set; } = new();

    /// <summary>
    /// 复制全局歌词样式，不覆盖活动布局中的框和单框覆盖值。
    /// 预览和保存共用此方法，避免临时预览意外移动或改写各个歌词框。
    /// </summary>
    public void CopyGlobalStyleFrom(FramesConfig source)
    {
        ArgumentNullException.ThrowIfNull(source);

        FontSize = source.FontSize;
        FontFamily = source.FontFamily;
        TextColor = source.TextColor;
        TextGradientColors = source.TextGradientColors?.ToList() ?? new List<string>();
        PlayedTextColor = source.PlayedTextColor;
        UnplayedTextColor = source.UnplayedTextColor;
        GlowColor = source.GlowColor;
        GlowRadius = source.GlowRadius;
        GlowOpacity = source.GlowOpacity;
        SubTextColor = source.SubTextColor;
        Opacity = source.Opacity;
        ShowTrackInfo = source.ShowTrackInfo;
        LyricEffect = source.LyricEffect;
        EffectIntensity = source.EffectIntensity;
        MotionDirection = source.MotionDirection;
        MotionPeriodMs = source.MotionPeriodMs;
        ParticleSize = source.ParticleSize;
        ParticleCount = source.ParticleCount;
        ParticleSpread = source.ParticleSpread;
        ParticleDurationMs = source.ParticleDurationMs;
        FadeInMs = source.FadeInMs;
        FadeOutMs = source.FadeOutMs;
        PlaybackEffect = source.PlaybackEffect;
        HoldUntilNextFrameLine = source.HoldUntilNextFrameLine;
        LetterSpacing = source.LetterSpacing;
        HoldMs = source.HoldMs;
    }

    // ---------------------------------------------------------------
    // 公共样式
    // ---------------------------------------------------------------

    /// <summary>全局字号。</summary>
    public double FontSize { get; set; } = 44;

    /// <summary>全局字体。</summary>
    public string FontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>全局文字颜色。</summary>
    public string TextColor { get; set; } = "#FFFFD866";

    /// <summary>全局字体颜色渐变色标；空列表沿用 TextColor，单色列表显示纯色。</summary>
    public List<string> TextGradientColors { get; set; } = new();

    /// <summary>从左至右播放效果中已播放部分的颜色；空值跟随全局文字颜色。</summary>
    public string? PlayedTextColor { get; set; }

    /// <summary>从左至右播放效果中未播放部分的颜色；空值跟随全局文字颜色的淡化版本。</summary>
    public string? UnplayedTextColor { get; set; }

    /// <summary>全局文字光晕颜色。</summary>
    public string GlowColor { get; set; } = "#FFFFB83D";

    /// <summary>全局文字光晕半径；0 表示关闭光晕。</summary>
    public double GlowRadius { get; set; } = 16;

    /// <summary>全局文字光晕不透明度。</summary>
    public double GlowOpacity { get; set; } = 0.85;

    /// <summary>曲名小字颜色。</summary>
    public string SubTextColor { get; set; } = "#B0FFFFFF";

    /// <summary>全局不透明度。</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>是否显示曲名小字。</summary>
    public bool ShowTrackInfo { get; set; } = true;

    /// <summary>应用到所有歌词框的动态效果。Shake 表示上下，Float 表示左右；保留旧枚举值兼容配置。</summary>
    public LyricEffectMode LyricEffect { get; set; } = LyricEffectMode.None;

    /// <summary>上下/左右运动幅度，范围 0.1 到 1。</summary>
    public double EffectIntensity { get; set; } = 0.55;

    /// <summary>旧版动效方向；用于将原先的“效果 + 方向”迁移到上下/左右效果选项。</summary>
    public LyricMotionDirection MotionDirection { get; set; } = LyricMotionDirection.Vertical;

    /// <summary>上下/左右运动往返一次的周期，单位毫秒。</summary>
    public int MotionPeriodMs { get; set; } = 1200;

    /// <summary>单个粒子的直径，单位像素。</summary>
    public double ParticleSize { get; set; } = 6;

    /// <summary>每轮散开的粒子数量。</summary>
    public int ParticleCount { get; set; } = 14;

    /// <summary>粒子离开歌词文字后的移动距离，单位像素。</summary>
    public double ParticleSpread { get; set; } = 72;

    /// <summary>一轮粒子散开与淡出的总时长，单位毫秒。</summary>
    public int ParticleDurationMs { get; set; } = 1100;

    // ---------------------------------------------------------------
    // 动画
    // ---------------------------------------------------------------

    /// <summary>淡入时长（毫秒）。</summary>
    public int FadeInMs { get; set; } = 260;

    /// <summary>淡出时长（毫秒）。</summary>
    public int FadeOutMs { get; set; } = 420;

    /// <summary>淡入淡出与从左至右歌词高亮为互斥的播放效果。</summary>
    public PlaybackEffectMode PlaybackEffect { get; set; } = PlaybackEffectMode.Fade;

    /// <summary>
    /// 歌词框保留当前歌词，直到该框轮到自己的下一句前再淡出。
    /// 关闭时沿用每次全局换句都淡出其它框的旧行为。
    /// </summary>
    public bool HoldUntilNextFrameLine { get; set; } = true;

    /// <summary>
    /// 字间距（像素）。0 表示不改，负数更紧凑、正数更松散。
    ///
    /// 非零间距由显示层逐字排版；0 使用正常文本排版。
    /// </summary>
    public double LetterSpacing { get; set; }

    /// <summary>淡入完成后允许歌词继续停留的时长；0 表示不设时长上限（毫秒）。</summary>
    public int HoldMs { get; set; } = 0;

    /// <summary>
    /// 该给第 <paramref name="lineIndex"/> 句歌词用哪个框。
    ///
    /// 这是**取模分配**：2 个框时，框 0 拿第 0、2、4… 句，框 1 拿第 1、3、5… 句。
    /// 多加一个框，循环周期就多一格。
    ///
    /// 返回的是框对象本身，不是下标。
    /// 为什么不用下标：回到"启用框列表里的第几个"和"原始列表里的第几个"是不同的东西，
    /// 停用中间某个框之后两者会错位，调用方极易用错（这个坑是单元测试抓出来的）。
    /// 直接给对象就没有歧义了。
    ///
    /// 算不出可用框时返回 null（比如一个框都没启用）。
    /// </summary>
    public FrameConfig? GetFrameForLine(int lineIndex)
    {
        if (lineIndex < 0)
        {
            return null;
        }

        var enabledCount = 0;
        foreach (var frame in Frames)
        {
            if (frame.Enabled)
            {
                enabledCount++;
            }
        }

        if (enabledCount == 0)
        {
            return null;
        }

        var target = lineIndex % enabledCount;
        foreach (var frame in Frames)
        {
            if (frame.Enabled && target-- == 0)
            {
                return frame;
            }
        }

        return null;
    }

    /// <summary>当前启用的框。</summary>
    public List<FrameConfig> GetEnabledFrames()
        => Frames.Where(f => f.Enabled).ToList();

    /// <summary>
    /// 深拷贝。
    ///
    /// 用途是**样式快照**：主循环每 100 毫秒要比对"全局样式有没有变"，
    /// 直接持有配置对象是不行的（它会被就地修改，比对永远相等），
    /// 所以必须存一份独立的副本。
    /// </summary>
    public FramesConfig Clone()
    {
        // 值类型和不可变字符串直接复制，新增样式字段也自动保留。
        // 可变列表单独深拷贝，防止草稿/快照修改泄漏到当前配置。
        var copy = (FramesConfig)MemberwiseClone();
        copy.TextGradientColors = TextGradientColors?.ToList() ?? new List<string>();
        copy.Frames = Frames.Select(f => f.Clone()).ToList();
        return copy;
    }

    /// <summary>
    /// 首次运行时造出默认的两个框：一个偏上、一个偏下，水平居中。
    /// </summary>
    /// <param name="screenWidth">屏幕宽度（设备无关像素）。</param>
    /// <param name="screenHeight">屏幕高度。</param>
    public static List<FrameConfig> CreateDefaultFrames(double screenWidth, double screenHeight)
    {
        const double width = 900;
        const double height = 100;
        var left = Math.Max(0, (screenWidth - width) / 2);

        return new List<FrameConfig>
        {
            new()
            {
                Name = "歌词框 1",
                Left = left,
                Top = Math.Max(0, screenHeight * 0.18),
                Width = width,
                Height = height,
            },
            new()
            {
                Name = "歌词框 2",
                Left = left,
                Top = Math.Max(0, screenHeight * 0.30),
                Width = width,
                Height = height,
            },
        };
    }
}
