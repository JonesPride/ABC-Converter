using System.IO;
using ABC.Converter.App.Commands;
using ABC.Converter.App.Services;
using ABC.Converter.App.Tests.TestSupport;
using ABC.Converter.App.ViewModels;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using ABC.Converter.Infrastructure.Persistence;
using Xunit;

namespace ABC.Converter.App.Tests.ViewModels;

public sealed class QueuePersistenceViewModelTests
{
    private static readonly DateTimeOffset UtcNow =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task InitializationRestoresJobsInDeterministicCreatedOrder()
    {
        QueueJobRecord later = CreateRecord(
            "later.cbz",
            createdUtc: UtcNow.AddMinutes(1),
            id: Guid.Parse("00000000-0000-0000-0000-000000000002"));
        QueueJobRecord sameTimeSecond = CreateRecord(
            "second-at-same-time.cbz",
            createdUtc: UtcNow,
            id: Guid.Parse("00000000-0000-0000-0000-000000000003"));
        QueueJobRecord sameTimeFirst = CreateRecord(
            "first-at-same-time.cbz",
            createdUtc: UtcNow,
            id: Guid.Parse("00000000-0000-0000-0000-000000000001"));
        var store = new InMemoryQueueStore(
            [later, sameTimeSecond, sameTimeFirst]);
        MainWindowViewModel viewModel = CreateViewModel(store);

        await viewModel.InitializeAsync(CancellationToken.None);

        Assert.Equal(
            [sameTimeFirst.Id, sameTimeSecond.Id, later.Id],
            viewModel.Jobs.Select(job => job.Id));
    }

    [Fact]
    public async Task InitializationAwaitsRecoveryWritesBeforeReturning()
    {
        QueueJobRecord interrupted = CreateRecord(
            "interrupted.cbz",
            state: QueueJobState.BuildingEpub) with
        {
            Progress = 75,
            OutputPath = "partial.tmp",
        };
        var saveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryQueueStore([interrupted])
        {
            BeforeSaveJobAsync = job =>
            {
                if (job.State == QueueJobState.Queued)
                {
                    saveEntered.TrySetResult();
                    return releaseSave.Task;
                }

                return Task.CompletedTask;
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(store);

        Task initialization = viewModel.InitializeAsync(CancellationToken.None);
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(initialization.IsCompleted);
        Assert.Empty(viewModel.Jobs);

        releaseSave.TrySetResult();
        await initialization.WaitAsync(TimeSpan.FromSeconds(5));

        ConversionJobViewModel restored = Assert.Single(viewModel.Jobs);
        Assert.Equal("Queued", restored.Status);
        Assert.Equal(0, restored.Progress);
        Assert.Null(restored.OutputPath);
        Assert.Equal(QueueJobState.Queued, Assert.Single(store.SavedJobs).State);
    }

    [Fact]
    public async Task AddingBatchFingerprintsSequentiallyAndWaitsForDurableInsertBeforeUiInsertion()
    {
        using var files = new TemporaryDirectory();
        string firstPath = files.Write("first.cbz", [0x01]);
        string secondPath = files.Write("second.cbz", [0x02]);
        var addEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdd = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryQueueStore
        {
            BeforeAddJobsAsync = _ =>
            {
                addEntered.TrySetResult();
                return releaseAdd.Task;
            },
        };
        var fingerprints = new RecordingFingerprintProvider();
        MainWindowViewModel viewModel = CreateViewModel(store, fingerprints);
        await viewModel.InitializeAsync(CancellationToken.None);

        Task add = viewModel.AddPathsAsync([firstPath, secondPath]);
        await addEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Empty(viewModel.Jobs);
        Assert.Equal(1, fingerprints.MaximumConcurrency);
        Assert.Equal(2, fingerprints.RequestedPaths.Count);

        releaseAdd.TrySetResult();
        await add.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, viewModel.Jobs.Count);
        Assert.Equal(2, store.Records.Count);
        Assert.All(
            viewModel.Jobs,
            job => Assert.Contains(
                store.Records,
                record => record.Id == job.Id));
    }

    [Fact]
    public async Task DuplicateIdentitySurvivesRestartButChangedFingerprintIsAccepted()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("same.cbz", [0x01]);
        var store = new InMemoryQueueStore();
        var fingerprints = new RecordingFingerprintProvider();
        fingerprints.Set(comicPath, "v1:1:100");
        MainWindowViewModel first = CreateViewModel(store, fingerprints);
        await first.InitializeAsync(CancellationToken.None);
        await first.AddPathsAsync([comicPath]);

        MainWindowViewModel reopened = CreateViewModel(store, fingerprints);
        await reopened.InitializeAsync(CancellationToken.None);
        await reopened.AddPathsAsync([comicPath]);

        Assert.Single(reopened.Jobs);
        Assert.Single(store.Records);

        fingerprints.Set(comicPath, "v1:2:200");
        await reopened.AddPathsAsync([comicPath]);

        Assert.Equal(2, reopened.Jobs.Count);
        Assert.Equal(2, store.Records.Count);
        Assert.Equal(
            ["v1:1:100", "v1:2:200"],
            reopened.Jobs.Select(job => job.SourceFingerprint));
    }

