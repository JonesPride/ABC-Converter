using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.ComicVine;
using ABC.Converter.Infrastructure.Metadata;
using Xunit;

namespace ABC.Converter.Tests.ComicVine;

public sealed class ComicVineMetadataResolverTests
{
    [Fact]
    public async Task UniqueMatchEnrichesOfflineMetadata()
    {
        var offline = new ComicMetadata("Fallback title", "Batman", "1", PublicationYear: 2016);
        var resolver = new ComicVineMetadataResolver(
            new FixedResolver(offline),
            new FixedLookup([
                new ComicVineIssue(101, "I Am Gotham, Part One", "1", "Batman", 2016),
            ]));

        ComicMetadata result = await resolver.ResolveAsync(
            "Batman 001.cbz",
            new ComicMetadata("Batman 001", "Batman"),
            CancellationToken.None);

        Assert.Equal("I Am Gotham, Part One", result.Title);
        Assert.Equal("Batman", result.Series);
        Assert.Equal("1", result.IssueNumber);
        Assert.Equal(2016, result.PublicationYear);
    }

    [Fact]
    public async Task NoMatchPreservesOfflineMetadata()
    {
        var offline = new ComicMetadata("Local title", "Unknown series", "7");
        var resolver = new ComicVineMetadataResolver(
            new FixedResolver(offline),
            new FixedLookup([]));

        ComicMetadata result = await resolver.ResolveAsync(
            "unknown.cbz",
            new ComicMetadata("unknown", "unknown"),
            CancellationToken.None);

        Assert.Same(offline, result);
    }

    [Fact]
    public async Task AmbiguousMatchRequiresReviewInsteadOfGuessing()
    {
        var offline = new ComicMetadata("Batman 1", "Batman", "1", PublicationYear: 2016);
        ComicVineIssue[] candidates =
        [
            new(101, "Batman", "1", "Batman", 2016),
            new(102, "Batman", "1", "Batman", 2016),
        ];
        var resolver = new ComicVineMetadataResolver(
            new FixedResolver(offline),
            new FixedLookup(candidates));

        MetadataReviewRequiredException exception =
            await Assert.ThrowsAsync<MetadataReviewRequiredException>(
                () => resolver.ResolveAsync(
                    "Batman 001.cbz",
                    offline,
                    CancellationToken.None));

        Assert.Same(offline, exception.OfflineMetadata);
        Assert.Equal([101L, 102L], exception.Candidates.Select(item => item.Id));
    }

    [Fact]
    public async Task DisabledLookupNeverCallsComicVine()
    {
        var offline = new ComicMetadata("Local title", "Batman", "1");
        var lookup = new RecordingLookup();
        var resolver = new ComicVineMetadataResolver(
            new FixedResolver(offline),
            lookup,
            () => false);

        ComicMetadata result = await resolver.ResolveAsync(
            "Batman.cbz",
            offline,
            CancellationToken.None);

        Assert.Same(offline, result);
        Assert.Equal(0, lookup.CallCount);
    }

    private sealed class FixedResolver(ComicMetadata result) : IComicMetadataResolver
    {
        public Task<ComicMetadata> ResolveAsync(
            string sourcePath,
            ComicMetadata defaults,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class FixedLookup(IReadOnlyList<ComicVineIssue> results) :
        IComicVineIssueLookup
    {
        public Task<IReadOnlyList<ComicVineIssue>> SearchAsync(
            ComicMetadata metadata,
            CancellationToken cancellationToken) => Task.FromResult(results);
    }

    private sealed class RecordingLookup : IComicVineIssueLookup
    {
        public int CallCount { get; private set; }

        public Task<IReadOnlyList<ComicVineIssue>> SearchAsync(
            ComicMetadata metadata,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult<IReadOnlyList<ComicVineIssue>>([]);
        }
    }
}
