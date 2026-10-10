using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 歌词缓存的单元测试。
///
/// 重点验证两件事：
///   1. 缓存目录和用户自己的歌词目录是物理隔离的 —— "清空缓存"绝不能删到用户的文件；
///   2. 同名不同歌手的歌不会互相覆盖。
/// </summary>
public sealed class LyricsCacheTests : IDisposable
{
    private readonly string _root;

    public LyricsCacheTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lyricdesktop-cache-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void 缓存目录是歌词目录下的cache子目录()
    {
        var cache = new LyricsCache(_root);

        Assert.Equal(Path.Combine(_root, "cache"), cache.CacheDirectory);
    }

    [Fact]
    public void 写入后能读回来且文件存在()
    {
        var cache = new LyricsCache(_root);

        var path = cache.Write("起风了", "买辣椒也用券", "[00:01.00]这一路上走走停停", "kugou");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.True(cache.Exists("起风了", "买辣椒也用券"));

        var content = File.ReadAllText(path!);
        Assert.Contains("这一路上走走停停", content, StringComparison.Ordinal);
        Assert.Contains("kugou", content, StringComparison.Ordinal);
    }

    [Fact]
    public void 没写过的歌返回不存在()
    {
        var cache = new LyricsCache(_root);

        Assert.False(cache.Exists("没抓过的歌", "某歌手"));
    }

    [Fact]
    public void 空内容不会被写进缓存()
    {
        var cache = new LyricsCache(_root);

        Assert.Null(cache.Write("歌名", "歌手", string.Empty));
        Assert.Null(cache.Write("歌名", "歌手", "   "));
        Assert.False(cache.Exists("歌名", "歌手"));
    }

    [Fact]
    public void 同名不同歌手的歌不会互相覆盖()
    {
        var cache = new LyricsCache(_root);

        // 《起风了》有原版和一堆翻唱，不能互相盖掉
        var pathA = cache.Write("起风了", "买辣椒也用券", "[00:01.00]原版歌词");
        var pathB = cache.Write("起风了", "某个翻唱歌手", "[00:01.00]翻唱版歌词");

        Assert.NotEqual(pathA, pathB);
        Assert.Contains("原版歌词", File.ReadAllText(pathA!), StringComparison.Ordinal);
        Assert.Contains("翻唱版歌词", File.ReadAllText(pathB!), StringComparison.Ordinal);
    }

    [Fact]
    public void 非法字符长标题和字段边界都不会造成缓存身份碰撞()
    {
        var cache = new LyricsCache(_root);
        var sharedPrefix = new string('长', 140);
        var paths = new[]
        {
            cache.GetCachePath("A/B", "C"),
            cache.GetCachePath("A:B", "C"),
            cache.GetCachePath(sharedPrefix + "甲", "歌手一"),
            cache.GetCachePath(sharedPrefix + "乙", "歌手一"),
            cache.GetCachePath("A - B", "C"),
            cache.GetCachePath("A", "B - C"),
        };

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        cache.Write("A/B", "C", "[00:01.00]第一首");
        cache.Write("A:B", "C", "[00:01.00]第二首");
        Assert.Contains("第一首", cache.Read("A/B", "C")!.LyricsText, StringComparison.Ordinal);
        Assert.Contains("第二首", cache.Read("A:B", "C")!.LyricsText, StringComparison.Ordinal);
    }

    [Fact]
    public void 缓存保留匹配确认状态()
    {
        var cache = new LyricsCache(_root);
        cache.Write(
            "测试曲目",
            "目标歌手",
            "[00:01.00]歌词",
            "kugou",
            needsConfirmation: true,
            message: "歌手信息不一致",
            matchedCandidateKey: "candidate-key");

        var unresolved = cache.Read("测试曲目", "目标歌手");
        Assert.NotNull(unresolved);
        Assert.True(unresolved!.NeedsConfirmation);
        Assert.Equal("kugou", unresolved.SourceName);
        Assert.Equal("candidate-key", unresolved.MatchedCandidateKey);

        Assert.True(cache.MarkConfirmed("测试曲目", "目标歌手"));
        var confirmed = cache.Read("测试曲目", "目标歌手");
        Assert.NotNull(confirmed);
        Assert.False(confirmed!.NeedsConfirmation);
        Assert.True(confirmed.IsConfirmed);
    }

    [Fact]
    public void 没有歌手时也能正常缓存()
    {
        var cache = new LyricsCache(_root);

        var path = cache.Write("纯音乐", string.Empty, "[00:01.00]纯音乐请欣赏");

        Assert.NotNull(path);
        Assert.True(cache.Exists("纯音乐", string.Empty));
    }

    [Fact]
    public void 曲名里的非法文件名字符会被替换()
    {
        var cache = new LyricsCache(_root);

        // 曲名里带 / 和 : 在 Windows 上是非法文件名字符
        var path = cache.Write("AC/DC: Back in Black", "AC/DC", "[00:01.00]歌词");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.DoesNotContain('/', Path.GetFileName(path!));
        Assert.DoesNotContain(':', Path.GetFileName(path!));
    }

