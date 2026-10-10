using LyricCanvas.Media;

namespace LyricCanvas.Media.Tests;

public sealed class AudioLyricSyncLifecycleTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public void 识别与界面排队延迟会按实际播放时间补偿(double delaySeconds)
    {
        var capturedPosition = TimeSpan.FromSeconds(42);
        var delay = TimeSpan.FromSeconds(delaySeconds);

        var appliedPosition = AudioLyricSyncLifecycle.CompensateRecognitionDelay(
            capturedPosition,
            delay,
            isPlaying: true);

        Assert.Equal(capturedPosition + delay, appliedPosition);
    }

    [Fact]
    public void 暂停不推进位置且失效运行的结果不能再应用()
    {
        var lifecycle = new AudioLyricSyncLifecycle();
        var run = lifecycle.TryStart("track-a", isPlaying: true, DateTimeOffset.UnixEpoch);
        Assert.NotNull(run);
        Assert.True(lifecycle.IsCurrent(run.Value));

        var pausedPosition = AudioLyricSyncLifecycle.CompensateRecognitionDelay(
            TimeSpan.FromSeconds(42),
            TimeSpan.FromSeconds(3),
            isPlaying: false);
        lifecycle.Invalidate();

        Assert.Equal(TimeSpan.FromSeconds(42), pausedPosition);
        Assert.False(lifecycle.IsCurrent(run.Value));
        Assert.Null(lifecycle.Complete(run.Value, wasCancelled: false, DateTimeOffset.UnixEpoch));
        Assert.False(lifecycle.IsRunning);
    }

    [Fact]
    public void 校准失败后会释放运行状态并允许同曲目退避重试()
    {
        var lifecycle = new AudioLyricSyncLifecycle();
        var start = DateTimeOffset.UnixEpoch;
        var firstRun = lifecycle.TryStart("track-a", isPlaying: true, start);
        Assert.NotNull(firstRun);

        var firstDelay = lifecycle.Complete(firstRun.Value, wasCancelled: false, start);

        Assert.False(lifecycle.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(2), firstDelay);
        Assert.Null(lifecycle.TryStart("track-a", isPlaying: true, start.AddSeconds(1)));

        var secondRun = lifecycle.TryStart("track-a", isPlaying: true, start.AddSeconds(2));
        Assert.NotNull(secondRun);
        Assert.True(lifecycle.MarkStarted(secondRun.Value));

        var nextDelay = lifecycle.Complete(secondRun.Value, wasCancelled: false, start.AddSeconds(3));

        Assert.False(lifecycle.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(2), nextDelay);
        Assert.NotNull(lifecycle.TryStart("track-a", isPlaying: true, start.AddSeconds(5)));
    }

    [Fact]
    public void 重试等待时间有上限且切换曲目会清空旧退避()
    {
        var lifecycle = new AudioLyricSyncLifecycle();
        var now = DateTimeOffset.UnixEpoch;
        var expectedDelays = new[] { 2, 4, 8, 16, 30, 30 };

        foreach (var expectedSeconds in expectedDelays)
        {
            var run = lifecycle.TryStart("track-a", isPlaying: true, now);
            Assert.NotNull(run);
            var delay = lifecycle.Complete(run.Value, wasCancelled: false, now);
            Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), delay);
            now += delay!.Value;
        }

        var nextTrackRun = lifecycle.TryStart("track-b", isPlaying: true, now);

        Assert.NotNull(nextTrackRun);
    }
}
