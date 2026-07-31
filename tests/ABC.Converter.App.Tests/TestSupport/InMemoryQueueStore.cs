using ABC.Converter.Core.Queue;

namespace ABC.Converter.App.Tests.TestSupport;

internal sealed class InMemoryQueueStore : IQueueStore
{
    private readonly object sync = new();
    private readonly List<QueueJobRecord> jobs;
    private readonly Dictionary<string, string> settings;
    private readonly List<QueueJobRecord> savedJobs = [];

    public InMemoryQueueStore(
        IEnumerable<QueueJobRecord>? jobs = null,
        IReadOnlyDictionary<string, string>? settings = null)
    {
        this.jobs = jobs?.ToList() ?? [];
        this.settings = new Dictionary<string, string>(StringComparer.Ordinal);
        if (settings is not null)
        {
            foreach ((string key, string value) in settings)
            {
                this.settings.Add(key, value);
            }
        }
    }

    public Func<IReadOnlyList<QueueJobRecord>, Task>? BeforeAddJobsAsync { get; set; }

    public Func<QueueJobRecord, Task>? BeforeSaveJobAsync { get; set; }

    public int InitializeCallCount { get; private set; }

    public IReadOnlyList<QueueJobRecord> Records
    {
        get
        {
            lock (sync)
            {
                return jobs.ToArray();
            }
        }
    }

    public IReadOnlyList<QueueJobRecord> SavedJobs
    {
        get
        {
            lock (sync)
            {
                return savedJobs.ToArray();
            }
        }
    }

    public Task InitializeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InitializeCallCount++;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<QueueJobRecord>> LoadJobsAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            return Task.FromResult<IReadOnlyList<QueueJobRecord>>(jobs.ToArray());
        }
    }

    public Task<string?> LoadSettingAsync(
        string key,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            settings.TryGetValue(key, out string? value);
            return Task.FromResult(value);
        }
    }

    public Task SaveSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            settings[key] = value;
        }

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<Guid>> AddJobsAsync(
        IReadOnlyList<QueueJobRecord> records,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeAddJobsAsync is not null)
        {
            await BeforeAddJobsAsync(records);
        }

        cancellationToken.ThrowIfCancellationRequested();
        var inserted = new List<Guid>();
        lock (sync)
        {
            foreach (QueueJobRecord record in records)
            {
                bool duplicate = jobs.Any(existing =>
                    string.Equals(
                        existing.SourcePath,
                        record.SourcePath,
                        StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(
                        existing.SourceFingerprint,
                        record.SourceFingerprint,
                        StringComparison.Ordinal));
                if (duplicate)
                {
                    continue;
                }

                jobs.Add(record);
                inserted.Add(record.Id);
            }
        }

        return inserted;
    }

    public async Task SaveJobAsync(
        QueueJobRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        cancellationToken.ThrowIfCancellationRequested();
        if (BeforeSaveJobAsync is not null)
        {
            await BeforeSaveJobAsync(job);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (sync)
        {
            int index = jobs.FindIndex(existing => existing.Id == job.Id);
            if (index < 0)
            {
                throw new InvalidOperationException($"Queue job '{job.Id}' does not exist.");
            }

            jobs[index] = job;
            savedJobs.Add(job);
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
