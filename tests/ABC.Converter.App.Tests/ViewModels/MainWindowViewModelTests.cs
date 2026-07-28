using System.IO;
using ABC.Converter.App.Commands;
using ABC.Converter.App.ViewModels;
using ABC.Converter.App.Tests.TestSupport;
using Xunit;

namespace ABC.Converter.App.Tests.ViewModels;

public sealed class MainWindowViewModelTests
{
    [Fact]
    public async Task DiscoversCbzFilesRecursivelyAndCaseInsensitivelyWithoutOpeningArchives()
    {
        using var files = new TemporaryDirectory();
        string firstComic = files.Write("first.cbz", [0x01]);
        string secondComic = files.Write("nested/second.CBZ", [0x02]);
        files.Write("nested/not-a-comic.zip", [0x03]);
        files.Write("nested/also-not-a-comic.cbz.tmp", [0x04]);
        var reader = new RecordingArchiveReader();
        var viewModel = new MainWindowViewModel(ConversionHarness.CreateService(reader));

        await viewModel.AddPathsAsync([files.Path]);

        var expectedPaths = new HashSet<string>(
            [Path.GetFullPath(firstComic), Path.GetFullPath(secondComic)],
            StringComparer.OrdinalIgnoreCase);
        Assert.True(
            expectedPaths.SetEquals(viewModel.Jobs.Select(job => job.SourcePath)));
        Assert.All(viewModel.Jobs, job => Assert.Equal("Queued", job.Status));
        Assert.Equal(0, reader.ReadCount);
    }

    [Fact]
    public async Task SuppressesCanonicalDuplicatesWithoutRegardToPathCasing()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("nested/Comic.CBZ", [0x01]);
        string equivalentPath = Path.Combine(
            files.Path,
            "nested",
            ".",
            "..",
            "nested",
            "Comic.CBZ");
        string differentlyCasedPath = comicPath.ToUpperInvariant();
        var viewModel = new MainWindowViewModel(ConversionHarness.CreateService());

        await viewModel.AddPathsAsync(
            [comicPath, equivalentPath, differentlyCasedPath, files.Path]);

