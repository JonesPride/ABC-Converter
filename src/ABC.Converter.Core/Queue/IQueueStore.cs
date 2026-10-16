namespace ABC.Converter.Core.Queue;

public interface IQueueStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<QueueJobRecord>> LoadJobsAsync(CancellationToken cancellationToken);

    Task<string?> LoadSettingAsync(string key, CancellationToken cancellationToken);

    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken);

    Task<IReadOnlyList<Guid>> AddJobsAsync(
        IReadOnlyList<QueueJobRecord> jobs,
        CancellationToken cancellationToken);

    Task SaveJobAsync(QueueJobRecord job, CancellationToken cancellationToken);
}
