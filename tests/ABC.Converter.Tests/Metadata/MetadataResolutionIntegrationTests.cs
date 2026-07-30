using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Conversion;
using Xunit;

namespace ABC.Converter.Tests.Metadata;

public sealed class MetadataResolutionIntegrationTests
{
    [Fact]
    public async Task ConversionUsesResolvedMetadataForOutputAndEpub()
    {
        string root = Path.Combine(Path.GetTempPath(), $"abc-metadata-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string sourcePath = Path.Combine(root, "source.cbz");
        await File.WriteAllBytesAsync(sourcePath, [0x01]);
        var resolved = new ComicMetadata("Resolved title", "Resolved series", "4");
        ComicMetadata? outputMetadata = null;
        ComicMetadata? epubMetadata = null;
        string outputPath = Path.Combine(root, "book.epub");
        var service = new ComicConversionService(
            new ArchiveReader(),
            new Builder((metadata, destination) =>
            {
                epubMetadata = metadata;
                File.WriteAllBytes(destination, [0x45, 0x50, 0x55, 0x42]);
            }),
            new Validator(),
            new OutputPolicy(metadata =>
            {
                outputMetadata = metadata;
                return outputPath;
            }),
            metadataResolver: new Resolver(resolved));

        try
        {
            await service.ConvertAsync(
                new ConversionRequest(
                    sourcePath,
                    root,
                    new ComicMetadata("Fallback title", "Fallback series")),
                CancellationToken.None);

            Assert.Same(resolved, outputMetadata);
            Assert.Same(resolved, epubMetadata);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Resolver(ComicMetadata result) : IComicMetadataResolver
    {
        public Task<ComicMetadata> ResolveAsync(
            string sourcePath,
            ComicMetadata defaults,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class ArchiveReader : IComicArchiveReader
    {
        public Task<ComicSource> ReadAsync(string sourcePath, CancellationToken cancellationToken) =>
            Task.FromResult(new ComicSource(
                Path.GetFullPath(sourcePath),
                [new ComicPage("page.png", "image/png", 1, 1)],
                0));

        public Task ExtractPageAsync(
            string sourcePath,
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Builder(Action<ComicMetadata, string> build) : IEpubBuilder
    {
        public Task BuildAsync(
            ComicSource source,
            ComicMetadata metadata,
            ReadingDirection direction,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            build(metadata, destinationPath);
            return Task.CompletedTask;
        }
    }

    private sealed class Validator : IEpubValidator
    {
        public Task ValidateAsync(string epubPath, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private sealed class OutputPolicy(Func<ComicMetadata, string> getPath) : IOutputPathPolicy
    {
        public string GetOutputPath(string outputRoot, ComicMetadata metadata) =>
            getPath(metadata);
    }
}
