using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Queue;

public sealed record QueueJobRecord(
    Guid Id,
    string SourcePath,
    string SourceFingerprint,
    string OutputRoot,
    ComicMetadata RequestedMetadata,
    QueueJobState State,
    int Progress,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ComicMetadata? ApprovedMetadata = null,
    QueueMetadataReview? MetadataReview = null,
    string? OutputPath = null,
    string? ErrorStage = null,
    string? UserError = null,
    string? TechnicalError = null,
    bool Retryable = false,
    int RetryCount = 0);
