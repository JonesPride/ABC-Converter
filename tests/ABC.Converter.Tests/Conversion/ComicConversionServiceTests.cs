using System.Security.Cryptography;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Conversion;

public sealed class ComicConversionServiceTests
{
    [Fact]
    public async Task ConvertsWithoutChangingSourceBytesAndLeavesNoTemporaryFiles()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.PathFor("source.cbz");
        File.Move(
            FixtureArchive.CreateCbz(
                ("page1.png", FixtureArchive.ValidImage(".png")),
                ("page2.jpg", FixtureArchive.ValidImage(".jpg"))),
            sourcePath);
        byte[] sourceHash = SHA256.HashData(await File.ReadAllBytesAsync(sourcePath));
        string outputPath = files.PathFor("output/book.epub");
        var reader = new CbzArchiveReader();
        var service = new ComicConversionService(
            reader,
            new FixedLayoutEpubBuilder(reader),
            new EpubStructureValidator(),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        ConversionResult result = await service.ConvertAsync(
            Request(sourcePath, files.Path),
            CancellationToken.None);

        Assert.Equal(Path.GetFullPath(sourcePath), result.SourcePath);
        Assert.Equal(outputPath, result.OutputPath);
        Assert.Equal(2, result.PageCount);
        Assert.True(File.Exists(outputPath));
        await new EpubStructureValidator().ValidateAsync(outputPath, CancellationToken.None);
        Assert.Equal(sourceHash, SHA256.HashData(await File.ReadAllBytesAsync(sourcePath)));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Fact]
    public async Task GivesSimultaneousConversionsUniqueTemporaryBuildDestinations()
    {
        using var files = new TemporaryDirectory();
        string firstSource = files.Write("first.cbz", [0x01]);
        string secondSource = files.Write("second.cbz", [0x02]);
        string outputPath = files.PathFor("output/book.epub");
        var destinations = new List<string>();
        var destinationLock = new object();
        var buildersStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseBuilders = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int buildCount = 0;
        var builder = new DelegatingEpubBuilder(async (_, _, _, destination, _) =>
        {
            lock (destinationLock)
            {
                destinations.Add(destination);
            }

            File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
            if (Interlocked.Increment(ref buildCount) == 2)
            {
                buildersStarted.TrySetResult();
            }

            await releaseBuilders.Task;
        });
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            builder,
            new DelegatingValidator((_, _) => Task.CompletedTask),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        Task<ConversionResult> first = service.ConvertAsync(
            Request(firstSource, files.Path),
            CancellationToken.None);
        Task<ConversionResult> second = service.ConvertAsync(
            Request(secondSource, files.Path),
            CancellationToken.None);
        await buildersStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        releaseBuilders.TrySetResult();

        await Assert.ThrowsAsync<IOException>(() => Task.WhenAll(first, second));

        lock (destinationLock)
        {
            Assert.Equal(2, destinations.Count);
            Assert.Equal(2, destinations.Distinct(StringComparer.Ordinal).Count());
        }

        Assert.True(File.Exists(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Fact]
    public async Task ValidatesTheTemporaryFileBeforePublishingIt()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        bool sawTemporaryFile = false;
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            new DelegatingEpubBuilder((_, _, _, destination, _) =>
            {
                File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
                return Task.CompletedTask;
            }),
            new DelegatingValidator((temporaryPath, _) =>
            {
                sawTemporaryFile = File.Exists(temporaryPath) && !File.Exists(outputPath);
                return Task.CompletedTask;
            }),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        await service.ConvertAsync(Request(sourcePath, files.Path), CancellationToken.None);

        Assert.True(sawTemporaryFile);
        Assert.True(File.Exists(outputPath));
    }

    [Fact]
    public async Task RefusesToReplaceAnExistingFinalOutputAndPreservesItsBytes()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        byte[] existing = [0x01, 0x02, 0x03];
        File.WriteAllBytes(outputPath, existing);
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            WritingBuilder(),
            new DelegatingValidator((_, _) => Task.CompletedTask),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => service.ConvertAsync(Request(sourcePath, files.Path), CancellationToken.None));

