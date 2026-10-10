namespace LyricCanvas.Core;

/// <summary>
/// 歌词的获取流程：本地文件 → 缓存 → 联网抓取 —— 取到就写缓存。
///
/// 这个顺序是刻意的：
///   1. **用户自己放的歌词优先级最高**。用户手动放了一份，说明他要么想改歌词，
///      要么那份比自动抓的准。程序永远不该覆盖或忽略它。
///   2. 缓存次之。命中缓存就不用发网络请求，同一首歌的歌词也保持稳定。
///   3. 最后才联网。抓到了写进缓存，下次直接命中第 2 步。
///
/// 界面只需要调 <see cref="ResolveAsync"/> 一个方法，不用关心这些细节。
/// </summary>
public sealed class LyricsResolver
{
    private readonly Func<LyricsOptions> _getOptions;

    /// <param name="getOptions">
    /// 每次解析时现取配置，而不是构造时取一次。
    /// 这样用户在设置里改了目录或开关，立刻生效，不用重启。
    /// </param>
    public LyricsResolver(Func<LyricsOptions> getOptions)
    {
        _getOptions = getOptions ?? throw new ArgumentNullException(nameof(getOptions));
    }

    /// <summary>
    /// 按当前配置解析一首歌的歌词。
    ///
    /// 任何来源出问题（网络超时、接口改版、返回垃圾数据）都只会让这一步失败，
    /// 不会往外抛异常——歌词拿不到不该让整个窗口崩掉。
    /// </summary>
    public async Task<LyricsResolution> ResolveAsync(
        string title,
        string artist,
        string albumTitle,
        TimeSpan duration,
        CancellationToken cancellationToken = default,
        IReadOnlySet<string>? excludedCandidateKeys = null,
        bool skipCache = false)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            return LyricsResolution.NotFound("没有曲名，无法查找歌词");
        }

        if (cancellationToken.IsCancellationRequested)
        {
            return LyricsResolution.NotFound("已取消");
        }

        var options = _getOptions();

        // ---------- 第 1 步：用户自己的文件 ----------
        var directories = options.SearchDirectories.Count == 0
            ? new[] { options.LyricsDirectory }
            : options.SearchDirectories.ToArray();
        var finder = new LrcFileFinder(directories);
        var userFile = finder.Find(title, artist);
        if (userFile is not null)
        {
            var document = LrcParser.ParseFile(userFile);
            if (!document.IsEmpty)
            {
                return new LyricsResolution(
                    document,
                    LyricsOrigin.UserFile,
                    SourceName: string.Empty,
                    FilePath: userFile,
                    NeedsConfirmation: false,
                    Message: string.Empty);
            }
        }

        var cache = new LyricsCache(options.LyricsDirectory);

        // ---------- 第 2 步：缓存 ----------
        if (options.UseCache && !skipCache)
        {
            var cached = cache.Read(title, artist);
            if (cached is not null)
            {
                var document = LrcParser.Parse(cached.LyricsText);
                if (!document.IsEmpty)
                {
                    return new LyricsResolution(
                        document,
                        LyricsOrigin.Cache,
                        SourceName: cached.SourceName,
                        FilePath: cached.FilePath,
                        NeedsConfirmation: cached.NeedsConfirmation,
                        Message: cached.NeedsConfirmation ? cached.Message : string.Empty)
                    {
                        MatchedCandidateKey = cached.MatchedCandidateKey,
                        MatchedTitle = cached.MatchedTitle,
                        MatchedArtist = cached.MatchedArtist,
                        IsConfirmed = cached.IsConfirmed,
                    };
                }
            }
        }

        if (!options.FetchFromNetwork || options.Sources.Count == 0)
        {
            return LyricsResolution.NotFound("未找到歌词文件");
        }

        // ---------- 第 3 步：联网 ----------
        // 整个检索过程套一层超时，避免某个接口卡住导致歌词永远出不来
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(options.Timeout);
        var token = timeoutSource.Token;

        var query = new LyricsQuery(title, artist, albumTitle, duration);

        foreach (var source in options.Sources)
        {
            if (token.IsCancellationRequested)
            {
                break;
            }

            try
            {
                var candidates = await source.SearchAsync(query, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (candidates is null || candidates.Count == 0)
                {
                    continue;
                }

                if (excludedCandidateKeys is { Count: > 0 })
                {
                    candidates = candidates
                        .Where(candidate => !excludedCandidateKeys.Contains(LyricsMatcher.GetCandidateKey(candidate)))
                        .ToArray();
                    if (candidates.Count == 0)
                    {
                        continue;
                    }
                }

                var match = LyricsMatcher.Match(title, artist, duration, candidates);
                if (!match.HasMatch || match.BestCandidate is null)
                {
                    continue;
                }

                var best = match.BestCandidate;

                // 两步式接口（例如酷狗）：搜索结果里没有歌词正文，要再请求一次
                var lyrics = best.HasLyrics
                    ? best.Lyrics
                    : await source.FetchLyricsAsync(best, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(lyrics))
                {
                    continue;
                }

                // 先用解析器验一遍：解析不出任何歌词行的内容不该写进缓存，
                // 否则以后每次都会命中这个坏缓存
                var document = LrcParser.Parse(lyrics);
                if (document.IsEmpty)
                {
                    continue;
                }

                var candidateKey = LyricsMatcher.GetCandidateKey(best);
                var message = match.Ambiguous ? "匹配结果可能不准确，请确认或尝试其他匹配" : string.Empty;
                var savedPath = cache.Write(
                    title,
                    artist,
                    lyrics,
                    source.Name,
                    needsConfirmation: match.Ambiguous,
                    message: message,
                    matchedCandidateKey: candidateKey,
                    matchedTitle: best.Title,
                    matchedArtist: best.Artist) ?? string.Empty;

                return new LyricsResolution(
                    document,
                    LyricsOrigin.Network,
                    source.Name,
                    savedPath,
                    match.Ambiguous,
                    message)
                {
                    MatchedCandidateKey = candidateKey,
                    MatchedTitle = best.Title,
                    MatchedArtist = best.Artist,
                };
            }
            catch (OperationCanceledException)
            {
                // 用户换歌了或整体超时，安静退出
                break;
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or FormatException
                or InvalidOperationException or System.Text.Json.JsonException or KeyNotFoundException)
            {
                // 单个来源失败就换下一个，不影响整体
                continue;
            }
        }

        return LyricsResolution.NotFound(
            cancellationToken.IsCancellationRequested ? "已取消" : "联网也没找到这首歌的歌词");
    }
}