    [Fact]
    public async Task ApprovingMetadataPersistsTheDecisionAcrossRestart()
    {
        var candidate = new QueueMetadataCandidate(
            "ComicVine",
            101,
            "Approved title",
            "7",
            "Approved series",
            2026);
        QueueJobRecord pending = CreateRecord(
            "review.cbz",
            state: QueueJobState.NeedsReview) with
        {
            MetadataReview = new QueueMetadataReview(
                new ComicMetadata("Offline title", "Offline series"),
                [candidate]),
        };
        var store = new InMemoryQueueStore([pending]);
        MainWindowViewModel first = CreateViewModel(store);
        await first.InitializeAsync(CancellationToken.None);
        ConversionJobViewModel job = Assert.Single(first.Jobs);
        first.SelectedJob = job;
        MetadataReviewViewModel review = Assert.IsType<MetadataReviewViewModel>(
            job.MetadataReview);
        review.SelectedCandidate = Assert.Single(review.Candidates);
        var approve = Assert.IsType<AsyncCommand>(first.ApproveMetadataCommand);

        await approve.ExecuteAsync();

        MainWindowViewModel reopened = CreateViewModel(store);
        await reopened.InitializeAsync(CancellationToken.None);
        ConversionJobViewModel restored = Assert.Single(reopened.Jobs);
        Assert.Equal("Queued", restored.Status);
        Assert.Null(restored.MetadataReview);
        Assert.Equal(
            new ComicMetadata(
                "Approved title",
                "Approved series",
                "7",
                PublicationYear: 2026),
            restored.ApprovedMetadata);
    }

    [Fact]
    public async Task PendingReviewAndFailureDetailsRestoreAcrossNewViewModelInstances()
    {
        var reviewCandidate = new QueueMetadataCandidate(
            "ComicVine",
            202,
            "Candidate title",
            "2",
            "Candidate series",
            2025);
        QueueJobRecord pending = CreateRecord(
            "pending.cbz",
            state: QueueJobState.NeedsReview) with
        {
            Progress = 19,
            MetadataReview = new QueueMetadataReview(
                new ComicMetadata("Offline", "Offline series"),
                [reviewCandidate]),
        };
        ComicMetadata approved = new("Approved", "Approved series", "4");
        QueueJobRecord failed = CreateRecord(
            "failed.cbz",
            state: QueueJobState.Failed) with
        {
            Progress = 63,
            ApprovedMetadata = approved,
            OutputPath = "failed-output.epub",
            ErrorStage = "Validating",
            UserError = "Invalid EPUB.",
            TechnicalError = "InvalidDataException: Invalid EPUB.",
            Retryable = true,
            RetryCount = 3,
        };
        var store = new InMemoryQueueStore(
            [failed, pending],
            new Dictionary<string, string>
            {
                ["OutputRoot"] = "persisted-output",
            });

        MainWindowViewModel first = CreateViewModel(store);
        await first.InitializeAsync(CancellationToken.None);
        MainWindowViewModel reopened = CreateViewModel(store);
        await reopened.InitializeAsync(CancellationToken.None);

        Assert.Equal("persisted-output", reopened.OutputRoot);
        ConversionJobViewModel restoredPending = Assert.Single(
            reopened.Jobs,
            job => job.Id == pending.Id);
        Assert.Equal("Needs Review", restoredPending.Status);
        Assert.Equal(19, restoredPending.Progress);
        MetadataReviewViewModel review = Assert.IsType<MetadataReviewViewModel>(
            restoredPending.MetadataReview);
        Assert.Equal("Offline", review.Title);
        Assert.Equal(202, Assert.Single(review.Candidates).Id);

        ConversionJobViewModel restoredFailed = Assert.Single(
            reopened.Jobs,
            job => job.Id == failed.Id);
        Assert.Equal("Failed", restoredFailed.Status);
        Assert.Equal(63, restoredFailed.Progress);
        Assert.Equal("failed-output.epub", restoredFailed.OutputPath);
        Assert.Equal("Invalid EPUB.", restoredFailed.ErrorMessage);
        Assert.Equal("Validating", restoredFailed.ErrorStage);
        Assert.Equal(
            "InvalidDataException: Invalid EPUB.",
            restoredFailed.TechnicalError);
        Assert.True(restoredFailed.Retryable);
        Assert.Equal(3, restoredFailed.RetryCount);
        Assert.Equal(approved, restoredFailed.ApprovedMetadata);
    }

