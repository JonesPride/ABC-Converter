using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using Xunit;

namespace ABC.Converter.Tests.Queue;

public sealed class QueueRecoveryPolicyTests
{
    private static readonly DateTimeOffset RecoveryUtc =
        new(2026, 7, 31, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(QueueJobState.Scanning)]
    [InlineData(QueueJobState.FetchingMetadata)]
    [InlineData(QueueJobState.Extracting)]
    [InlineData(QueueJobState.BuildingEpub)]
    [InlineData(QueueJobState.Validating)]
    [InlineData(QueueJobState.Publishing)]
    public void ActiveJobsReturnToQueued(QueueJobState state)
    {
        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(
            CreateJob(state) with
            {
                Progress = 75,
                OutputPath = "partial.tmp",
                ErrorStage = "BuildingEpub",
                UserError = "The build stopped.",
                TechnicalError = "Interrupted during shutdown.",
                Retryable = true,
            },
            _ => false,
            RecoveryUtc);

        Assert.Equal(QueueJobState.Queued, recovered.State);
        Assert.Equal(0, recovered.Progress);
        Assert.Null(recovered.OutputPath);
        Assert.Null(recovered.ErrorStage);
        Assert.Null(recovered.UserError);
        Assert.Null(recovered.TechnicalError);
        Assert.False(recovered.Retryable);
        Assert.Equal(RecoveryUtc, recovered.UpdatedUtc);
    }

    [Fact]
    public void NeedsReviewPreservesItsMetadataCandidates()
    {
        QueueMetadataCandidate[] candidates =
        [
            new("Comic Vine", 101, "Detective Comics #1", "1", "Detective Comics", 1937),
            new("Comic Vine", 102, "Detective Comics #1", "1", "Detective Comics", 2011),
        ];
        var review = new QueueMetadataReview(
            new ComicMetadata("Detective Comics #1", "Detective Comics", "1", PublicationYear: 1937),
            candidates);
        QueueJobRecord job = CreateJob(QueueJobState.NeedsReview) with
        {
            MetadataReview = review,
            UpdatedUtc = RecoveryUtc.AddHours(-1),
        };

        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(job, _ => false, RecoveryUtc);

        Assert.Same(job, recovered);
        Assert.Same(review, recovered.MetadataReview);
        Assert.Same(candidates, recovered.MetadataReview!.Candidates);
    }

    [Fact]
    public void CompletedJobWithExistingEpubRemainsCompleted()
    {
        QueueJobRecord job = CreateJob(QueueJobState.Completed) with
        {
            OutputPath = "output/detective-comics-1.epub",
            Progress = 100,
        };

        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(job, _ => true, RecoveryUtc);

        Assert.Same(job, recovered);
        Assert.Equal(QueueJobState.Completed, recovered.State);
        Assert.Equal("output/detective-comics-1.epub", recovered.OutputPath);
    }

    [Fact]
    public void CompletedJobWithMissingEpubBecomesRetryableRecoveryFailure()
    {
        const string missingPath = "output/detective-comics-1.epub";
        QueueJobRecord job = CreateJob(QueueJobState.Completed) with
        {
            OutputPath = missingPath,
            Progress = 100,
            ErrorStage = "OldStage",
            UserError = "Old error.",
            TechnicalError = "Old technical error.",
            UpdatedUtc = RecoveryUtc.AddHours(-1),
        };

        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(job, _ => false, RecoveryUtc);

        Assert.Equal(QueueJobState.Failed, recovered.State);
        Assert.Null(recovered.OutputPath);
        Assert.Equal("Recovery", recovered.ErrorStage);
        Assert.Equal("The completed EPUB could not be found.", recovered.UserError);
        Assert.Equal(missingPath, recovered.TechnicalError);
        Assert.True(recovered.Retryable);
        Assert.Equal(RecoveryUtc, recovered.UpdatedUtc);
    }

    [Fact]
    public void CompletedJobWithoutRecordedOutputBecomesRetryableRecoveryFailure()
    {
        bool outputExistsCalled = false;
        QueueJobRecord job = CreateJob(QueueJobState.Completed) with
        {
            OutputPath = null,
            Progress = 100,
            UpdatedUtc = RecoveryUtc.AddHours(-1),
        };

        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(
            job,
            _ =>
            {
                outputExistsCalled = true;
                return false;
            },
            RecoveryUtc);

        string technicalError = Assert.IsType<string>(recovered.TechnicalError);
        Assert.False(outputExistsCalled);
        Assert.Equal(QueueJobState.Failed, recovered.State);
        Assert.Null(recovered.OutputPath);
        Assert.Equal("Recovery", recovered.ErrorStage);
        Assert.Equal("The completed EPUB could not be found.", recovered.UserError);
        Assert.Contains("recorded output path", technicalError, StringComparison.OrdinalIgnoreCase);
        Assert.True(recovered.Retryable);
        Assert.Equal(RecoveryUtc, recovered.UpdatedUtc);
    }

    [Theory]
    [InlineData(QueueJobState.Queued)]
    [InlineData(QueueJobState.Cancelled)]
    [InlineData(QueueJobState.Failed)]
    public void NonRecoverableJobsRemainUnchanged(QueueJobState state)
    {
        QueueJobRecord job = CreateJob(state) with
        {
            Progress = 42,
            OutputPath = "previous-output.epub",
            ErrorStage = "PreviousStage",
            UserError = "Previous user error.",
            TechnicalError = "Previous technical error.",
            UpdatedUtc = RecoveryUtc.AddHours(-1),
        };

        QueueJobRecord recovered = QueueRecoveryPolicy.Recover(job, _ => false, RecoveryUtc);

        Assert.Same(job, recovered);
    }

    private static QueueJobRecord CreateJob(QueueJobState state) => new(
        Guid.Parse("7100c4df-80c1-46f3-a4fb-da7a1e1e9872"),
        "source/detective-comics-1.cbz",
        "v1:1024:638895888000000000",
        "output",
        new ComicMetadata("Detective Comics #1", "Detective Comics", "1", PublicationYear: 1937),
        state,
        0,
        RecoveryUtc.AddHours(-2),
        RecoveryUtc.AddHours(-1));
}
