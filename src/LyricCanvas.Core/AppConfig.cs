namespace LyricCanvas.Core;

/// <summary>
/// 应用的配置项。
///
/// 结构分为全局样式、活动布局方案和其它应用设置：
///   - <see cref="Frames"/>：公共样式，以及活动方案的框列表（兼容现有窗口代码）；
///   - <see cref="LayoutSchemes"/>：持久保存的具名布局方案；
///   - 显示内容相关的字段：显示歌词还是自定义文本；
///   - 行为相关的字段：是否后台运行、热键、是否置顶等。
///
/// 字段命名按 JSON 里的键名走，方便直接手改配置文件；解析对大小写、注释、
/// 缺失字段都是宽容的（见 ConfigService）。新增字段给个合理默认值即可，
/// 老配置文件缺字段也能正常加载。
/// </summary>
public sealed class AppConfig
{
    /// <summary>当前配置格式版本。以后结构变化时用它做迁移判断。</summary>
    public int Version { get; set; } = 7;

    /// <summary>
    /// 全局歌词样式与当前活动方案的歌词框列表。
    /// </summary>
    public FramesConfig Frames { get; set; } = new();

    /// <summary>所有保存的布局方案。Frames.Frames 是当前活动方案的兼容投影。</summary>
    public List<LayoutSchemeConfig> LayoutSchemes { get; set; } = new();

    /// <summary>当前活动布局方案的标识。</summary>
    public string ActiveLayoutId { get; set; } = string.Empty;

    // ---------------------------------------------------------------
    // 显示内容
    // ---------------------------------------------------------------

    /// <summary>显示什么：歌词，还是用户自定义的固定文本。</summary>
    public ContentMode ContentMode { get; set; } = ContentMode.Lyrics;

    /// <summary>
    /// 自定义文本的内容。<see cref="ContentMode.CustomText"/> 模式下显示它。
    /// 可以写多行（用换行分隔）。
    /// </summary>
    public string CustomText { get; set; } = string.Empty;

    /// <summary>自定义文本显示的默认内容，第一次使用时给个提示，不然用户不知道该填什么。</summary>
    public const string DefaultCustomText = "在这里输入要显示的文字";

    // ---------------------------------------------------------------
    // 窗口行为
    // ---------------------------------------------------------------

    /// <summary>是否始终显示在其他窗口之上。</summary>
    public bool Topmost { get; set; } = true;

    /// <summary>
    /// 关闭设置窗口后是否继续在后台运行（托盘里保留图标）。
    /// 关掉的话，关闭窗口就等于退出程序。
    /// </summary>
    public bool RunInBackground { get; set; } = true;

    /// <summary>
    /// 是否启用全局热键。
    /// 穿透状态下鼠标点不到歌词框，热键是打开布局编辑器的可靠入口。
    /// </summary>
    public bool HotkeyEnabled { get; set; } = true;

    /// <summary>打开布局编辑器的热键：主键名（例如 "E"）。</summary>
    public string HotkeyKey { get; set; } = "E";

    /// <summary>热键是否需要 Ctrl。</summary>
    public bool HotkeyCtrl { get; set; } = true;

    /// <summary>热键是否需要 Alt。</summary>
    public bool HotkeyAlt { get; set; } = true;

    /// <summary>热键是否需要 Shift。</summary>
    public bool HotkeyShift { get; set; }

    // ---------------------------------------------------------------
    // 歌词来源
    // ---------------------------------------------------------------

    /// <summary>额外的歌词搜索目录。空表示只用程序目录下的 lyrics。</summary>
    public string LyricsDirectory { get; set; } = string.Empty;

    /// <summary>
    /// 本地找不到歌词时，是否允许联网抓取。
    /// 关掉之后只认歌词目录里的文件（包含之前抓下来的缓存）。
    /// </summary>
    public bool FetchLyricsFromNetwork { get; set; } = true;

    /// <summary>
    /// 是否启用本地系统音频识别来校准歌词进度。默认关闭，避免未明确开启时捕获系统输出音频。
    /// </summary>
    public bool AudioLyricSyncEnabled { get; set; }

    /// <summary>用户额外允许读取的媒体应用 ID；一行一个，精确匹配。</summary>
    public string MediaAppAllowList { get; set; } = string.Empty;

    /// <summary>按曲名和歌手记录的逐首歌词时间偏移。</summary>
    public List<SongLyricOffset> SongLyricOffsets { get; set; } = new();

    public int GetSongLyricOffsetMs(string? title, string? artist)
    {
        var key = SongLyricOffset.CreateKey(title, artist);
        if (SongLyricOffset.NormalizePart(title).Length == 0)
        {
            return 0;
        }

        return SongLyricOffsets.FirstOrDefault(offset =>
            string.Equals(SongLyricOffset.CreateKey(offset.Title, offset.Artist), key, StringComparison.Ordinal))?.OffsetMs ?? 0;
    }

    public void SetSongLyricOffsetMs(string? title, string? artist, int offsetMs)
    {
        var key = SongLyricOffset.CreateKey(title, artist);
        if (SongLyricOffset.NormalizePart(title).Length == 0)
        {
            return;
        }

        SongLyricOffsets ??= new List<SongLyricOffset>();
        SongLyricOffsets.RemoveAll(offset =>
            string.Equals(SongLyricOffset.CreateKey(offset.Title, offset.Artist), key, StringComparison.Ordinal));

        if (offsetMs != 0)
        {
            SongLyricOffsets.Add(new SongLyricOffset
            {
                Title = title?.Trim() ?? string.Empty,
                Artist = artist?.Trim() ?? string.Empty,
                OffsetMs = Math.Clamp(offsetMs, -10_000, 10_000),
            });
        }
    }

    /// <summary>
    /// 复制一份。
    ///
    /// 注意要深拷贝 <see cref="Frames"/>：它是可变对象，
    /// 浅拷贝会让"配置快照"和"正在用的配置"指向同一份列表，
    /// 界面改动会直接影响到快照，变化检测就失效了。
    /// </summary>
    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.Frames = Frames.Clone();
        copy.LayoutSchemes = LayoutSchemes.Select(layout => layout.Clone()).ToList();
        copy.SongLyricOffsets = SongLyricOffsets.Select(offset => offset.Clone()).ToList();

        return copy;
    }

    /// <summary>把活动框列表同步到活动方案，供现有窗口和设置代码继续使用。</summary>
    public void SaveActiveFramesToLayout()
    {
        var active = LayoutSchemes.FirstOrDefault(layout =>
            string.Equals(layout.Id, ActiveLayoutId, StringComparison.Ordinal));
        if (active is not null)
        {
            active.Frames = Frames.Frames.Select(frame => frame.Clone()).ToList();
        }
    }
}
