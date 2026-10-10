namespace LyricCanvas.Core;

/// <summary>
/// 播放状态。这里刻意不直接用 Windows 的
/// <c>GlobalSystemMediaTransportControlsSessionPlaybackStatus</c>，
/// 而是定义一份自己的枚举。
///
/// 原因：LyricCanvas.Core 要在没有 Windows 环境的机器上也能编译和跑单元测试。
/// 一旦引用 WinRT 类型，测试项目就得跟着变成 Windows 专用，纯逻辑的测试价值就没了。
/// WinRT 那套状态在 LyricCanvas.Media 里做一次映射即可。
/// </summary>
public enum PlaybackState
{
    /// <summary>没拿到任何会话，或状态未知。</summary>
    Unknown = 0,

    /// <summary>已关闭。</summary>
    Closed = 1,

    /// <summary>已打开但没在播。</summary>
    Opened = 2,

    /// <summary>正在播放。</summary>
    Playing = 4,

    /// <summary>暂停。</summary>
    Paused = 5,

    /// <summary>快进 / 快退中。</summary>
    Changing = 6,

    /// <summary>已停止。</summary>
    Stopped = 7,
}