        ConversionJobViewModel job = Assert.Single(viewModel.Jobs);
        Assert.Equal(
            Path.GetFullPath(comicPath),
            job.SourcePath,
            ignoreCase: true);
    }

    [Fact]
    public async Task ConvertsQueuedJobsSequentiallyAndPublishesOrderedStatusTransitions()
    {
        using var files = new TemporaryDirectory();
        string firstComic = files.Write("First Comic.cbz", [0x01]);
        string secondComic = files.Write("Second Comic.cbz", [0x02]);
        var snapshots = new List<ConversionSnapshot>();
        MainWindowViewModel? viewModel = null;
        var reader = new RecordingArchiveReader(sourcePath =>
        {
            snapshots.Add(
                new ConversionSnapshot(
                    sourcePath,
                    viewModel!.Jobs.Select(job => job.Status).ToArray()));
        });
        viewModel = new MainWindowViewModel(ConversionHarness.CreateService(reader))
        {
            OutputRoot = files.PathFor("output"),
        };
        await viewModel.AddPathsAsync([firstComic, secondComic]);
        var statusChanges = viewModel.Jobs.ToDictionary(
            job => job,
            _ => new List<string>());
        foreach (ConversionJobViewModel job in viewModel.Jobs)
        {
            job.PropertyChanged += (_, eventArgs) =>
            {
                if (eventArgs.PropertyName == nameof(job.Status))
                {
                    statusChanges[job].Add(job.Status);
                }
            };
        }

        await viewModel.StartAsync(CancellationToken.None);

        Assert.Equal(
            [Path.GetFullPath(firstComic), Path.GetFullPath(secondComic)],
            snapshots.Select(snapshot => snapshot.SourcePath));
        Assert.NotEqual("Queued", snapshots[0].Statuses[0]);
        Assert.Equal("Queued", snapshots[0].Statuses[1]);
        Assert.Equal("Completed", snapshots[1].Statuses[0]);
        Assert.NotEqual("Queued", snapshots[1].Statuses[1]);
        Assert.All(
            statusChanges.Values,
            changes =>
            {
                Assert.NotEmpty(changes);
                Assert.NotEqual("Queued", changes[0]);
                Assert.Equal("Completed", changes[^1]);
                Assert.Equal(1, changes.Count(status => status == "Completed"));
            });
        Assert.All(viewModel.Jobs, job =>
        {
            Assert.Equal("Completed", job.Status);
            Assert.Equal(100, job.Progress);
            Assert.True(File.Exists(job.OutputPath));
        });
    }

    [Fact]
    public async Task MapsCancellationToCancelledWithoutReportingAFailure()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("cancelled.cbz", [0x01]);
        var viewModel = new MainWindowViewModel(ConversionHarness.CreateService())
        {
            OutputRoot = files.PathFor("output"),
        };
        await viewModel.AddPathsAsync([comicPath]);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await viewModel.StartAsync(cancellation.Token);

        ConversionJobViewModel job = Assert.Single(viewModel.Jobs);
        Assert.Equal("Cancelled", job.Status);
        Assert.Null(job.ErrorMessage);
        Assert.Null(job.OutputPath);
    }

    [Fact]
    public async Task MapsConversionFailureToFailedAndKeepsTheUserFacingMessage()
    {
        using var files = new TemporaryDirectory();
        string comicPath = files.Write("broken.cbz", [0x01]);
        var reader = new RecordingArchiveReader(
            readException: new InvalidDataException("The archive is corrupt."));
        var viewModel = new MainWindowViewModel(ConversionHarness.CreateService(reader))
        {
            OutputRoot = files.PathFor("output"),
        };
        await viewModel.AddPathsAsync([comicPath]);

        await viewModel.StartAsync(CancellationToken.None);

        ConversionJobViewModel job = Assert.Single(viewModel.Jobs);
        Assert.Equal("Failed", job.Status);
        Assert.Equal("The archive is corrupt.", job.ErrorMessage);
        Assert.Null(job.OutputPath);
    }

    [Fact]
    public async Task CancellingLaterQueuedJobWhileFirstRunsPreventsItsConversion()
    {
        using var files = new TemporaryDirectory();
        string firstComic = files.Write("first.cbz", [0x01]);
        string secondComic = files.Write("second.cbz", [0x02]);
        var firstReadStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFirstRead = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readCount = 0;
        var reader = new RecordingArchiveReader(
            beforeReturn: async (_, cancellationToken) =>
            {
                if (Interlocked.Increment(ref readCount) == 1)
                {
                    firstReadStarted.TrySetResult();
                    await releaseFirstRead.Task.WaitAsync(cancellationToken);
                }
            });
        var viewModel = new MainWindowViewModel(
            ConversionHarness.CreateService(reader))
        {
            OutputRoot = files.PathFor("output"),
        };
        await viewModel.AddPathsAsync([firstComic, secondComic]);
        ConversionJobViewModel firstJob = viewModel.Jobs[0];
        ConversionJobViewModel secondJob = viewModel.Jobs[1];
        viewModel.SelectedJob = secondJob;

        Task conversion = viewModel.StartAsync(CancellationToken.None);
        await firstReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var cancelCommand = Assert.IsType<AsyncCommand>(
            viewModel.CancelSelectedCommand);

        await cancelCommand.ExecuteAsync();
        string statusAfterCancellation = secondJob.Status;
        releaseFirstRead.TrySetResult();
        await conversion.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Cancelled", statusAfterCancellation);
        Assert.Equal([Path.GetFullPath(firstComic)], reader.ReadPaths);
        Assert.Equal("Completed", firstJob.Status);
        Assert.Equal("Cancelled", secondJob.Status);
    }

    private sealed record ConversionSnapshot(
        string SourcePath,
        IReadOnlyList<string> Statuses);
}
