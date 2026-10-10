namespace LyricCanvas.Core;

/// <summary>
/// 界面上那两行文字该显示什么，纯粹由数据决定。
///
/// 为什么单独抽出来：程序本身和自动化验证脚本都需要知道"这种数据应该显示成什么"。
/// 如果规则只写在界面代码里，验证脚本就只能靠猜，两边一旦不一致就会报假失败
/// （实际发生过一次：给无歌词的曲目加了提示后缀，验证脚本按"完全相同"判定，
/// 结果误报失败）。放在 Core 里，程序调用它、验证脚本对照它，规则只有一份。
/// </summary>
public static class NowPlayingDisplay
{
    /// <summary>没有任何会话时，主行显示的提示。</summary>
    public const string NoTrackMessage = "当前没有正在播放的音乐";

    /// <summary>有会话但没找到歌词文件时，主行显示的后缀。</summary>
    public const string NoLyricsSuffix = "（未找到歌词文件）";

    /// <summary>
    /// 副行（小字）该显示什么。没有曲目时显示空字符串。
    /// </summary>
    public static string GetSubText(NowPlaying? nowPlaying)
        => nowPlaying is null || !nowPlaying.HasTrack ? string.Empty : nowPlaying.DisplayText;
}
