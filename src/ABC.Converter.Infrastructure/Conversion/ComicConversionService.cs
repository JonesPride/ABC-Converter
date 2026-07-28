using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Conversion;

public sealed class ComicConversionService(
    IComicArchiveReader archiveReader,
    IEpubBuilder epubBuilder,
    IEpubValidator validator,
    IOutputPathPolicy outputPathPolicy)
{
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string sourceFullPath = Path.GetFullPath(request.SourcePath);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(sourceFullPath);
        long sourceLength = new FileInfo(sourceFullPath).Length;
        ComicSource source = await archiveReader.ReadAsync(
            sourceFullPath,
            cancellationToken);
        string outputPath = outputPathPolicy.GetOutputPath(
            request.OutputRoot,
            request.Metadata);
        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);

        string tempPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await epubBuilder.BuildAsync(
                source,
                request.Metadata,
                request.Direction,
                tempPath,
                cancellationToken);
            await validator.ValidateAsync(tempPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var sourceInfo = new FileInfo(sourceFullPath);
            if (sourceInfo.Length != sourceLength ||
                sourceInfo.LastWriteTimeUtc != sourceWriteTime)
            {
                throw new IOException("The source CBZ changed during conversion.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(outputPath))
            {
                throw new IOException($"Output already exists: {outputPath}");
            }

            File.Move(tempPath, outputPath);
            return new ConversionResult(
                sourceFullPath,
                outputPath,
                source.Pages.Count);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }
}
