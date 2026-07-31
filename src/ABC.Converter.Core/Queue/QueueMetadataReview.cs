using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Queue;

public sealed record QueueMetadataReview(
    ComicMetadata OfflineMetadata,
    IReadOnlyList<QueueMetadataCandidate> Candidates);
