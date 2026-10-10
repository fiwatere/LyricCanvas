using System.Net;
using System.Text;
using LyricCanvas.Core;
using LyricCanvas.Media.Lyrics;

namespace LyricCanvas.Media.Tests;

public sealed class LrclibLyricsSourceTests
{
    private static readonly LyricsQuery Query = new("Song", "Artist", "Album", TimeSpan.FromMinutes(3));
    private const string RecordJson = """
        {"id": 1, "trackName": "Song", "artistName": "Artist", "duration": 180,
         "syncedLyrics": "[00:01.00]First line"}
        """;

    [Fact]
    public async Task ExactMatchDisposesResponseContent()
    {
        var content = new TrackedContent(RecordJson);
        using var handler = new StubHandler((_, _) => Task.FromResult(Response(HttpStatusCode.OK, content)));
        using var client = new HttpClient(handler);
        var source = new LrclibLyricsSource(client);

        var results = await source.SearchAsync(Query);

        Assert.Equal("Song", Assert.Single(results).Title);
        Assert.True(content.IsDisposed);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedExactMatchAndSuccessfulFallbackDisposeBothResponses(HttpStatusCode exactStatus)
    {
        var exactContent = new TrackedContent("{}");
        var fallbackContent = new TrackedContent("[" + RecordJson + "]");
        using var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal)
                ? Response(exactStatus, exactContent)
                : Response(HttpStatusCode.OK, fallbackContent)));
        using var client = new HttpClient(handler);
        var source = new LrclibLyricsSource(client);

        var results = await source.SearchAsync(Query);

        Assert.Single(results);
        Assert.True(exactContent.IsDisposed);
        Assert.True(fallbackContent.IsDisposed);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task FailedFallbackDisposesResponseContent()
    {
        var exactContent = new TrackedContent("{}");
        var fallbackContent = new TrackedContent("service unavailable");
        using var handler = new StubHandler((request, _) => Task.FromResult(
            request.RequestUri!.AbsolutePath.EndsWith("/get", StringComparison.Ordinal)
                ? Response(HttpStatusCode.NotFound, exactContent)
                : Response(HttpStatusCode.ServiceUnavailable, fallbackContent)));
        using var client = new HttpClient(handler);
        var source = new LrclibLyricsSource(client);

        var results = await source.SearchAsync(Query);

        Assert.Empty(results);
        Assert.True(exactContent.IsDisposed);
        Assert.True(fallbackContent.IsDisposed);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task CallerCancellationStopsBeforeFallback()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new StubHandler((request, token) =>
        {
            Assert.EndsWith("/get", request.RequestUri!.AbsolutePath);
            cancellation.Cancel();
            return Task.FromCanceled<HttpResponseMessage>(token);
        });
        using var client = new HttpClient(handler);
        var source = new LrclibLyricsSource(client);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.SearchAsync(Query, cancellation.Token));

        Assert.Equal(1, handler.RequestCount);
    }

    private static HttpResponseMessage Response(HttpStatusCode status, HttpContent content)
        => new(status) { Content = content };

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            return respond(request, cancellationToken);
        }
    }

    private sealed class TrackedContent(string json) : StringContent(json, Encoding.UTF8, "application/json")
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed |= disposing;
            base.Dispose(disposing);
        }
    }
}
