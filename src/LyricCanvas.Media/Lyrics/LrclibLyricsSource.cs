using System.Net;
using System.Text;
using System.Text.Json.Serialization;
using LyricCanvas.Core;

namespace LyricCanvas.Media.Lyrics;

/// <summary>
/// LRCLIB 的歌词接口（https://lrclib.net）。
///
/// 为什么把它当第二来源：它是**开放共建的免费服务**（MIT 开源，2000+ 星），
/// 接口就是给第三方程序用的，没有任何鉴权、签名或 Cookie。相比调用国内平台
/// 的未公开接口，这一条是明确被欢迎的用法。
///
/// 实测注意点：
///   - <c>/api/get</c> 是精确匹配（曲名+歌手+专辑+时长），**匹配不上直接 404**，
///     所以必须回退到 <c>/api/search</c>，否则会漏掉大量本来能找到的歌。
///   - <c>/api/search</c> 返回的 JSON 是 **UTF-8**，中文/日文必须显式按 UTF-8 解码，
///     用默认编码读会得到乱码（实测踩到过）。
/// </summary>
public sealed class LrclibLyricsSource : ILyricsSource
{
    private const string BaseUrl = "https://lrclib.net/api";
    // 歌词来源随换歌重新创建，默认客户端共用连接池；注入的客户端仍由调用方管理。
    private static readonly HttpClient DefaultHttpClient = CreateDefaultClient();

    private readonly HttpClient _http;

    public LrclibLyricsSource(HttpClient? httpClient = null)
    {
        _http = httpClient ?? DefaultHttpClient;
    }

    public string Name => "lrclib";

    public static HttpClient CreateDefaultClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        // LRCLIB 建议第三方客户端带上能识别自己的 User-Agent
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "LyricCanvas/0.6 (https://github.com/; desktop lyrics tool)");

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

        // 先试精确匹配（快、准），404 或没有结果时再走搜索
        var exact = await TryGetExactAsync(query, cancellationToken).ConfigureAwait(false);
        if (exact is not null)
        {
            return new[] { exact };
        }

        return await SearchFallbackAsync(query, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>精确匹配一次。拿不到返回 null。</summary>
    private async Task<LyricCandidate?> TryGetExactAsync(LyricsQuery query, CancellationToken cancellationToken)
    {
        try
        {
            var url = $"{BaseUrl}/get" +
                      $"?artist_name={Uri.EscapeDataString(query.Artist)}" +
                      $"&track_name={Uri.EscapeDataString(query.Title)}";

            if (!string.IsNullOrWhiteSpace(query.AlbumTitle))
            {
                url += $"&album_name={Uri.EscapeDataString(query.AlbumTitle)}";
            }

            if (query.Duration > TimeSpan.Zero)
            {
                url += $"&duration={(int)query.Duration.TotalSeconds}";
            }

            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);

            // 匹配不上时接口返回 404，这是正常情况而不是错误
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var record = await ReadJsonAsync<LrclibRecord>(response, cancellationToken).ConfigureAwait(false);
            return record is null ? null : ToCandidate(record);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>模糊搜索，返回多个候选。</summary>
    private async Task<IReadOnlyList<LyricCandidate>> SearchFallbackAsync(
        LyricsQuery query,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/search" +
                  $"?artist_name={Uri.EscapeDataString(query.Artist)}" +
                  $"&track_name={Uri.EscapeDataString(query.Title)}";

        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return Array.Empty<LyricCandidate>();
        }

        var records = await ReadJsonAsync<List<LrclibRecord>>(response, cancellationToken).ConfigureAwait(false);
        if (records is null || records.Count == 0)
        {
            return Array.Empty<LyricCandidate>();
        }

        var candidates = new List<LyricCandidate>(records.Count);

        foreach (var record in records)
        {
            var candidate = ToCandidate(record);
            if (candidate is not null)
            {
                candidates.Add(candidate);
            }
        }

        return candidates;
    }

    /// <summary>
    /// 显式按 UTF-8 读 JSON。
    ///
    /// 不能用 GetFromJsonAsync 的默认路径图省事：实测中文/日文会变乱码。
    /// 这里先把响应体当字节读出来，再用 UTF-8 解码，最后反序列化。
    /// </summary>
    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var json = Encoding.UTF8.GetString(bytes);

        if (string.IsNullOrWhiteSpace(json))
        {
            return default;
        }

        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, JsonOptions);
        }
        catch (System.Text.Json.JsonException)
        {
            return default;
        }
    }

    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private LyricCandidate? ToCandidate(LrclibRecord record)
    {
        if (string.IsNullOrWhiteSpace(record.TrackName))
        {
            return null;
        }

        // syncedLyrics 是带时间戳的标准 LRC；plainLyrics 只有纯文本。
        // 我们只需要同步歌词——纯文本没法跟着进度走，显示出来没有意义。
        var lyrics = record.SyncedLyrics;
        if (string.IsNullOrWhiteSpace(lyrics))
        {
            return null;
        }

        return new LyricCandidate(
            Source: Name,
            Title: record.TrackName,
            Artist: record.ArtistName ?? string.Empty,
            AlbumTitle: record.AlbumName ?? string.Empty,
            Duration: TimeSpan.FromSeconds(record.Duration),
            Lyrics: lyrics,
            SourceId: record.Id > 0 ? record.Id.ToString() : null);
    }

    public Task<string?> FetchLyricsAsync(LyricCandidate candidate, CancellationToken cancellationToken = default)
    {
        // LRCLIB 是单步式：搜索结果里就带着歌词正文了
        return Task.FromResult<string?>(candidate.HasLyrics ? candidate.Lyrics : null);
    }

    private sealed record LrclibRecord
    {
        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("trackName")]
        public string? TrackName { get; init; }

        [JsonPropertyName("artistName")]
        public string? ArtistName { get; init; }

        [JsonPropertyName("albumName")]
        public string? AlbumName { get; init; }

        [JsonPropertyName("duration")]
        public double Duration { get; init; }

        [JsonPropertyName("instrumental")]
        public bool Instrumental { get; init; }

        [JsonPropertyName("syncedLyrics")]
        public string? SyncedLyrics { get; init; }

        [JsonPropertyName("plainLyrics")]
        public string? PlainLyrics { get; init; }
    }
}
