using LyricCanvas.Core;

namespace LyricCanvas.Core.Tests;

/// <summary>
/// 歌词解析流程的单元测试。
///
/// 用假的歌词来源（不联网）验证整条链路：本地文件 → 缓存 → 联网 → 写入缓存。
/// 重点是"优先级顺序"和"单个来源失败不影响整体"。
/// </summary>
public sealed class LyricsResolverTests : IDisposable
{
    private readonly string _root;

    public LyricsResolverTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "lyricdesktop-resolver-tests", Guid.NewGuid().ToString("N"));
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

    // ---------------------------------------------------------------
    // 测试用的假来源
    // ---------------------------------------------------------------

    private sealed class FakeSource : ILyricsSource
    {
        private readonly LyricCandidate[] _candidates;
        private readonly string? _lyrics;
        private readonly Exception? _throws;
        private readonly Exception? _fetchThrows;
        private readonly Action? _afterFetch;

        public FakeSource(
            string name,
            LyricCandidate[]? candidates = null,
            string? lyrics = null,
            Exception? throws = null,
            Exception? fetchThrows = null,
            Action? afterFetch = null)
        {
            Name = name;
            _candidates = candidates ?? Array.Empty<LyricCandidate>();
            _lyrics = lyrics;
            _throws = throws;
            _fetchThrows = fetchThrows;
            _afterFetch = afterFetch;
        }

        public string Name { get; }

        public int SearchCallCount { get; private set; }

        public int FetchCallCount { get; private set; }

        public Task<IReadOnlyList<LyricCandidate>> SearchAsync(LyricsQuery query, CancellationToken cancellationToken = default)
        {
            SearchCallCount++;
            if (_throws is not null)
            {
                throw _throws;
            }

            return Task.FromResult<IReadOnlyList<LyricCandidate>>(_candidates);
        }

        public Task<string?> FetchLyricsAsync(LyricCandidate candidate, CancellationToken cancellationToken = default)
        {
            FetchCallCount++;
            if (_fetchThrows is not null)
            {
                throw _fetchThrows;
            }

            _afterFetch?.Invoke();
            return Task.FromResult<string?>(_lyrics ?? candidate.Lyrics);
        }
    }

    private static LyricCandidate Candidate(
        string title,
        string artist = "",
        int durationSeconds = 240,
        string? lyrics = "[00:01.00]网络抓到的歌词",
        string source = "fake",
        string? sourceId = "id-1",
        string? sourceKey = "key-1")
        => new(source, title, artist, string.Empty, TimeSpan.FromSeconds(durationSeconds),
               lyrics ?? string.Empty, sourceId, sourceKey);

    private LyricsResolver CreateResolver(params ILyricsSource[] sources)
        => CreateResolver(network: true, cache: true, sources);

    private LyricsResolver CreateResolver(bool network, bool cache, params ILyricsSource[] sources)
        => new(() => new LyricsOptions
        {
            LyricsDirectory = _root,
            FetchFromNetwork = network,
            UseCache = cache,
            Sources = sources,
            Timeout = TimeSpan.FromSeconds(3),
        });

    // ---------------------------------------------------------------
    // 优先级
    // ---------------------------------------------------------------

    [Fact]
    public async Task 优先使用用户自己放的歌词文件()
    {
        File.WriteAllText(Path.Combine(_root, "测试歌曲.lrc"), "[00:01.00]用户自己放的");

        var source = new FakeSource("fake", new[] { Candidate("测试歌曲") });
        var result = await CreateResolver(source).ResolveAsync("测试歌曲", string.Empty, string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal(LyricsOrigin.UserFile, result.Origin);
        Assert.Equal("用户自己放的", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.SearchCallCount);
    }

    [Fact]
    public async Task 本地歌词按自定义程序工作目录顺序回退查找()
    {
        var customDirectory = Path.Combine(_root, "custom");
        var appDirectory = Path.Combine(_root, "app", "lyrics");
        var workingDirectory = Path.Combine(_root, "working", "lyrics");
        Directory.CreateDirectory(customDirectory);
        Directory.CreateDirectory(appDirectory);
        Directory.CreateDirectory(workingDirectory);

        var searchDirectories = LyricsDirectoryPolicy.GetSearchDirectories(
            customDirectory,
            Path.Combine(_root, "app"),
            Path.Combine(_root, "working"));
        File.WriteAllText(Path.Combine(appDirectory, "多目录歌曲.lrc"), "[00:01.00]程序目录歌词");
        File.WriteAllText(Path.Combine(workingDirectory, "多目录歌曲.lrc"), "[00:01.00]工作目录歌词");

        var resolver = new LyricsResolver(() => new LyricsOptions
        {
            LyricsDirectory = searchDirectories[0],
            SearchDirectories = searchDirectories,
            FetchFromNetwork = false,
            UseCache = false,
        });

        var fallbackResult = await resolver.ResolveAsync(
            "多目录歌曲", string.Empty, string.Empty, TimeSpan.Zero);
        Assert.Equal("程序目录歌词", fallbackResult.Document.GetTextAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(Path.Combine(appDirectory, "多目录歌曲.lrc"), fallbackResult.FilePath);

        File.WriteAllText(Path.Combine(customDirectory, "多目录歌曲.lrc"), "[00:01.00]自定义目录歌词");
        var customResult = await resolver.ResolveAsync(
            "多目录歌曲", string.Empty, string.Empty, TimeSpan.Zero);
        Assert.Equal("自定义目录歌词", customResult.Document.GetTextAt(TimeSpan.FromSeconds(1)));

        File.Delete(Path.Combine(appDirectory, "多目录歌曲.lrc"));
        var workingResult = await resolver.ResolveAsync(
            "多目录歌曲", string.Empty, string.Empty, TimeSpan.Zero);
        Assert.Equal("自定义目录歌词", workingResult.Document.GetTextAt(TimeSpan.FromSeconds(1)));
        File.Delete(Path.Combine(customDirectory, "多目录歌曲.lrc"));
        var finalFallback = await resolver.ResolveAsync(
            "多目录歌曲", string.Empty, string.Empty, TimeSpan.Zero);
        Assert.Equal("工作目录歌词", finalFallback.Document.GetTextAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 用户文件比缓存优先()
    {
        var cache = new LyricsCache(_root);
        cache.Write("测试歌曲", string.Empty, "[00:01.00]缓存里的");
        File.WriteAllText(Path.Combine(_root, "测试歌曲.lrc"), "[00:01.00]用户自己放的");

        var result = await CreateResolver().ResolveAsync("测试歌曲", string.Empty, string.Empty, TimeSpan.FromSeconds(240));

        Assert.Equal(LyricsOrigin.UserFile, result.Origin);
        Assert.Equal("用户自己放的", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 次级优先使用缓存不联网()
    {
        var cache = new LyricsCache(_root);
        cache.Write("测试歌曲", "某歌手", "[00:01.00]缓存里的歌词");

        var source = new FakeSource("fake", new[] { Candidate("测试歌曲", "某歌手") });
        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.Equal(LyricsOrigin.Cache, result.Origin);
        Assert.Equal("缓存里的歌词", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
        Assert.Equal(0, source.SearchCallCount);
    }

    [Fact]
    public async Task 都没有时联网抓取并写入缓存()
    {
        var source = new FakeSource("fake", new[] { Candidate("测试歌曲", "某歌手") });
        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal(LyricsOrigin.Network, result.Origin);
        Assert.Equal("fake", result.SourceName);
        Assert.Equal(1, source.SearchCallCount);

        // 关键：应该已经写进缓存了
        Assert.True(new LyricsCache(_root).Exists("测试歌曲", "某歌手"));

        // 再解析一次应该直接命中缓存，不再联网
        var second = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));
        Assert.Equal(LyricsOrigin.Cache, second.Origin);
        Assert.Equal(1, source.SearchCallCount);
    }

    // ---------------------------------------------------------------
    // 两步式来源（搜索结果里没有正文）
    // ---------------------------------------------------------------

    [Fact]
    public async Task 两步式来源会再请求一次取正文()
    {
        var source = new FakeSource(
            "two-step",
            new[] { Candidate("测试歌曲", "某歌手", lyrics: null) },
            lyrics: "[00:01.00]第二步取到的歌词");

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal(1, source.FetchCallCount);
        Assert.Equal("第二步取到的歌词", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 第一步就带正文时不会重复请求()
    {
        var source = new FakeSource("one-step", new[] { Candidate("测试歌曲", "某歌手", lyrics: "[00:01.00]直接就有") });

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal(0, source.FetchCallCount);
    }

    // ---------------------------------------------------------------
    // 容错
    // ---------------------------------------------------------------

    [Fact]
    public async Task 第一个来源抛异常时会尝试下一个()
    {
        var broken = new FakeSource("broken", throws: new HttpRequestException("网络炸了"));
        var working = new FakeSource("working", new[] { Candidate("测试歌曲", "某歌手") });

        var result = await CreateResolver(broken, working).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal("working", result.SourceName);
        Assert.Equal(1, broken.SearchCallCount);
        Assert.Equal(1, working.SearchCallCount);
    }

    [Fact]
    public async Task 搜索返回坏Json时继续尝试下一个来源()
    {
        var broken = new FakeSource("broken", throws: new System.Text.Json.JsonException("bad json"));
        var working = new FakeSource("working", new[] { Candidate("测试歌曲", "某歌手") });

        var result = await CreateResolver(broken, working).ResolveAsync(
            "测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal("working", result.SourceName);
    }

    [Fact]
    public async Task 下载返回坏Json时继续尝试下一个来源()
    {
        var broken = new FakeSource(
            "broken",
            new[] { Candidate("测试歌曲", "某歌手", lyrics: null) },
            fetchThrows: new System.Text.Json.JsonException("bad json"));
        var working = new FakeSource("working", new[] { Candidate("测试歌曲", "某歌手") });

        var result = await CreateResolver(broken, working).ResolveAsync(
            "测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal("working", result.SourceName);
    }

    [Fact]
    public async Task 所有来源都失败时返回未找到而不是抛异常()
    {
        var a = new FakeSource("a", throws: new HttpRequestException("炸了"));
        var b = new FakeSource("b", throws: new IOException("也炸了"));

        var result = await CreateResolver(a, b).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.False(result.HasLyrics);
        Assert.Equal(LyricsOrigin.None, result.Origin);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
    }

    [Fact]
    public async Task 来源返回空候选时继续尝试下一个()
    {
        var empty = new FakeSource("empty", Array.Empty<LyricCandidate>());
        var working = new FakeSource("working", new[] { Candidate("测试歌曲", "某歌手") });

        var result = await CreateResolver(empty, working).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal("working", result.SourceName);
    }

    [Fact]
    public async Task 候选匹配不上时不会误用()
    {
        // 返回的是完全不相干的歌，宁可找不到也不能用
        var source = new FakeSource("fake", new[]
        {
            Candidate("完全不相干的歌", "别的歌手", 200, "[00:01.00]错歌词"),
        });

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.False(result.HasLyrics);
        Assert.False(new LyricsCache(_root).Exists("测试歌曲", "某歌手"));
    }

    [Fact]
    public async Task 返回的内容解析不出歌词时不会写进缓存()
    {
        // 接口返回了 HTML 错误页之类的东西
        var source = new FakeSource("fake", new[]
        {
            Candidate("测试歌曲", "某歌手", lyrics: "<html>502 Bad Gateway</html>"),
        });

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.False(result.HasLyrics);
        Assert.False(new LyricsCache(_root).Exists("测试歌曲", "某歌手"),
            "解析不出歌词的内容不该进缓存，否则以后每次都会命中这个坏缓存");
    }

    // ---------------------------------------------------------------
    // 配置开关
    // ---------------------------------------------------------------

    [Fact]
    public async Task 关掉联网后只认本地文件()
    {
        var source = new FakeSource("fake", new[] { Candidate("测试歌曲", "某歌手") });

        var result = await CreateResolver(network: false, cache: true, source)
            .ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.False(result.HasLyrics);
        Assert.Equal(0, source.SearchCallCount);
    }

    [Fact]
    public async Task 关掉联网但仍然用已有缓存()
    {
        new LyricsCache(_root).Write("测试歌曲", "某歌手", "[00:01.00]缓存里的");

        var result = await CreateResolver(network: false, cache: true)
            .ResolveAsync("测试歌曲", "某歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.Equal(LyricsOrigin.Cache, result.Origin);
    }

    [Fact]
    public async Task 曲名为空时直接返回未找到()
    {
        var source = new FakeSource("fake", new[] { Candidate("测试歌曲") });

        var result = await CreateResolver(source).ResolveAsync(string.Empty, "某歌手", string.Empty, TimeSpan.Zero);

        Assert.False(result.HasLyrics);
        Assert.Equal(0, source.SearchCallCount);
    }

    // ---------------------------------------------------------------
    // 歧义提示
    // ---------------------------------------------------------------

    [Fact]
    public async Task 匹配不确定时仍返回歌词但带上提示()
    {
        // 曲名吻合但歌手完全不同 —— 可能是翻唱，应该提示用户确认
        var source = new FakeSource("fake", new[] { Candidate("测试歌曲", "另一个歌手") });

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "原唱歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.True(result.NeedsConfirmation);
        Assert.False(string.IsNullOrWhiteSpace(result.Message));
        Assert.Equal("测试歌曲", result.MatchedTitle);
        Assert.Equal("另一个歌手", result.MatchedArtist);

        var cached = await CreateResolver().ResolveAsync(
            "测试歌曲", "原唱歌手", string.Empty, TimeSpan.FromSeconds(240));
        Assert.Equal(LyricsOrigin.Cache, cached.Origin);
        Assert.True(cached.NeedsConfirmation);
        Assert.False(cached.IsConfirmed);
        Assert.Equal(result.MatchedCandidateKey, cached.MatchedCandidateKey);

        Assert.True(new LyricsCache(_root).MarkConfirmed("测试歌曲", "原唱歌手"));
        var confirmed = await CreateResolver().ResolveAsync(
            "测试歌曲", "原唱歌手", string.Empty, TimeSpan.FromSeconds(240));
        Assert.False(confirmed.NeedsConfirmation);
        Assert.True(confirmed.IsConfirmed);
    }

    [Fact]
    public async Task 用户拒绝的候选可以从后续结果中排除()
    {
        var candidate = Candidate("测试歌曲", "某歌手");
        var source = new FakeSource("fake", new[] { candidate });
        var result = await CreateResolver(source).ResolveAsync(
            "测试歌曲",
            "某歌手",
            string.Empty,
            TimeSpan.FromSeconds(240),
            skipCache: true);

        var retry = await CreateResolver(source).ResolveAsync(
            "测试歌曲",
            "某歌手",
            string.Empty,
            TimeSpan.FromSeconds(240),
            excludedCandidateKeys: new HashSet<string> { result.MatchedCandidateKey },
            skipCache: true);

        Assert.True(result.HasLyrics);
        Assert.False(retry.HasLyrics);
        Assert.Equal(2, source.SearchCallCount);
    }

    [Fact]
    public async Task 匹配确定时不带提示()
    {
        var source = new FakeSource("fake", new[] { Candidate("测试歌曲", "原唱歌手") });

        var result = await CreateResolver(source).ResolveAsync("测试歌曲", "原唱歌手", string.Empty, TimeSpan.FromSeconds(240));

        Assert.True(result.HasLyrics);
        Assert.False(result.NeedsConfirmation);
    }

    // ---------------------------------------------------------------
    // 曲名文件名规则
    // ---------------------------------------------------------------

    [Fact]
    public async Task 带歌手的文件名优先于只要曲名的()
    {
        File.WriteAllText(Path.Combine(_root, "起风了.lrc"), "[00:01.00]通用版");
        File.WriteAllText(Path.Combine(_root, "起风了 - 买辣椒也用券.lrc"), "[00:01.00]指定歌手版");

        var result = await CreateResolver().ResolveAsync("起风了", "买辣椒也用券", string.Empty, TimeSpan.FromSeconds(325));

        Assert.Equal("指定歌手版", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 没有指定歌手的文件时退回只要曲名的()
    {
        File.WriteAllText(Path.Combine(_root, "起风了.lrc"), "[00:01.00]通用版");

        var result = await CreateResolver().ResolveAsync("起风了", "某个没放过歌词的歌手", string.Empty, TimeSpan.FromSeconds(325));

        Assert.True(result.HasLyrics);
        Assert.Equal("通用版", result.Document.GetTextAt(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task 换歌取消后即使来源晚返回也不会保存过期结果()
    {
        using var cancellation = new CancellationTokenSource();
        var source = new FakeSource("slow", new[] { Candidate("旧歌", lyrics: null) },
            lyrics: "[00:01.00]旧歌词", afterFetch: cancellation.Cancel);

        var result = await CreateResolver(source).ResolveAsync(
            "旧歌", string.Empty, string.Empty, TimeSpan.FromSeconds(240), cancellation.Token);

        Assert.Equal(1, source.FetchCallCount);
        Assert.False(result.HasLyrics);
        Assert.Equal("已取消", result.Message);
        Assert.False(new LyricsCache(_root).Exists("旧歌", string.Empty));
    }

    [Fact]
    public async Task 已取消的检索不会读取旧歌文件或请求网络()
    {
        File.WriteAllText(Path.Combine(_root, "旧歌.lrc"), "[00:01.00]旧歌词");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var source = new FakeSource("fake", new[] { Candidate("旧歌") });

        var result = await CreateResolver(source).ResolveAsync(
            "旧歌", string.Empty, string.Empty, TimeSpan.FromSeconds(240), cancellation.Token);

        Assert.False(result.HasLyrics);
        Assert.Equal("已取消", result.Message);
        Assert.Equal(0, source.SearchCallCount);
    }
}
