using System.IO;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Conversion;

namespace ABC.Converter.App.Tests.TestSupport;

internal static class ConversionHarness
{
    public static ComicConversionService CreateService(
        RecordingArchiveReader? reader = null)
    {
        return new ComicConversionService(
            reader ?? new RecordingArchiveReader(),
            new WritingEpubBuilder(),
            new ExistingFileValidator(),
            new TitleOutputPathPolicy());
    }

    private sealed class WritingEpubBuilder : IEpubBuilder
    {
        public Task BuildAsync(
            ComicSource source,
            ComicMetadata metadata,
            ReadingDirection direction,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            File.WriteAllBytes(destinationPath, [0x45, 0x50, 0x55, 0x42]);
            return Task.CompletedTask;
        }
    }

    private sealed class ExistingFileValidator : IEpubValidator
    {
        public Task ValidateAsync(
            string epubPath,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(epubPath))
            {
                throw new InvalidDataException("The EPUB was not built.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class TitleOutputPathPolicy : IOutputPathPolicy
    {
        public string GetOutputPath(string outputRoot, ComicMetadata metadata)
        {
            return Path.Combine(outputRoot, $"{metadata.Title}.epub");
        }
    }
}
