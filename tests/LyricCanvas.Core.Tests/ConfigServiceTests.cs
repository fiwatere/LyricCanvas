using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 配置服务的单元测试。
///
/// 重点覆盖三类容易出事的地方：
///   1. 落盘后能原样读回来（不然用户每次重启都丢设置）；
///   2. 非法值被拉回合理范围（手改配置文件写错了不能让界面崩）；
///   3. 配置文件损坏时不抛异常、退回默认值（而且不覆盖原文件）。
/// </summary>
public sealed class ConfigServiceTests : IDisposable
{
    private readonly string _directory;
    private readonly string _file;

    public ConfigServiceTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "lyricdesktop-config-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _file = Path.Combine(_directory, "config.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 默认配置的值是合理的()
    {
        var path = Path.Combine(_directory, "defaults.json");
        using var service = new ConfigService(path, autoSave: false);

        var config = service.Current;

        Assert.Equal(1.0, config.Frames.Opacity);
        Assert.Equal(44, config.Frames.FontSize);
        Assert.Equal("Microsoft YaHei UI", config.Frames.FontFamily);
        Assert.True(config.Topmost);
        // 框默认是锁定的（鼠标穿透）：不挡游戏操作是默认行为
        Assert.All(config.Frames.Frames, f => Assert.True(f.Locked));
        Assert.True(config.Frames.ShowTrackInfo);

        // 默认会造出两个框，位置是算出来的具体值而不是空
        Assert.Equal(2, config.Frames.Frames.Count);
        Assert.All(config.Frames.Frames, f => Assert.True(f.Width > 0 && f.Height > 0));
    }

    [Fact]
    public void 没有配置文件时用默认值且不抛异常()
    {
        using var service = new ConfigService(_file, autoSave: false);

        Assert.False(File.Exists(_file));
        Assert.Equal(1.0, service.Current.Frames.Opacity);
    }

    [Fact]
    public void 修改配置后能保存并读回()
    {
        using (var service = new ConfigService(_file, autoSave: false))
        {
            service.Update(c =>
            {
                c.Frames.Opacity = 0.42;
                c.Frames.FontSize = 72;
                c.Frames.TextColor = "#FF00FF00";

                // 位置是每个框各自的，所以得先有框
                c.Frames.Frames = FramesConfig.CreateDefaultFrames(1920, 1080);
                c.Frames.Frames[0].Locked = false;
                c.Frames.Frames[0].Left = 123.5;
                c.Frames.Frames[0].Top = 456.25;
            });
            service.SaveNow();
        }

        Assert.True(File.Exists(_file));

        using var reloaded = new ConfigService(_file, autoSave: false);
        var config = reloaded.Current;

        Assert.Equal(0.42, config.Frames.Opacity);
        Assert.Equal(72, config.Frames.FontSize);
        Assert.Equal("#FF00FF00", config.Frames.TextColor);

        // 上面把它设成了 false，读回来应该也是 false（验证每个框的锁定状态能独立存取）
        Assert.False(config.Frames.Frames[0].Locked);
        Assert.Equal(123.5, config.Frames.Frames[0].Left);
        Assert.Equal(456.25, config.Frames.Frames[0].Top);
    }

    [Fact]
    public void 自动保存会在延迟后落盘()
    {
        using var service = new ConfigService(_file, autoSave: true, saveDelay: TimeSpan.FromMilliseconds(50));

        service.Update(c => c.Frames.FontSize = 60);

        // 等防抖延迟过去
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!File.Exists(_file) && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(50);
        }

        Assert.True(File.Exists(_file));