    [Fact]
    public void 超长曲名会被截短()
    {
        var cache = new LyricsCache(_root);
        var longTitle = new string('长', 500);

        var path = cache.Write(longTitle, "歌手", "[00:01.00]歌词");

        Assert.NotNull(path);
        Assert.True(Path.GetFileName(path!).Length <= 128, "文件名不该超过 128 个字符");
    }

    // ---------------------------------------------------------------
    // 清空缓存 —— 最关键的一条：不能动用户的文件
    // ---------------------------------------------------------------

    [Fact]
    public void 清空缓存只删缓存不碰用户自己的歌词()
    {
        var cache = new LyricsCache(_root);

        // 用户自己放的歌词
        var userLyric = Path.Combine(_root, "起风了.lrc");
        File.WriteAllText(userLyric, "[00:01.00]用户自己放的歌词");

        // 程序自动抓的
        cache.Write("晴天", "周杰伦", "[00:01.00]自动抓的歌词");
        cache.Write("七里香", "周杰伦", "[00:01.00]自动抓的歌词2");

        var removed = cache.Clear();

        Assert.Equal(2, removed);
        Assert.True(File.Exists(userLyric), "用户自己的歌词文件绝不能被删掉");
        Assert.Equal("[00:01.00]用户自己放的歌词", File.ReadAllText(userLyric));
        Assert.False(cache.Exists("晴天", "周杰伦"));
        Assert.False(cache.Exists("七里香", "周杰伦"));
    }

    [Fact]
    public void 清空缓存时会保留缓存目录本身()
    {
        var cache = new LyricsCache(_root);
        cache.Write("某歌", "某歌手", "[00:01.00]歌词");

        cache.Clear();

        Assert.True(Directory.Exists(cache.CacheDirectory), "缓存目录本身要留着，不然下次写入还要重建");
    }

    [Fact]
    public void 清空缓存只处理顶层可识别缓存并保留未知与嵌套文件()
    {
        var cache = new LyricsCache(_root);
        var managedPath = cache.Write("程序缓存", "歌手", "[00:01.00]歌词");
        var unknownPath = Path.Combine(cache.CacheDirectory, "我的备份.lrc");
        File.WriteAllText(unknownPath, "[00:01.00]没有词绘缓存标记");
        var nestedDirectory = Path.Combine(cache.CacheDirectory, "手工子目录");
        Directory.CreateDirectory(nestedDirectory);
        var nestedPath = Path.Combine(nestedDirectory, "保留.lrc");
        File.WriteAllText(nestedPath, "[00:01.00]嵌套文件");

        Assert.Equal(1, cache.Clear());
        Assert.False(File.Exists(managedPath!));
        Assert.False(File.Exists(managedPath + ".meta.json"));
        Assert.True(File.Exists(unknownPath));
        Assert.True(File.Exists(nestedPath));
    }

    [Fact]
    public void 无身份信息的旧缓存保留但不再命中()
    {
        var cache = new LyricsCache(_root);
        Directory.CreateDirectory(cache.CacheDirectory);
        var oldPath = Path.Combine(cache.CacheDirectory, "旧版缓存.lrc");
        File.WriteAllText(oldPath, "# 抓取时间: 2026-10-10 12:00:00\n[00:01.00]旧歌词");

        Assert.False(cache.Exists("旧版缓存", string.Empty));
        Assert.Null(cache.Read("旧版缓存", string.Empty));
        Assert.Equal(1, cache.GetInfo().IgnoredLegacyFileCount);
        Assert.Equal(1, cache.Clear());
        Assert.False(File.Exists(oldPath));
    }

    [Fact]
    public void 缓存为空时清空返回零且不报错()
    {
        var cache = new LyricsCache(_root);

        Assert.Equal(0, cache.Clear());
        Assert.Equal(0, cache.Clear());
    }

    // ---------------------------------------------------------------
    // 统计
    // ---------------------------------------------------------------

    [Fact]
    public void 统计能反映缓存的文件数与大小()
    {
        var cache = new LyricsCache(_root);

        var empty = cache.GetInfo();
        Assert.Equal(0, empty.FileCount);
        Assert.Equal(0, empty.TotalBytes);

        cache.Write("歌一", "歌手", "[00:01.00]歌词内容");
        cache.Write("歌二", "歌手", "[00:01.00]歌词内容");

        var info = cache.GetInfo();
        Assert.Equal(2, info.FileCount);
        Assert.True(info.TotalBytes > 0);
        Assert.Equal(cache.CacheDirectory, info.Directory);
    }

    [Fact]
    public void 统计不会把用户自己的歌词算进去()
    {
        var cache = new LyricsCache(_root);
        File.WriteAllText(Path.Combine(_root, "用户歌词.lrc"), "[00:01.00]用户的");

        var info = cache.GetInfo();

        Assert.Equal(0, info.FileCount);
    }

    [Theory]
    [InlineData(512, "512 字节")]
    [InlineData(2048, "2 KB")]
    [InlineData(3 * 1024 * 1024, "3 MB")]
    public void 大小显示是可读的(long bytes, string expected)
    {
        var info = new LyricsCacheInfo(1, bytes, "x");

        Assert.Equal(expected, info.SizeText);
    }
}
