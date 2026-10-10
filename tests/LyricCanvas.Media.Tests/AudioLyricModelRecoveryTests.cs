using LyricCanvas.Media;

namespace LyricCanvas.Media.Tests;

public sealed class AudioLyricModelRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "lyriccanvas-model-recovery-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void 词绘管理目录中的无效大模型会被隔离保留()
    {
        var modelDirectory = Path.Combine(_root, "models");
        Directory.CreateDirectory(modelDirectory);
        var modelPath = Path.Combine(modelDirectory, "ggml-tiny-q5_1.bin");
        var invalidBytes = new byte[1024 * 1024 + 16];
        Random.Shared.NextBytes(invalidBytes);
        File.WriteAllBytes(modelPath, invalidBytes);

        var quarantinedPath = AudioLyricModelRecovery.TryQuarantine(
            modelPath,
            [modelDirectory],
            new DateTimeOffset(2026, 10, 10, 12, 30, 0, TimeSpan.Zero));

        Assert.False(File.Exists(modelPath));
        Assert.NotNull(quarantinedPath);
        Assert.EndsWith(".invalid-20261010-123000000", quarantinedPath, StringComparison.Ordinal);
        Assert.Equal(invalidBytes, File.ReadAllBytes(quarantinedPath!));
    }

    [Fact]
    public void 非词绘管理目录中的模型不会被移动()
    {
        var unrelatedDirectory = Path.Combine(_root, "user-models");
        Directory.CreateDirectory(unrelatedDirectory);
        var modelPath = Path.Combine(unrelatedDirectory, "ggml-tiny-q5_1.bin");
        File.WriteAllText(modelPath, "user model");

        var quarantinedPath = AudioLyricModelRecovery.TryQuarantine(
            modelPath,
            [Path.Combine(_root, "LyricCanvas", "models")],
            DateTimeOffset.UtcNow);

        Assert.Null(quarantinedPath);
        Assert.Equal("user model", File.ReadAllText(modelPath));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
