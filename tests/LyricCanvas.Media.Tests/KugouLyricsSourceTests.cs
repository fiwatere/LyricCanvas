using System.Net;
using System.Text;
using System.Text.Json;
using LyricCanvas.Core;
using LyricCanvas.Media.Lyrics;

namespace LyricCanvas.Media.Tests;

public sealed class KugouLyricsSourceTests
{
    [Fact]
    public async Task Song里的连字符只在前缀等于Singer时拆分()
    {
        const string json = """
            {"candidates":[
              {"id":"1","accesskey":"a","song":"Mili - SAIKAI","singer":"Mili","duration":240000},
              {"id":"2","accesskey":"b","song":"Love - You","singer":"Artist","duration":180000},
              {"id":"3","accesskey":"c","song":"Only Song","singer":"Mili","duration":150000},
              {"id":"4","accesskey":"d","song":"Creator - Track - Remix","duration":200000}
            ]}
            """;
        using var client = CreateClient(json);
        var source = new KugouLyricsSource(client);

        var results = await source.SearchAsync(new LyricsQuery("SAIKAI", "Mili", string.Empty, TimeSpan.FromMinutes(4)));

        Assert.Equal(4, results.Count);
        Assert.Equal("SAIKAI", results[0].Title);
        Assert.Equal("Mili", results[0].Artist);
        Assert.Equal("Love - You", results[1].Title);
        Assert.Equal("Artist", results[1].Artist);
        Assert.Equal("Only Song", results[2].Title);
        Assert.Equal("Mili", results[2].Artist);
        Assert.Equal("Creator - Track - Remix", results[3].Title);
        Assert.Equal(string.Empty, results[3].Artist);
    }

    [Fact]
    public async Task 搜索响应不是Json时向解析器报告Json异常()
    {
        using var client = CreateClient("<html>service error</html>");
        var source = new KugouLyricsSource(client);

        await Assert.ThrowsAsync<JsonException>(() => source.SearchAsync(
            new LyricsQuery("Song", "Artist", string.Empty, TimeSpan.FromMinutes(3))));
    }

    [Fact]
    public async Task 下载响应不是Json时向解析器报告Json异常()
    {
        using var client = CreateClient("<html>service error</html>");
        var source = new KugouLyricsSource(client);
        var candidate = new LyricCandidate(
            "kugou", "Song", "Artist", string.Empty, TimeSpan.FromMinutes(3), string.Empty, "1", "key");

        await Assert.ThrowsAsync<JsonException>(() => source.FetchLyricsAsync(candidate));
    }

    private static HttpClient CreateClient(string responseBody)
        => new(new StubHandler(responseBody));

    private sealed class StubHandler(string responseBody) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
            });
        }
    }
}
