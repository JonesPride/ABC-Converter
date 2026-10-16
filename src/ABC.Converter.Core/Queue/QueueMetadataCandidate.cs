namespace ABC.Converter.Core.Queue;

public sealed record QueueMetadataCandidate(
    string Provider,
    long ExternalId,
    string Name,
    string? IssueNumber,
    string Series,
    int? StartYear);
