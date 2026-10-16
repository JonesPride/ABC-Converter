using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;

namespace ABC.Converter.Infrastructure.Conversion;

public sealed class ComicConversionService
{
    private const long MinimumWorkingSpaceBytes = 64L * 1024 * 1024;

    private readonly IComicArchiveReader archiveReader;
    private readonly IEpubBuilder epubBuilder;
    private readonly IEpubValidator validator;
    private readonly IOutputPathPolicy outputPathPolicy;
    private readonly IFreeSpaceProvider freeSpaceProvider;
    private readonly ITemporaryFileCleaner temporaryFileCleaner;
    private readonly IComicMetadataResolver metadataResolver;
    private readonly Action<string, Exception> onTemporaryCleanupFailure;

    public ComicConversionService(
        IComicArchiveReader archiveReader,
        IEpubBuilder epubBuilder,
        IEpubValidator validator,
        IOutputPathPolicy outputPathPolicy,
        IFreeSpaceProvider? freeSpaceProvider = null,
        ITemporaryFileCleaner? temporaryFileCleaner = null,
        Action<string, Exception>? onTemporaryCleanupFailure = null,
        IComicMetadataResolver? metadataResolver = null)
    {
        this.archiveReader = archiveReader;
        this.epubBuilder = epubBuilder;
        this.validator = validator;
        this.outputPathPolicy = outputPathPolicy;
        this.freeSpaceProvider = freeSpaceProvider ?? new DriveFreeSpaceProvider();
        this.temporaryFileCleaner =
            temporaryFileCleaner ?? new FileSystemTemporaryFileCleaner();
        this.onTemporaryCleanupFailure =
            onTemporaryCleanupFailure ?? ReportTemporaryCleanupFailure;
        this.metadataResolver = metadataResolver ?? PassthroughMetadataResolver.Instance;
    }

    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken,
        Func<QueueJobState, int, Task>? checkpoint = null,
        Func<string, Task>? beforePublication = null)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string sourceFullPath = Path.GetFullPath(request.SourcePath);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(sourceFullPath);
        long sourceLength = new FileInfo(sourceFullPath).Length;
        ComicSource source = await archiveReader.ReadAsync(
            sourceFullPath,
            cancellationToken);
        if (checkpoint is not null) await checkpoint(QueueJobState.FetchingMetadata, 15);
        ComicMetadata metadata = request.MetadataIsApproved
            ? request.Metadata
            : await metadataResolver.ResolveAsync(
                sourceFullPath,
                request.Metadata,
                cancellationToken);
        ConversionRequest resolvedRequest = request with { Metadata = metadata };
        string outputPath = outputPathPolicy.GetOutputPath(
            resolvedRequest.OutputRoot,
            resolvedRequest.Metadata,
            sourceFullPath);
        string outputDirectory = Path.GetDirectoryName(outputPath)!;
        Directory.CreateDirectory(outputDirectory);
        EnsureSufficientFreeSpace(outputDirectory, source, sourceLength);

        string tempPath = Path.Combine(
            outputDirectory,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (checkpoint is not null) await checkpoint(QueueJobState.BuildingEpub, 35);
            await epubBuilder.BuildAsync(
                source,
                resolvedRequest.Metadata,
                resolvedRequest.Direction,
                tempPath,
                cancellationToken);
            if (checkpoint is not null) await checkpoint(QueueJobState.Validating, 85);
            await validator.ValidateAsync(tempPath, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            var sourceInfo = new FileInfo(sourceFullPath);
            if (sourceInfo.Length != sourceLength ||
                sourceInfo.LastWriteTimeUtc != sourceWriteTime)
            {
                throw new IOException("The source CBZ changed during conversion.");
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (checkpoint is not null) await checkpoint(QueueJobState.Publishing, 95);
            outputPath = await PublishWithoutOverwriteAsync(
                tempPath,
                outputPath,
                resolvedRequest,
                sourceFullPath,
                beforePublication,
                cancellationToken);
            return new ConversionResult(
                sourceFullPath,
                outputPath,
                source.Pages.Count);
        }
        finally
        {
            TryDeleteTemporaryFile(tempPath);
        }
    }

    private void TryDeleteTemporaryFile(string tempPath)
    {
        if (!File.Exists(tempPath))
        {
            return;
        }

        try
        {
            temporaryFileCleaner.Delete(tempPath);
        }
        catch (Exception exception) when (
            exception is IOException or
            UnauthorizedAccessException or
            NotSupportedException)
        {
            try
            {
                onTemporaryCleanupFailure(tempPath, exception);
            }
            catch (Exception reportingException)
            {
                System.Diagnostics.Trace.TraceError(
                    "Failed to report leftover temporary file '{0}': {1}",
                    tempPath,
                    reportingException);
            }
        }
    }

    private static void ReportTemporaryCleanupFailure(
        string tempPath,
        Exception exception)
    {
        System.Diagnostics.Trace.TraceWarning(
            "Could not delete temporary file '{0}'. It was left for later cleanup. {1}",
            tempPath,
            exception);
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

    private async Task<string> PublishWithoutOverwriteAsync(
        string tempPath,
        string initialOutputPath,
        ConversionRequest request,
        string sourceFullPath,
        Func<string, Task>? beforePublication,
        CancellationToken cancellationToken)
    {
        string outputPath = initialOutputPath;
        while (true)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (beforePublication is not null) await beforePublication(outputPath);
                // The durable output intent is committed before the atomic rename. If
                // interrupted now, recovery sees either this output or a missing-output failure.
                cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class PassthroughMetadataResolver : IComicMetadataResolver
    {
        public static PassthroughMetadataResolver Instance { get; } = new();

        public Task<ComicMetadata> ResolveAsync(
            string sourcePath,
            ComicMetadata defaults,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(defaults);
        }
    }
}
