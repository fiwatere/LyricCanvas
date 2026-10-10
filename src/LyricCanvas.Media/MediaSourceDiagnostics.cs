using LyricCanvas.Core;

namespace LyricCanvas.Media;

/// <summary>供设置页诊断当前选中的 Windows 媒体会话及其时间轴更新情况。</summary>
public sealed record MediaSourceDiagnostics(
    int SessionCount,
    string AppId,
    string Title,
    PlaybackState Playback,
    TimeSpan ReportedPosition,
    TimeSpan EstimatedPosition,
    DateTimeOffset SampledAtUtc,
    DateTimeOffset? TimelineUpdatedAtUtc,
    string SelectionStatus)
{
    public static MediaSourceDiagnostics Empty { get; } = new(
        0,
        string.Empty,
        string.Empty,
        PlaybackState.Unknown,
        TimeSpan.Zero,
        TimeSpan.Zero,
        DateTimeOffset.MinValue,
        null,
        "尚未发现媒体会话");
}
