using System.Net;
using System.Text;
using ABC.Converter.Infrastructure.ComicVine;
using Xunit;

namespace ABC.Converter.Tests.ComicVine;

public sealed class ComicVineClientTests
{
    [Fact]
    public async Task SearchUsesRequiredAuthenticationAndUserAgent()
    {
        const string json = """
            {"status_code":1,"error":"OK","results":[
              {"id":101,"name":"Batman","issue_number":"1",
               "volume":{"id":10,"name":"Batman","start_year":"2016"}}
            ]}
            """;
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new ComicVineClient(
            http,
            "secret key",
            "ABC-Converter/0.1 (contact: support@example.invalid)");

        IReadOnlyList<ComicVineIssue> results = await client.SearchIssuesAsync(
            "Batman",
            "1",
            CancellationToken.None);

        Assert.Single(results);
        Assert.Equal(101, results[0].Id);
        Assert.Contains("api_key=secret%20key", handler.RequestUri!.Query);
        Assert.Contains("format=json", handler.RequestUri.Query);
        Assert.Equal(
            "ABC-Converter/0.1 (contact: support@example.invalid)",
            handler.UserAgent);
    }

    [Fact]
    public async Task ApiFailureIsTypedAndNeverExposesTheApiKey()
    {
        const string key = "do-not-leak-this";
        var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent(
                    """{"status_code":107,"error":"Rate limit exceeded","results":[]}""",
                    Encoding.UTF8,
                    "application/json"),
            });
        using var http = new HttpClient(handler);
        var client = new ComicVineClient(http, key, "ABC-Converter tests");

        ComicVineApiException exception = await Assert.ThrowsAsync<ComicVineApiException>(
            () => client.SearchIssuesAsync("Batman", "1", CancellationToken.None));

        Assert.Equal(ComicVineFailureKind.RateLimited, exception.Kind);
        Assert.DoesNotContain(key, exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(key, exception.ToString(), StringComparison.Ordinal);
    }

    private sealed class RecordingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? UserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            UserAgent = request.Headers.UserAgent.ToString();
            return Task.FromResult(response);
        }
    }
}

public sealed class ComicVineCacheTests
{
    [Fact]
    public async Task ReturnsCachedValueBeforeExpiryAndRefreshesAfterExpiry()
    {
        DateTimeOffset now = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
        var cache = new MemoryComicVineCache(
            TimeSpan.FromHours(1),
            () => now);
        int loads = 0;

        async Task<IReadOnlyList<ComicVineIssue>> Load(CancellationToken cancellationToken)
        {
            await Task.Yield();
            loads++;
            return [new ComicVineIssue(loads, "Batman", "1", "Batman", 2016)];
        }

        IReadOnlyList<ComicVineIssue> first = await cache.GetOrCreateAsync(
            "batman|1",
            Load,
            CancellationToken.None);
        IReadOnlyList<ComicVineIssue> second = await cache.GetOrCreateAsync(
            "batman|1",
            Load,
            CancellationToken.None);
        now = now.AddHours(2);
        IReadOnlyList<ComicVineIssue> refreshed = await cache.GetOrCreateAsync(
            "batman|1",
            Load,
            CancellationToken.None);

        Assert.Same(first, second);
        Assert.Equal(2, loads);
        Assert.Equal(2, refreshed[0].Id);
    }
}

public sealed class ComicVineMatcherTests
{
    [Fact]
    public void SelectsAUniqueExactSeriesIssueAndYearMatch()
    {
        var candidates = new[]
        {
            new ComicVineIssue(1, "Batman", "1", "Batman", 2016),
            new ComicVineIssue(2, "Batman Annual", "1", "Batman Annual", 2016),
        };

        ComicVineMatch result = ComicVineMatcher.Match(
            "Batman",
            "1",
            2016,
            candidates);

        Assert.Equal(ComicVineMatchStatus.Matched, result.Status);
        Assert.Equal(1, result.Issue!.Id);
    }

    [Fact]
    public void NeverSilentlyAcceptsTiedCandidates()
    {
        var candidates = new[]
        {
            new ComicVineIssue(1, "Batman", "1", "Batman", 2016),
            new ComicVineIssue(2, "Batman", "1", "Batman", 2016),
        };

        ComicVineMatch result = ComicVineMatcher.Match(
            "Batman",
            "1",
            2016,
            candidates);

        Assert.Equal(ComicVineMatchStatus.Ambiguous, result.Status);
        Assert.Null(result.Issue);
        Assert.Equal(2, result.Candidates.Count);
    }
}
