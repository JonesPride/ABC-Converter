using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.ComicVine;

namespace ABC.Converter.Infrastructure.Metadata;

public interface IComicVineIssueLookup
{
    Task<IReadOnlyList<ComicVineIssue>> SearchAsync(
        ComicMetadata metadata,
        CancellationToken cancellationToken);
}

public sealed class ComicVineIssueLookup : IComicVineIssueLookup
{
    private readonly IComicVineApiKeyStore apiKeyStore;
    private readonly HttpClient httpClient;
    private readonly MemoryComicVineCache cache;
    private readonly string userAgent;

    public ComicVineIssueLookup(
        IComicVineApiKeyStore apiKeyStore,
        HttpClient httpClient,
        string userAgent,
        MemoryComicVineCache? cache = null)
    {
        this.apiKeyStore = apiKeyStore ??
            throw new ArgumentNullException(nameof(apiKeyStore));
        this.httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        this.userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? throw new ArgumentException("A user agent is required.", nameof(userAgent))
            : userAgent.Trim();
        this.cache = cache ?? new MemoryComicVineCache(TimeSpan.FromHours(24));
    }

    public Task<IReadOnlyList<ComicVineIssue>> SearchAsync(
        ComicMetadata metadata,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        string? apiKey = apiKeyStore.Load();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Task.FromResult<IReadOnlyList<ComicVineIssue>>([]);
        }

        string key = string.Join(
            "|",
            metadata.Series.Trim().ToUpperInvariant(),
            metadata.IssueNumber?.Trim().ToUpperInvariant() ?? string.Empty);
        return cache.GetOrCreateAsync(
            key,
            token => new ComicVineClient(httpClient, apiKey, userAgent)
                .SearchIssuesAsync(metadata.Series, metadata.IssueNumber, token),
            cancellationToken);
    }
}

public sealed class MetadataReviewRequiredException : Exception
{
    public MetadataReviewRequiredException(
        ComicMetadata offlineMetadata,
        IReadOnlyList<ComicVineIssue> candidates)
        : base("ComicVine found multiple equally likely matches. Review the metadata before converting.")
    {
        OfflineMetadata = offlineMetadata ??
            throw new ArgumentNullException(nameof(offlineMetadata));
        Candidates = candidates ??
            throw new ArgumentNullException(nameof(candidates));
    }

    public ComicMetadata OfflineMetadata { get; }

    public IReadOnlyList<ComicVineIssue> Candidates { get; }
}

public sealed class ComicVineMetadataResolver : IComicMetadataResolver
{
    private readonly IComicMetadataResolver offlineResolver;
    private readonly IComicVineIssueLookup lookup;
    private readonly Func<bool> isEnabled;

    public ComicVineMetadataResolver(
        IComicMetadataResolver offlineResolver,
        IComicVineIssueLookup lookup,
        Func<bool>? isEnabled = null)
    {
        this.offlineResolver = offlineResolver ??
            throw new ArgumentNullException(nameof(offlineResolver));
        this.lookup = lookup ?? throw new ArgumentNullException(nameof(lookup));
        this.isEnabled = isEnabled ?? (() => true);
    }

    public async Task<ComicMetadata> ResolveAsync(
        string sourcePath,
        ComicMetadata defaults,
        CancellationToken cancellationToken)
    {
        ComicMetadata offline = await offlineResolver.ResolveAsync(
            sourcePath,
            defaults,
            cancellationToken);
        if (!isEnabled())
        {
            return offline;
        }

        IReadOnlyList<ComicVineIssue> candidates = await lookup.SearchAsync(
            offline,
            cancellationToken);
        ComicVineMatch match = ComicVineMatcher.Match(
            offline.Series,
            offline.IssueNumber,
            offline.PublicationYear,
            candidates);
        if (match.Status == ComicVineMatchStatus.NoMatch)
        {
            return offline;
        }

        if (match.Status == ComicVineMatchStatus.Ambiguous)
        {
            throw new MetadataReviewRequiredException(offline, match.Candidates);
        }

        ComicVineIssue issue = match.Issue!;
        return new ComicMetadata(
            string.IsNullOrWhiteSpace(issue.Name) ? offline.Title : issue.Name,
            string.IsNullOrWhiteSpace(issue.Series) ? offline.Series : issue.Series,
            issue.IssueNumber ?? offline.IssueNumber,
            offline.Volume,
            issue.StartYear ?? offline.PublicationYear,
            offline.Language);
    }
}
