namespace ABC.Converter.Core.Queue;

public static class QueueRecoveryPolicy
{
    public static QueueJobRecord Recover(
        QueueJobRecord job,
        Func<string, bool> outputExists,
        DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(outputExists);

        if (IsActive(job.State))
        {
            return job with
            {
                State = QueueJobState.Queued,
                Progress = 0,
                OutputPath = null,
                ErrorStage = null,
                UserError = null,
                TechnicalError = null,
                Retryable = false,
                UpdatedUtc = utcNow,
            };
        }

        if (job.State != QueueJobState.Completed)
        {
            return job;
        }

        if (string.IsNullOrWhiteSpace(job.OutputPath))
        {
            return MissingCompletedOutput(job, "The completed job has no recorded output path.", utcNow);
        }

        return outputExists(job.OutputPath)
            ? job
            : MissingCompletedOutput(job, job.OutputPath, utcNow);
    }

    private static bool IsActive(QueueJobState state) => state is
        QueueJobState.Scanning or
        QueueJobState.FetchingMetadata or
        QueueJobState.Extracting or
        QueueJobState.BuildingEpub or
        QueueJobState.Validating or
        QueueJobState.Publishing;

    private static QueueJobRecord MissingCompletedOutput(
        QueueJobRecord job,
        string technicalError,
        DateTimeOffset utcNow) => job with
    {
        State = QueueJobState.Failed,
        OutputPath = null,
        ErrorStage = "Recovery",
        UserError = "The completed EPUB could not be found.",
        TechnicalError = technicalError,
        Retryable = true,
        UpdatedUtc = utcNow,
    };
}
