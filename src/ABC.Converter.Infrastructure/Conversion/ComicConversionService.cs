using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Conversion;

public sealed class ComicConversionService
{
    private const long MinimumWorkingSpaceBytes = 64L * 1024 * 1024;

    private readonly IComicArchiveReader archiveReader;
    private readonly IEpubBuilder epubBuilder;
    private readonly IEpubValidator validator;
    private readonly IOutputPathPolicy outputPathPolicy;
    private readonly IFreeSpaceProvider freeSpaceProvider;

    public ComicConversionService(
        IComicArchiveReader archiveReader,
        IEpubBuilder epubBuilder,
        IEpubValidator validator,
        IOutputPathPolicy outputPathPolicy,
        IFreeSpaceProvider? freeSpaceProvider = null)
    {
        this.archiveReader = archiveReader;
        this.epubBuilder = epubBuilder;
        this.validator = validator;
        this.outputPathPolicy = outputPathPolicy;
        this.freeSpaceProvider = freeSpaceProvider ?? new DriveFreeSpaceProvider();
    }

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
            request.Metadata,
            sourceFullPath);
        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        EnsureSufficientFreeSpace(outputDirectory, source, sourceLength);

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

            outputPath = PublishWithoutOverwrite(
                tempPath,
                outputPath,
                request,
                sourceFullPath);
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

    private void EnsureSufficientFreeSpace(
        string outputDirectory,
        ComicSource source,
        long sourceLength)
    {
        long estimatedPayload = Math.Max(source.TotalUncompressedBytes, sourceLength);
        long proportionalReserve = estimatedPayload / 20;
        long workingReserve = Math.Max(MinimumWorkingSpaceBytes, proportionalReserve);
        long requiredBytes;
        try
        {
            requiredBytes = checked(estimatedPayload + workingReserve);
        }
        catch (OverflowException exception)
        {
            throw new IOException("The estimated output size is too large.", exception);
        }

        long availableBytes = freeSpaceProvider.GetAvailableBytes(outputDirectory);
        if (availableBytes < requiredBytes)
        {
            throw new IOException(
                $"Insufficient free space for conversion. Required: {requiredBytes:N0} bytes; " +
                $"available: {availableBytes:N0} bytes.");
        }
    }

    private string PublishWithoutOverwrite(
        string tempPath,
        string initialOutputPath,
        ConversionRequest request,
        string sourceFullPath)
    {
        string outputPath = initialOutputPath;
        while (true)
        {
            try
            {
                File.Move(tempPath, outputPath);
                return outputPath;
            }
            catch (IOException) when (File.Exists(outputPath))
            {
                string nextPath = outputPathPolicy.GetOutputPath(
                    request.OutputRoot,
                    request.Metadata,
                    sourceFullPath);
                if (string.Equals(nextPath, outputPath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException($"Output already exists: {outputPath}");
                }

                outputPath = nextPath;
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            }
        }
    }

}