        using var reloaded = new ConfigService(_file, autoSave: false);
        Assert.Equal(60, reloaded.Current.Frames.FontSize);
    }

    [Fact]
    public async Task 保存期间提交的新修改仍保持待保存并能最终落盘()
    {
        using var service = new ConfigService(_file, autoSave: false);
        using var snapshotSaved = new ManualResetEventSlim();
        using var allowSaveToFinish = new ManualResetEventSlim();

        service.Update(config => config.Frames.FontSize = 60);
        service.AfterSnapshotSavedForTests = () =>
        {
            snapshotSaved.Set();
            allowSaveToFinish.Wait();
        };

        var firstSave = Task.Run(service.SaveNow);
        try
        {
            Assert.True(snapshotSaved.Wait(TimeSpan.FromSeconds(5)), "第一次快照没有及时写入。");
            service.Update(config => config.Frames.FontSize = 88);
        }
        finally
        {
            allowSaveToFinish.Set();
        }

        Assert.True(await firstSave);
        Assert.True(service.IsDirty);
        Assert.True(service.HasUnpersistedUserChanges);

        service.AfterSnapshotSavedForTests = null;
        Assert.True(service.SaveNow());

        using var reloaded = new ConfigService(_file, autoSave: false);
        Assert.Equal(88, reloaded.Current.Frames.FontSize);
    }

    [Fact]
    public void 新版配置存在时旧版配置时间戳较新也不会覆盖()
    {
        var currentPath = Path.Combine(_directory, "current", "config.json");
        var fallbackPath = Path.Combine(_directory, "fallback", "config.json");
        var legacyPath = Path.Combine(_directory, "LyricDesktop", "config.json");

        using (var current = new ConfigService(currentPath, autoSave: false))
        {
            current.Update(config => config.Frames.FontSize = 91);
            Assert.True(current.SaveNow());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
        File.WriteAllText(legacyPath, """{ "version": 7, "frames": { "fontSize": 37 } }""");
        File.SetLastWriteTimeUtc(legacyPath, DateTime.UtcNow.AddMinutes(5));

        using var loaded = new ConfigService(
            currentPath,
            autoSave: false,
            saveDelay: null,
            fallbackFilePaths: new[] { fallbackPath },
            legacyReadFilePaths: new[] { legacyPath });

        Assert.Equal(91, loaded.Current.Frames.FontSize);
        Assert.Equal(currentPath, loaded.FilePath);
    }

    [Fact]
    public void 变更会触发通知并带上新值()
    {
        using var service = new ConfigService(filePath: null);

        AppConfig? received = null;
        service.Changed += (_, c) => received = c;

        service.Update(c => c.Frames.FontSize = 88);

        Assert.NotNull(received);
        Assert.Equal(88, received!.Frames.FontSize);
    }

    [Fact]
    public void 拿到的配置是副本改它不影响内部状态()
    {
        using var service = new ConfigService(filePath: null);

        var copy = service.Current;
        copy.Frames.FontSize = 999;

        Assert.Equal(44, service.Current.Frames.FontSize);
    }

    // ---------------------------------------------------------------
    // 非法值兜底
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(-5.0, 0.05)]
    [InlineData(0.0, 0.05)]
    [InlineData(1.5, 1.0)]
    [InlineData(0.5, 0.5)]
    public void 不透明度会被限制在合理范围(double input, double expected)
    {
        using var service = new ConfigService(filePath: null);

        service.Update(c => c.Frames.Opacity = input);

        Assert.Equal(expected, service.Current.Frames.Opacity, precision: 6);
    }

    [Theory]
    [InlineData(5.0, 10.0)]
    [InlineData(500.0, 200.0)]
    [InlineData(48.0, 48.0)]
    public void 字号会被限制在合理范围(double input, double expected)
    {
        using var service = new ConfigService(filePath: null);

        service.Update(c => c.Frames.FontSize = input);

        Assert.Equal(expected, service.Current.Frames.FontSize, precision: 6);
    }

    [Fact]
    public void 空字体名会被换成默认字体()
    {
        using var service = new ConfigService(filePath: null);

        service.Update(c => c.Frames.FontFamily = "   ");

        Assert.Equal("Microsoft YaHei UI", service.Current.Frames.FontFamily);
    }

    [Fact]
    public void 空颜色值会被换成默认颜色()
    {
        using var service = new ConfigService(filePath: null);

        service.Update(c =>
        {
            c.Frames.TextColor = string.Empty;
            c.Frames.SubTextColor = string.Empty;
        });

        Assert.Equal("#FFFFD866", service.Current.Frames.TextColor);
        Assert.Equal("#B0FFFFFF", service.Current.Frames.SubTextColor);
    }

    // ---------------------------------------------------------------
    // 坏文件
    // ---------------------------------------------------------------

    [Fact]
    public void 配置文件内容是乱码时退回默认值且不崩溃()
    {
        File.WriteAllText(_file, "{ 这不是合法的 JSON ][");

        using var service = new ConfigService(_file, autoSave: false);

        Assert.Equal(1.0, service.Current.Frames.Opacity);
        Assert.Equal(44, service.Current.Frames.FontSize);
    }

    [Fact]
    public void 配置文件损坏时不会被自动覆盖()
    {
        const string original = "{ 坏内容";
        File.WriteAllText(_file, original);

        using (var service = new ConfigService(_file, autoSave: true, saveDelay: TimeSpan.FromMilliseconds(30)))
        {
            // 用一个能正常加载的路径做对照，确保"不写盘"不是因为路径写错了
            Assert.Equal(44, service.Current.Frames.FontSize);

            // 就算改了配置、等过防抖延迟、再退出，也不能覆盖那个坏文件
            service.Update(c => c.Frames.FontSize = 123);
            Thread.Sleep(200);
        }

        Assert.Equal(original, File.ReadAllText(_file));
    }

    [Fact]
    public void 损坏文件被手工修好后可以正常写入()
    {
        File.WriteAllText(_file, "{ 坏内容");

        // 第一次：坏文件，拒绝写盘
        using (var broken = new ConfigService(_file, autoSave: false))
        {
            broken.Update(c => c.Frames.FontSize = 50);
            broken.SaveNow();
        }

        Assert.Equal("{ 坏内容", File.ReadAllText(_file));

        // 用户手工修好文件（或删掉它）之后，程序应该恢复正常保存能力
        File.Delete(_file);

        using (var healthy = new ConfigService(_file, autoSave: false))
        {
            healthy.Update(c => c.Frames.FontSize = 50);
            healthy.SaveNow();
        }

        using var reloaded = new ConfigService(_file, autoSave: false);
        Assert.Equal(50, reloaded.Current.Frames.FontSize);
    }

    [Fact]
    public void 配置文件缺少字段时其余字段仍按默认值加载()
    {
        File.WriteAllText(_file, """{ "frames": { "fontSize": 66 } }""");

        using var service = new ConfigService(_file, autoSave: false);

        Assert.Equal(66, service.Current.Frames.FontSize);
        Assert.Equal(1.0, service.Current.Frames.Opacity);
        Assert.Equal("Microsoft YaHei UI", service.Current.Frames.FontFamily);
    }

    [Theory]
    [InlineData("""{ "frames": { "fontSize": 66 } }""")]      // 小驼峰（手改时最常写的形式）
    [InlineData("""{ "frames": { "FontSize": 66 } }""")]      // 大驼峰（程序自己写出来的形式）
    [InlineData("""{ "frames": { "FONTSIZE": 66 } }""")]      // 全大写
    public void 键名大小写不影响读取(string json)
    {
        File.WriteAllText(_file, json);

        using var service = new ConfigService(_file, autoSave: false);

        Assert.Equal(66, service.Current.Frames.FontSize);
    }

    [Fact]
    public void 配置文件里可以写注释和尾随逗号()
    {
        File.WriteAllText(_file, """
            {
              // 这是注释，手改配置时很有用
              "frames": {
                "fontSize": 66,
                "opacity": 0.5
              },
            }
            """);

        using var service = new ConfigService(_file, autoSave: false);

        Assert.Equal(66, service.Current.Frames.FontSize);
        Assert.Equal(0.5, service.Current.Frames.Opacity);
    }

    [Fact]
    public void 保存时会创建不存在的目录()
    {
        var nested = Path.Combine(_directory, "a", "b", "config.json");

        using (var service = new ConfigService(nested, autoSave: false))
        {
            service.Update(c => c.Frames.FontSize = 50);
            service.SaveNow();
        }

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void 保存不会留下临时的中间文件()
    {
        using (var service = new ConfigService(_file, autoSave: false))
        {
            service.Update(c => c.Frames.FontSize = 52);
            service.SaveNow();
        }

        Assert.False(File.Exists(_file + ".tmp"));
    }

    [Fact]
    public void Dispose时会保存待写入的改动()
    {
        using (var service = new ConfigService(_file, autoSave: true, saveDelay: TimeSpan.FromMinutes(10)))
        {
            // 防抖延迟很长，正常不会触发定时保存
            service.Update(c => c.Frames.FontSize = 99);
        }

        using var reloaded = new ConfigService(_file, autoSave: false);
        Assert.Equal(99, reloaded.Current.Frames.FontSize);
    }
}
