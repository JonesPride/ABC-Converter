using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Conversion;
using Xunit;

namespace ABC.Converter.Tests.Conversion;

public sealed class ComicConversionSecurityTests
{
    [Fact]
    public async Task RejectsConversionBeforeBuildWhenFreeSpaceIsInsufficient()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            $"abc-converter-space-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.cbz");
        string outputPath = Path.Combine(root, "output", "book.epub");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        bool builderCalled = false;

        try
        {
            var service = new ComicConversionService(
                new StubArchiveReader(),
                new StubBuilder(() => builderCalled = true),
                new StubValidator(),
                new StubOutputPathPolicy(outputPath),
                new StubFreeSpaceProvider(0));

            IOException error = await Assert.ThrowsAsync<IOException>(
                () => service.ConvertAsync(
                    new ConversionRequest(
                        sourcePath,
                        root,
                        new ComicMetadata("Pilot", "Series", "1", "1")),
                    CancellationToken.None));

            Assert.Contains("free space", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(builderCalled);
            Assert.False(File.Exists(outputPath));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class StubArchiveReader : IComicArchiveReader
    {
        public Task<ComicSource> ReadAsync(
            string sourcePath,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ComicSource(
                Path.GetFullPath(sourcePath),
                [new ComicPage("page.png", "image/png", 1, 1)],
                0)
            {
                TotalUncompressedBytes = 1024
            });

        public Task ExtractPageAsync(
            string sourcePath,
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class StubBuilder(Action onBuild) : IEpubBuilder
    {
        public Task BuildAsync(
            ComicSource source,
            ComicMetadata metadata,
            ReadingDirection direction,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            onBuild();
            return Task.CompletedTask;
        }
    }

    private sealed class StubValidator : IEpubValidator
    {
        public Task ValidateAsync(string epubPath, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class StubOutputPathPolicy(string outputPath) : IOutputPathPolicy
    {
        public string GetOutputPath(string outputRoot, ComicMetadata metadata) => outputPath;
    }

    private sealed class StubFreeSpaceProvider(long availableBytes) : IFreeSpaceProvider
    {
        public long GetAvailableBytes(string path) => availableBytes;
    }
}