    [Fact]
    public async Task OutputRootChangeSurvivesGracefulShutdownAndRestart()
    {
        using var files = new TemporaryDirectory();
        var store = new InMemoryQueueStore();
        MainWindowViewModel first = CreateViewModel(store);
        await first.InitializeAsync(CancellationToken.None);
        first.OutputRoot = files.PathFor("durable-output");

        await first.PrepareForShutdownAsync(TimeSpan.FromSeconds(5));

        MainWindowViewModel reopened = CreateViewModel(store);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal(first.OutputRoot, reopened.OutputRoot);
    }

    [Fact]
    public async Task ConversionWaitsForScanningWriteBeforeReadingTheArchive()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("ordered.cbz", [0x01]);
        var reader = new RecordingArchiveReader();
        var saveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryQueueStore
        {
            BeforeSaveJobAsync = job =>
            {
                if (job.State == QueueJobState.Scanning)
                {
                    saveEntered.TrySetResult();
                    return releaseSave.Task;
                }

                return Task.CompletedTask;
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            service: ConversionHarness.CreateService(reader));
        viewModel.OutputRoot = files.PathFor("output");
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.AddPathsAsync([comicPath]);

        Task conversion = viewModel.StartAsync(CancellationToken.None);
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, reader.ReadCount);
        Assert.False(conversion.IsCompleted);

