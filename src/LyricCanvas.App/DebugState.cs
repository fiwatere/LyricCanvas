using System.IO;

namespace LyricCanvas.App;

/// <summary>
/// 调试用的状态覆盖。
///
/// 为什么需要它：验证"歌词有没有在正确的时间点切到正确的那一行"，必须在
/// 指定的播放进度上观察窗口。但真实播放器的进度没法用脚本精确控制，
/// 所以提供测试状态入口：环境变量 LYRICCANVAS_DEBUG_STATE 指向一个 JSON 文件，
/// 里面有曲名和播放进度，程序就用它代替 SMTC 的真实数据。
///
/// 只在设置了该环境变量时才生效，正常运行完全不受影响。
///
/// JSON 形如：
/// <code>
/// { "title": "测试歌曲", "artist": "测试歌手", "positionSeconds": 12.5 }
/// </code>
/// 文件内容每次刷新都会重新读，所以测试脚本可以在程序运行期间改进度。
/// </summary>
internal sealed class DebugState
{
    public const string EnvironmentVariableName = "LYRICCANVAS_DEBUG_STATE";
    private const string LegacyEnvironmentVariableName = "LYRICDESKTOP_DEBUG_STATE";
    private static readonly System.Text.Json.JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>覆盖用的曲名。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>覆盖用的歌手。</summary>
    public string Artist { get; set; } = string.Empty;

    /// <summary>覆盖用的播放进度（秒）。</summary>
    public double PositionSeconds { get; set; }

    /// <summary>
    /// 覆盖用的歌曲总时长（秒）。
    /// 自动化验证"歌词匹配"要靠它做时长校验，所以必须能指定。
    /// </summary>
    public double DurationSeconds { get; set; }

    /// <summary>是否正在播放。暂停时进度不再推进。</summary>
    public bool IsPlaying { get; set; } = true;

    /// <summary>
    /// 读取调试状态。没设环境变量、文件不存在或内容坏掉时返回 null，
    /// 表示"用真实数据"。
    /// </summary>
    public static DebugState? TryLoad()
    {
        var path = Environment.GetEnvironmentVariable(EnvironmentVariableName);
        if (string.IsNullOrWhiteSpace(path))
        {
            path = Environment.GetEnvironmentVariable(LegacyEnvironmentVariableName);
        }
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(path);
            return System.Text.Json.JsonSerializer.Deserialize<DebugState>(
                json, SerializerOptions);
        }
        catch
        {
            // 调试文件写了一半、或格式不对，都当作没有覆盖，不要影响正常运行
            return null;
        }
    }
}