        Assert.Contains("already exists", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(existing, File.ReadAllBytes(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BuildOrValidationFailureLeavesNoFinalOutputAndCleansTemporaryFile(
        bool failDuringBuild)
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        var builder = new DelegatingEpubBuilder((_, _, _, destination, _) =>
        {
            File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
            return failDuringBuild
                ? Task.FromException(new InvalidDataException("build failure"))
                : Task.CompletedTask;
        });
        var validator = new DelegatingValidator((_, _) => failDuringBuild
            ? Task.CompletedTask
            : Task.FromException(new InvalidDataException("validation failure")));
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            builder,
            validator,
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ConvertAsync(Request(sourcePath, files.Path), CancellationToken.None));

        Assert.False(File.Exists(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Fact]
    public async Task RejectsPublicationWhenTheSourceChangesDuringConversion()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            new DelegatingEpubBuilder((_, _, _, destination, _) =>
            {
                File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
                using var source = new FileStream(
                    sourcePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.None);
                source.Write([0x02]);
                return Task.CompletedTask;
            }),
            new DelegatingValidator((_, _) => Task.CompletedTask),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => service.ConvertAsync(Request(sourcePath, files.Path), CancellationToken.None));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Fact]
    public async Task RejectsPublicationWhenOnlyTheSourceTimestampChangesDuringConversion()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(sourcePath);
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            new DelegatingEpubBuilder((_, _, _, destination, _) =>
            {
                File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
                File.SetLastWriteTimeUtc(sourcePath, originalWriteTime.AddMinutes(1));
                return Task.CompletedTask;
            }),
            new DelegatingValidator((_, _) => Task.CompletedTask),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => service.ConvertAsync(Request(sourcePath, files.Path), CancellationToken.None));

        Assert.Contains("changed", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    [Fact]
    public async Task PropagatesCancellationAndCleansTheTemporaryFile()
    {
        using var files = new TemporaryDirectory();
        string sourcePath = files.Write("source.cbz", [0x01]);
        string outputPath = files.PathFor("output/book.epub");
        using var cancellation = new CancellationTokenSource();
        var service = new ComicConversionService(
            new DelegatingArchiveReader(),
            new DelegatingEpubBuilder((_, _, _, destination, token) =>
            {
                File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
            new DelegatingValidator((_, _) => Task.CompletedTask),
            new DelegatingOutputPathPolicy((_, _) => outputPath));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ConvertAsync(Request(sourcePath, files.Path), cancellation.Token));

        Assert.False(File.Exists(outputPath));
        Assert.Empty(TemporaryFiles(files.Path));
    }

    private static ConversionRequest Request(string sourcePath, string outputRoot) =>
        new(
            sourcePath,
            outputRoot,
            new ComicMetadata("Pilot", "Example Series", "1", "1"));

    private static IEnumerable<string> TemporaryFiles(string root) =>
        Directory.Exists(root)
            ? Directory.EnumerateFiles(root, "*.tmp", SearchOption.AllDirectories)
            : [];

    private static DelegatingEpubBuilder WritingBuilder() =>
        new((_, _, _, destination, _) =>
        {
            File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
            return Task.CompletedTask;
        });

    private sealed class DelegatingArchiveReader : IComicArchiveReader
    {
        public Task<ComicSource> ReadAsync(
            string sourcePath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new ComicSource(
                Path.GetFullPath(sourcePath),
                [new ComicPage("page.png", "image/png", 1, 1)],
                0));
        }

        public Task ExtractPageAsync(
            string sourcePath,
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("The coordinator test builder does not extract pages.");
    }

    private sealed class DelegatingEpubBuilder(
        Func<ComicSource, ComicMetadata, ReadingDirection, string, CancellationToken, Task> build)
        : IEpubBuilder
    {
        public Task BuildAsync(
            ComicSource source,
            ComicMetadata metadata,
            ReadingDirection direction,
            string destinationPath,
            CancellationToken cancellationToken) =>
            build(source, metadata, direction, destinationPath, cancellationToken);
    }

    private sealed class DelegatingValidator(
        Func<string, CancellationToken, Task> validate) : IEpubValidator
    {
        public Task ValidateAsync(string epubPath, CancellationToken cancellationToken) =>
            validate(epubPath, cancellationToken);
    }

    private sealed class DelegatingOutputPathPolicy(
        Func<string, ComicMetadata, string> outputPath) : IOutputPathPolicy
    {
        public string GetOutputPath(string outputRoot, ComicMetadata metadata) =>
            outputPath(outputRoot, metadata);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"abc-converter-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(string relativePath) =>
            System.IO.Path.Combine(Path, relativePath);

        public string Write(string relativePath, byte[] contents)
        {
            string path = PathFor(relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