        releaseSave.TrySetResult();
        await conversion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, reader.ReadCount);
        Assert.Equal(
            [QueueJobState.Scanning, QueueJobState.FetchingMetadata, QueueJobState.BuildingEpub,
                QueueJobState.Validating, QueueJobState.Publishing, QueueJobState.Completed, QueueJobState.Completed],
            store.SavedJobs.Select(job => job.State));
    }

    [Fact]
    public async Task ConversionFailurePersistsSafeRetryableErrorDetails()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("broken.cbz", [0x01]);
        var reader = new RecordingArchiveReader(
            readException: new InvalidDataException("The archive is corrupt."));
        var store = new InMemoryQueueStore();
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            service: ConversionHarness.CreateService(reader));
        viewModel.OutputRoot = files.PathFor("output");
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.AddPathsAsync([comicPath]);

        await viewModel.StartAsync(CancellationToken.None);

        QueueJobRecord failed = Assert.Single(store.Records);
        Assert.Equal(QueueJobState.Failed, failed.State);
        Assert.Equal("Scanning", failed.ErrorStage);
        Assert.Equal("The archive is corrupt.", failed.UserError);
        string technicalError = Assert.IsType<string>(failed.TechnicalError);
        Assert.Contains(nameof(InvalidDataException), technicalError);
        Assert.Contains("The archive is corrupt.", technicalError);
        Assert.True(failed.Retryable);
        Assert.Null(failed.OutputPath);
    }

    [Fact]
    public async Task ConversionFailureRedactsApiKeyFromEveryDurableErrorField()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("secret-error.cbz", [0x01]);
        var reader = new RecordingArchiveReader(
            readException: new InvalidOperationException(
                "ComicVine request failed: https://example.test/?API_KEY=top-secret&format=json"));
        var store = new InMemoryQueueStore();
        MainWindowViewModel viewModel = CreateViewModel(
            store,
            service: ConversionHarness.CreateService(reader));
        viewModel.OutputRoot = files.PathFor("output");
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.AddPathsAsync([comicPath]);

        await viewModel.StartAsync(CancellationToken.None);

        QueueJobRecord failed = Assert.Single(store.Records);
        string userError = Assert.IsType<string>(failed.UserError);
        string technicalError = Assert.IsType<string>(failed.TechnicalError);
        Assert.Contains("ComicVine request failed", userError);
        Assert.Contains("format=json", userError);
        Assert.DoesNotContain(
            "top-secret",
            userError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "API_KEY=top-secret",
            userError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "top-secret",
            technicalError,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "API_KEY=top-secret",
            technicalError,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RetryAwaitsQueuedWriteClearsTransientStateAndPreservesApproval()
    {
        ComicMetadata approved = new("Approved", "Approved series", "8");
        QueueJobRecord failed = CreateRecord(
            "retry.cbz",
            state: QueueJobState.Failed) with
        {
            Progress = 80,
            ApprovedMetadata = approved,
            MetadataReview = new QueueMetadataReview(
                new ComicMetadata("Offline", "Offline series"),
                []),
            OutputPath = "partial.epub",
            ErrorStage = "BuildingEpub",
            UserError = "Build failed.",
            TechnicalError = "IOException: Build failed.",
            Retryable = true,
            RetryCount = 2,
        };
        var saveEntered = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSave = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new InMemoryQueueStore([failed])
        {
            BeforeSaveJobAsync = job =>
            {
                if (job.State == QueueJobState.Queued)
                {
                    saveEntered.TrySetResult();
                    return releaseSave.Task;
                }

                return Task.CompletedTask;
            },
        };
        MainWindowViewModel viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync(CancellationToken.None);
        viewModel.SelectedJob = Assert.Single(viewModel.Jobs);
        var retry = Assert.IsType<AsyncCommand>(viewModel.RetrySelectedCommand);

        Task retrying = retry.ExecuteAsync();
        await saveEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(retrying.IsCompleted);
        releaseSave.TrySetResult();
        await retrying.WaitAsync(TimeSpan.FromSeconds(5));

        QueueJobRecord saved = Assert.Single(store.SavedJobs);
        Assert.Equal(QueueJobState.Queued, saved.State);
        Assert.Equal(0, saved.Progress);
        Assert.Null(saved.MetadataReview);
        Assert.Null(saved.OutputPath);
        Assert.Null(saved.ErrorStage);
        Assert.Null(saved.UserError);
        Assert.Null(saved.TechnicalError);
        Assert.False(saved.Retryable);
        Assert.Equal(3, saved.RetryCount);
        Assert.Equal(approved, saved.ApprovedMetadata);
    }

    [Fact]
    public async Task QueuedCancellationIsPersisted()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("cancel.cbz", [0x01]);
        var store = new InMemoryQueueStore();
        MainWindowViewModel viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync(CancellationToken.None);
        await viewModel.AddPathsAsync([comicPath]);
        ConversionJobViewModel selected = Assert.Single(viewModel.Jobs);
        viewModel.SelectedJob = selected;
        var cancel = Assert.IsType<AsyncCommand>(viewModel.CancelSelectedCommand);

        await cancel.ExecuteAsync();

        QueueJobRecord saved = Assert.Single(store.SavedJobs);
        Assert.Equal(QueueJobState.Cancelled, saved.State);
        Assert.Equal("Cancelled", selected.Status);
    }

    [Fact]
    public async Task GracefulShutdownCancelsActiveWorkAndLeavesItQueuedForRestart()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("shutdown.cbz", [0x01]);
        var readStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var reader = new RecordingArchiveReader(
            beforeReturn: async (_, cancellationToken) =>
            {
                readStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
        var store = new InMemoryQueueStore();
        MainWindowViewModel first = CreateViewModel(
            store,
            service: ConversionHarness.CreateService(reader));
        first.OutputRoot = files.PathFor("output");
        await first.InitializeAsync(CancellationToken.None);
        await first.AddPathsAsync([comicPath]);
        Task conversion = first.StartAsync(CancellationToken.None);
        await readStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await first.PrepareForShutdownAsync(TimeSpan.FromSeconds(5));
        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await conversion.WaitAsync(TimeSpan.FromSeconds(5));

        MainWindowViewModel reopened = CreateViewModel(store);
        await reopened.InitializeAsync(CancellationToken.None);
        Assert.Equal("Queued", Assert.Single(reopened.Jobs).Status);
        Assert.Equal(QueueJobState.Queued, Assert.Single(store.Records).State);
    }

    [Fact]
    public async Task InitializationCanOnlyRunOnce()
    {
        var store = new InMemoryQueueStore();
        MainWindowViewModel viewModel = CreateViewModel(store);
        await viewModel.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.InitializeAsync(CancellationToken.None));

        Assert.Equal(1, store.InitializeCallCount);
    }

    [Fact]
    public void ApplicationDataPathUsesPerUserAbcConverterDirectoryWithoutCreatingADatabaseDirectory()
    {
        string databasePath = ApplicationDataPaths.QueueDatabasePath;
        string parentPath = Assert.IsType<string>(Path.GetDirectoryName(databasePath));

        Assert.Equal("abc-converter.db", Path.GetFileName(databasePath));
        Assert.Equal("ABC Converter", new DirectoryInfo(parentPath).Name);
        Assert.True(Directory.Exists(parentPath));
        Assert.False(Directory.Exists(databasePath));
    }

    [Fact]
    public void SafeExceptionDetailsRedactsApiKeysAndDoesNotTraverseNestedState()
    {
        Exception exception = CaptureSensitiveException();

        string details = SafeExceptionDetails.Format(exception);

        Assert.Contains(nameof(InvalidOperationException), details);
        Assert.Contains("ComicVine request failed", details);
        Assert.DoesNotContain("top-secret", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("inner-secret", details, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("data-secret", details, StringComparison.OrdinalIgnoreCase);
    }

    private static MainWindowViewModel CreateViewModel(
        InMemoryQueueStore store,
        ISourceFingerprintProvider? fingerprints = null,
        ABC.Converter.Infrastructure.Conversion.ComicConversionService? service = null)
    {
        return new MainWindowViewModel(
            service ?? ConversionHarness.CreateService(),
            queueStore: store,
            fingerprintProvider: fingerprints ?? new RecordingFingerprintProvider(),
            utcNow: () => UtcNow);
    }

    private static QueueJobRecord CreateRecord(
        string sourcePath,
        QueueJobState state = QueueJobState.Queued,
        DateTimeOffset? createdUtc = null,
        Guid? id = null)
    {
        string title = Path.GetFileNameWithoutExtension(sourcePath);
        DateTimeOffset created = createdUtc ?? UtcNow.AddHours(-1);
        return new QueueJobRecord(
            id ?? Guid.NewGuid(),
            Path.GetFullPath(sourcePath),
            "v1:1:1",
            "output",
            new ComicMetadata(title, title),
            state,
            0,
            created,
            created);
    }

    private static Exception CaptureSensitiveException()
    {
        try
        {
            throw new InvalidOperationException(
                "ComicVine request failed: https://example.test/?API_KEY=top-secret&format=json",
                new IOException("inner-secret"));
        }
        catch (Exception exception)
        {
            exception.Data["token"] = "data-secret";
            return exception;
        }
    }

    private sealed class RecordingFingerprintProvider : ISourceFingerprintProvider
    {
        private readonly Dictionary<string, string> fingerprints =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly System.Collections.Concurrent.ConcurrentQueue<string>
            requestedPaths = [];
        private int concurrentCalls;
        private int maximumConcurrency;

        public IReadOnlyList<string> RequestedPaths => requestedPaths.ToArray();

        public int MaximumConcurrency => Volatile.Read(ref maximumConcurrency);

        public void Set(string sourcePath, string fingerprint)
        {
            fingerprints[Path.GetFullPath(sourcePath)] = fingerprint;
        }

        public async Task<string> GetFingerprintAsync(
            string sourcePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int active = Interlocked.Increment(ref concurrentCalls);
            UpdateMaximumConcurrency(active);
            try
            {
                requestedPaths.Enqueue(sourcePath);
                await Task.Yield();
                return fingerprints.TryGetValue(
                    Path.GetFullPath(sourcePath),
                    out string? fingerprint) && fingerprint is not null
                    ? fingerprint
                    : "v1:1:1";
            }
            finally
            {
                Interlocked.Decrement(ref concurrentCalls);
            }
        }

        private void UpdateMaximumConcurrency(int value)
        {
            int current = Volatile.Read(ref maximumConcurrency);
            while (value > current)
            {
                int observed = Interlocked.CompareExchange(
                    ref maximumConcurrency,
                    value,
                    current);
                if (observed == current)
                {
                    return;
                }

                current = observed;
            }
        }
    }
}
