using System.Security.Cryptography;
using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Infrastructure.Files;
using ABC.Converter.Infrastructure.Persistence;
using ABC.Converter.Tests.Fixtures;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ABC.Converter.Tests.Acceptance;

public sealed class QueueRecoveryAcceptanceTests
{
    [Fact]
    public async Task InterruptedJobRecoversConvertsOnceAndPreservesApprovalAndSource()
    {
        using var files = new RecoveryFiles();
        byte[] hash = SHA256.HashData(await File.ReadAllBytesAsync(files.Source));
        var record = await files.RecordAsync(QueueJobState.BuildingEpub);
        record = record with { ApprovedMetadata = new ComicMetadata("Approved title", "Approved series", "7") };
        await using (var first = new SqliteQueueStore(files.Database))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.AddJobsAsync([record], CancellationToken.None);
        }
        await using (var reopened = new SqliteQueueStore(files.Database))
        {
            await reopened.InitializeAsync(CancellationToken.None);
            var recovered = QueueRecoveryPolicy.Recover(Assert.Single(await reopened.LoadJobsAsync(CancellationToken.None)), File.Exists, DateTimeOffset.UtcNow);
            Assert.Equal(QueueJobState.Queued, recovered.State);
            await reopened.SaveJobAsync(recovered, CancellationToken.None);
            var result = await files.Service(new MustNotResolve()).ConvertAsync(
                new ConversionRequest(files.Source, files.Output, recovered.ApprovedMetadata!, MetadataIsApproved: true),
                CancellationToken.None,
                async (state, progress) => await reopened.SaveJobAsync(recovered with { State = state, Progress = progress }, CancellationToken.None),
                async (path, hash) => await reopened.SaveJobAsync(recovered with { State = QueueJobState.Completed, Progress = 100, OutputPath = path, ErrorStage = "Publishing", TechnicalError = "sha256:" + hash }, CancellationToken.None));
            Assert.True(File.Exists(result.OutputPath));
        }
        // Simulates a crash after the atomic rename but before the caller acknowledges completion.
        await using (var final = new SqliteQueueStore(files.Database))
        {
            await final.InitializeAsync(CancellationToken.None);
            var completed = Assert.Single(await final.LoadJobsAsync(CancellationToken.None));
            Assert.Same(completed, QueueRecoveryPolicy.Recover(completed, path => PublishedOutputIdentity.Matches(completed, path), DateTimeOffset.UtcNow));
            Assert.Equal(QueueJobState.Completed, completed.State);
            Assert.Equal(record.ApprovedMetadata, completed.ApprovedMetadata);
            Assert.Single(Directory.GetFiles(files.Output, "*.epub", SearchOption.AllDirectories));
            Assert.Empty(Directory.GetFiles(files.Output, "*.tmp", SearchOption.AllDirectories));
        }
        Assert.Equal(hash, SHA256.HashData(await File.ReadAllBytesAsync(files.Source)));
    }

    [Fact]
    public async Task CrashBeforeAtomicRenameBecomesVisibleMissingOutputFailure()
    {
        using var files = new RecoveryFiles();
        var record = await files.RecordAsync(QueueJobState.Queued);
        await using (var first = new SqliteQueueStore(files.Database))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.AddJobsAsync([record], CancellationToken.None);
            await Assert.ThrowsAsync<IOException>(() => files.Service().ConvertAsync(
                new ConversionRequest(files.Source, files.Output, record.RequestedMetadata), CancellationToken.None,
                beforePublication: async (path, hash) =>
                {
                    Assert.False(File.Exists(path));
                    await first.SaveJobAsync(record with { State = QueueJobState.Completed, Progress = 100, OutputPath = path, ErrorStage = "Publishing", TechnicalError = "sha256:" + hash }, CancellationToken.None);
                    throw new IOException("Simulated interruption before rename.");
                }));
        }
        await using var reopened = new SqliteQueueStore(files.Database);
        await reopened.InitializeAsync(CancellationToken.None);
        var recovered = QueueRecoveryPolicy.Recover(Assert.Single(await reopened.LoadJobsAsync(CancellationToken.None)), File.Exists, DateTimeOffset.UtcNow);
        Assert.Equal(QueueJobState.Failed, recovered.State);
        Assert.Equal("Recovery", recovered.ErrorStage);
        Assert.True(recovered.Retryable);
        Assert.Empty(Directory.GetFiles(files.Output, "*.epub", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task PublicationAwaitsDurableCheckpointBeforeCreatingFinalEpub()
    {
        using var files = new RecoveryFiles();
        var record = await files.RecordAsync(QueueJobState.Queued);
        var entered = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<ConversionResult> conversion = files.Service().ConvertAsync(
            new ConversionRequest(files.Source, files.Output, record.RequestedMetadata), CancellationToken.None,
            beforePublication: async (path, hash) => { entered.SetResult(path); await release.Task; });
        string output = await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(output));
        Assert.False(conversion.IsCompleted);
        release.SetResult();
        await conversion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(File.Exists(output));
    }

    [Fact]
    public async Task ReopenedStoreRetainsQueuedBatchReviewErrorsAndDuplicateIdentity()
    {
        using var files = new RecoveryFiles();
        var queued = await files.RecordAsync(QueueJobState.Queued);
        var pending = queued with { Id = Guid.NewGuid(), SourcePath = files.Source + ".review", State = QueueJobState.NeedsReview,
            MetadataReview = new QueueMetadataReview(queued.RequestedMetadata, [new("ComicVine", 12, "Candidate", "1", "Series", 2026)]) };
        var failed = queued with { Id = Guid.NewGuid(), SourcePath = files.Source + ".failed", State = QueueJobState.Failed,
            ErrorStage = "Validating", UserError = "Invalid EPUB.", TechnicalError = "InvalidDataException", Retryable = true, RetryCount = 3 };
        await using (var first = new SqliteQueueStore(files.Database))
        {
            await first.InitializeAsync(CancellationToken.None);
            await first.AddJobsAsync([queued, pending, failed], CancellationToken.None);
        }
        await using var reopened = new SqliteQueueStore(files.Database);
        await reopened.InitializeAsync(CancellationToken.None);
        var jobs = await reopened.LoadJobsAsync(CancellationToken.None);
        Assert.Equal(3, jobs.Count);
        Assert.Equivalent(pending.MetadataReview, Assert.Single(jobs, j => j.Id == pending.Id).MetadataReview, strict: true);
        Assert.Equivalent(failed, Assert.Single(jobs, j => j.Id == failed.Id), strict: true);
        Assert.Empty(await reopened.AddJobsAsync([queued with { Id = Guid.NewGuid() }], CancellationToken.None));
        var changed = queued with { Id = Guid.NewGuid(), SourceFingerprint = queued.SourceFingerprint + "-changed" };
        Assert.Equal([changed.Id], await reopened.AddJobsAsync([changed], CancellationToken.None));
    }

    private sealed class MustNotResolve : IComicMetadataResolver
    {
        public Task<ComicMetadata> ResolveAsync(string sourcePath, ComicMetadata defaults, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Approved metadata must bypass lookup.");
    }

    private sealed class RecoveryFiles : IDisposable
    {
        private readonly string root = Path.Combine(Path.GetTempPath(), "abc-restart-" + Guid.NewGuid().ToString("N"));
        public RecoveryFiles()
        {
            Directory.CreateDirectory(root);
            Source = Path.Combine(root, "comic.cbz");
            File.Move(FixtureArchive.CreateCbz(("page.png", FixtureArchive.ValidImage(".png"))), Source);
        }
        public string Source { get; }
        public string Database => Path.Combine(root, "queue.db");
        public string Output => Path.Combine(root, "output");
        public async Task<QueueJobRecord> RecordAsync(QueueJobState state) => new(Guid.NewGuid(), Source,
            await new SourceFingerprintProvider().GetFingerprintAsync(Source, CancellationToken.None), Output,
            new ComicMetadata("Title", "Series"), state, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        public ComicConversionService Service(IComicMetadataResolver? resolver = null)
        {
            var reader = new CbzArchiveReader();
            return new(reader, new FixedLayoutEpubBuilder(reader), new EpubStructureValidator(), new SeriesOutputPathPolicy(), metadataResolver: resolver);
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); Directory.Delete(root, recursive: true); }
    }
}
