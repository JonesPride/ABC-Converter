namespace ABC.Converter.Core.Queue;

public enum QueueJobState
{
    Queued,
    Scanning,
    FetchingMetadata,
    NeedsReview,
    Extracting,
    BuildingEpub,
    Validating,
    Publishing,
    Completed,
    Cancelled,
    Failed,
}
