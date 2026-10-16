using System.Net;
using System.Text;
using ABC.Converter.Infrastructure.ComicVine;
using Xunit;

namespace ABC.Converter.Tests.ComicVine;

public sealed class ComicVineClientTests
{
    private const int ResponseLimit = 1024 * 1024;

    [Theory]
    [InlineData(null)]
    [InlineData(10L)]
    [InlineData(1048577L)]
    public async Task RejectsOversizedResponsesWithoutReadingBeyondTheLimit(long? advertisedLength)
    {
        using var stream = new TrackingStream(CreatePayload(ResponseLimit + 100));
        var content = new StreamContent(stream);
        content.Headers.ContentLength = advertisedLength;
        using var http = new HttpClient(new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = content }));
        var client = new ComicVineClient(http, "secret", "ABC-Converter tests");

        ComicVineApiException exception = await Assert.ThrowsAsync<ComicVineApiException>(
            () => client.SearchIssuesAsync("Batman", "1", CancellationToken.None));

        Assert.Equal(ComicVineFailureKind.InvalidResponse, exception.Kind);
        Assert.InRange(stream.BytesRead, 0, ResponseLimit + 1);
        Assert.True(stream.WasDisposed);
        if (advertisedLength > ResponseLimit)
        {
            Assert.Equal(0, stream.BytesRead);
        }
    }

    [Fact]
    public async Task AcceptsResponseExactlyAtTheLimit()
    {
        using var stream = new TrackingStream(CreatePayload(ResponseLimit));
        using var http = new HttpClient(new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(stream) }));
        var client = new ComicVineClient(http, "secret", "ABC-Converter tests");

        var results = await client.SearchIssuesAsync("Batman", "1", CancellationToken.None);

        Assert.Empty(results);
        Assert.Equal(ResponseLimit, stream.BytesRead);
    }

    [Fact]
    public async Task HttpFailuresDoNotReadTheResponseBody()
    {
        using var stream = new TrackingStream(CreatePayload(ResponseLimit + 100));
        var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StreamContent(stream),
        };
        response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(
            TimeSpan.FromMinutes(1));
        using var http = new HttpClient(new RecordingHandler(response));
        var client = new ComicVineClient(http, "secret", "ABC-Converter tests");

        var exception = await Assert.ThrowsAsync<ComicVineApiException>(
            () => client.SearchIssuesAsync("Batman", "1", CancellationToken.None));

        Assert.Equal(ComicVineFailureKind.RateLimited, exception.Kind);
        Assert.Equal(TimeSpan.FromMinutes(1), exception.RetryAfter);
        Assert.Equal(0, stream.BytesRead);
        Assert.True(stream.WasDisposed);
    }

    private static byte[] CreatePayload(int size)
    {
        const string json = """{"status_code":1,"results":[]}""";
        return Encoding.UTF8.GetBytes(json.PadRight(size));
    }

    private sealed class TrackingStream(byte[] data) : Stream
    {
        private readonly MemoryStream inner = new(data);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public int BytesRead { get; private set; }

        public bool WasDisposed { get; private set; }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            int count = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, 4096)], cancellationToken);
            BytesRead += count;
            return count;
        }

        protected override void Dispose(bool disposing)
        {
            WasDisposed = true;
            if (disposing)
            {
                inner.Dispose();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, Math.Min(count, 4096));
            BytesRead += read;
            return read;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

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
