using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using LyricCanvas.Core;

namespace LyricCanvas.Media.Lyrics;

/// <summary>
/// 酷狗音乐的歌词接口。
///
/// 两步式：
///   1. <c>lyrics.kugou.com/search</c> 拿候选（含 id 和 accesskey）
///   2. <c>lyrics.kugou.com/download</c> 用 id + accesskey 取歌词正文
///
/// 关键点（都是实测踩出来的）：
///   - <c>keyword</c> **必须是"歌手-歌名"**，用半角连字符。只给歌名会返回空列表。
///   - 请求 <c>fmt=lrc</c> 时接口**直接返回标准 LRC 的 Base64**，完全不需要解密
///     （请求 <c>fmt=krc</c> 才会给加密格式，那条路我们不走）。
///   - 返回的 Base64 要按 UTF-8 解码，否则中文会乱码。
/// </summary>
public sealed class KugouLyricsSource : ILyricsSource
{
    private const string SearchEndpoint = "https://lyrics.kugou.com/search";
    private const string DownloadEndpoint = "https://lyrics.kugou.com/download";
    // 歌词来源随换歌重新创建，默认客户端共用连接池；注入的客户端仍由调用方管理。
    private static readonly HttpClient DefaultHttpClient = CreateDefaultClient();

    private readonly HttpClient _http;

    public KugouLyricsSource(HttpClient? httpClient = null)
    {
        _http = httpClient ?? DefaultHttpClient;
    }

    public string Name => "kugou";

    /// <summary>建一个带必要请求头的默认客户端。</summary>
    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // 不带 User-Agent 有些接口会直接拒绝；Referer 也一并给上，尽量贴近正常客户端
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) LyricCanvas/0.6");
        client.DefaultRequestHeaders.Referrer = new Uri("https://www.kugou.com/");

        return client;
    }

    public async Task<IReadOnlyList<LyricCandidate>> SearchAsync(
        LyricsQuery query,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsUsable)
        {
            return Array.Empty<LyricCandidate>();
        }

        // keyword 必须是 "歌手-歌名"。歌手缺失时只能用歌名碰运气。
        var keyword = string.IsNullOrWhiteSpace(query.Artist)
            ? query.Title
            : $"{query.Artist}-{query.Title}";

        var durationMs = query.Duration > TimeSpan.Zero
            ? (long)query.Duration.TotalMilliseconds
            : 0;

        var url = $"{SearchEndpoint}?ver=1&man=yes&client=pc" +
                  $"&keyword={Uri.EscapeDataString(keyword)}" +
                  $"&duration={durationMs}&hash=";

        var response = await _http.GetFromJsonAsync<KugouSearchResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        if (response?.Candidates is null || response.Candidates.Count == 0)
        {
            return Array.Empty<LyricCandidate>();
        }

        var candidates = new List<LyricCandidate>(response.Candidates.Count);

        foreach (var item in response.Candidates)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.AccessKey))
            {
                continue;
            }

            // 酷狗还会单独给 Singer。只有 Song 左侧明确等于 Singer 时才剥离该前缀；
            // 否则把整个 Song 保留为曲名，避免把纯曲名里的连字符误当歌手分隔符。
            var (title, artist) = SplitSongField(item.Song, item.Singer);

            candidates.Add(new LyricCandidate(
                Source: Name,
                Title: title,
                Artist: artist,
                AlbumTitle: item.Album ?? string.Empty,
                Duration: TimeSpan.FromMilliseconds(item.Duration > 0 ? item.Duration : durationMs),
                Lyrics: string.Empty,
                SourceId: item.Id,
                SourceKey: item.AccessKey));
        }

        return candidates;
    }

    public async Task<string?> FetchLyricsAsync(
        LyricCandidate candidate,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(candidate.SourceId) || string.IsNullOrWhiteSpace(candidate.SourceKey))
        {
            return null;
        }

        var url = $"{DownloadEndpoint}?ver=1&client=pc" +
                  $"&id={Uri.EscapeDataString(candidate.SourceId)}" +
                  $"&accesskey={Uri.EscapeDataString(candidate.SourceKey)}" +
                  "&fmt=lrc&charset=utf8";

        var response = await _http.GetFromJsonAsync<KugouDownloadResponse>(url, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(response?.Content))
        {
            return null;
        }

        try
        {
            // 返回的是 Base64 编码的 UTF-8 文本
            var bytes = Convert.FromBase64String(response.Content);
            return Encoding.UTF8.GetString(bytes);
        }
        catch (FormatException)
        {
            // 不是合法 Base64，说明接口返回的东西变了
            return null;
        }
    }

    /// <summary>
    /// 把酷狗的 "Song" 字段拆成曲名和歌手。只接受与独立 Singer 字段一致的前缀。
    /// </summary>
    private static (string Title, string Artist) SplitSongField(string? song, string? fallbackSinger)
    {
        var singer = string.IsNullOrWhiteSpace(fallbackSinger) ? string.Empty : fallbackSinger.Trim();

        if (string.IsNullOrWhiteSpace(song))
        {
            return (string.Empty, singer);
        }

        var text = song.Trim();
        var separator = text.IndexOf(" - ", StringComparison.Ordinal);

        if (separator <= 0 || singer.Length == 0)
        {
            return (text, singer);
        }

        var left = text[..separator].Trim();
        var right = text[(separator + 3)..].Trim();

        if (left.Length > 0
            && right.Length > 0
            && string.Equals(
                LyricsText.Normalize(left),
                LyricsText.Normalize(singer),
                StringComparison.Ordinal))
        {
            return (right, singer);
        }

        return (text, singer);
    }

    // ---------------------------------------------------------------
    // 接口返回的 JSON 结构
    // ---------------------------------------------------------------

    private sealed record KugouSearchResponse
    {
        [JsonPropertyName("candidates")]
        public List<KugouCandidate>? Candidates { get; init; }
    }

    private sealed record KugouCandidate
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("accesskey")]
        public string? AccessKey { get; init; }

        [JsonPropertyName("song")]
        public string? Song { get; init; }

        [JsonPropertyName("singer")]
        public string? Singer { get; init; }

        [JsonPropertyName("album")]
        public string? Album { get; init; }

        [JsonPropertyName("duration")]
        public long Duration { get; init; }
    }

    private sealed record KugouDownloadResponse
    {
        [JsonPropertyName("content")]
        public string? Content { get; init; }

        [JsonPropertyName("fmt")]
        public string? Format { get; init; }
    }
}
