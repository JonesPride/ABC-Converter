using System.IO.Compression;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Sorting;
using SixLabors.ImageSharp;

namespace ABC.Converter.Infrastructure.Archives;

public sealed class CbzArchiveReader : IComicArchiveReader
{
    private static readonly HashSet<string> ThumbnailDirectoryNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "thumb", "thumbs", "thumbnail", "thumbnails"
        };

    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    private readonly CbzArchiveLimits limits;

    public CbzArchiveReader()
        : this(CbzArchiveLimits.Default)
    {
    }

    public CbzArchiveReader(CbzArchiveLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        this.limits = limits;
    }

    public async Task<ComicSource> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        ValidateSourceExtension(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();

        using ZipArchive zip = ZipFile.OpenRead(sourcePath);
        var pageEntries = new List<(ZipArchiveEntry Entry, string Path, string MediaType)>();
        long totalUncompressedBytes = 0;

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string normalizedPath = NormalizePath(entry.FullName);
            if (ShouldIgnoreEntry(normalizedPath))
            {
                continue;
            }

            string extension = Path.GetExtension(normalizedPath);
            if (!MediaTypes.TryGetValue(extension, out string? mediaType))
            {
                continue;
            }

            if (pageEntries.Count >= limits.MaxPageCount)
            {
                throw new InvalidDataException(
                    $"The CBZ page count exceeds the limit of {limits.MaxPageCount:N0}.");
            }

            ValidateEntry(entry);
            try
            {
                totalUncompressedBytes = checked(totalUncompressedBytes + entry.Length);
            }
            catch (OverflowException exception)
            {
                throw new InvalidDataException(
                    "The CBZ total uncompressed size is invalid.",
                    exception);
            }

            if (totalUncompressedBytes > limits.MaxTotalUncompressedBytes)
            {
                throw new InvalidDataException(
                    $"The CBZ total uncompressed size exceeds the limit of " +
                    $"{limits.MaxTotalUncompressedBytes:N0} bytes.");
            }

            pageEntries.Add((entry, normalizedPath, mediaType));
        }

        if (pageEntries.Count == 0)
        {
            throw new InvalidDataException(
                "The CBZ contains no supported page images.");
        }

        var pages = new List<ComicPage>(pageEntries.Count);
        foreach ((ZipArchiveEntry entry, string normalizedPath, string mediaType) in pageEntries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using Stream stream = entry.Open();
            (int width, int height) = await ReadDimensionsAsync(
                stream,
                mediaType,
                cancellationToken);
            ValidateDimensions(normalizedPath, width, height);
            pages.Add(new ComicPage(normalizedPath, mediaType, width, height));
        }

        pages.Sort(
            (left, right) => NaturalPathComparer.Instance.Compare(
                left.ArchivePath,
                right.ArchivePath));

        return new ComicSource(Path.GetFullPath(sourcePath), pages, 0)
        {
            TotalUncompressedBytes = totalUncompressedBytes
        };
    }

    public Task<IComicArchiveSession> OpenSessionAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateSourceExtension(sourcePath);
        return Task.FromResult<IComicArchiveSession>(
            new CbzArchiveSession(this, sourcePath));
    }

    public async Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken)
    {
        await using IComicArchiveSession session = await OpenSessionAsync(
            sourcePath,
            cancellationToken);
        await session.ExtractPageAsync(
            page,
            destination,
            cancellationToken);
    }

    private void ValidateEntry(ZipArchiveEntry entry)
    {
        if (entry.CompressedLength > limits.MaxEntryCompressedBytes)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullName}' compressed size exceeds the limit of " +
                $"{limits.MaxEntryCompressedBytes:N0} bytes.");
        }

        if (entry.Length > limits.MaxEntryUncompressedBytes)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullName}' uncompressed size exceeds the limit of " +
                $"{limits.MaxEntryUncompressedBytes:N0} bytes.");
        }

        double compressionRatio = entry.CompressedLength == 0
            ? entry.Length == 0 ? 1 : double.PositiveInfinity
            : (double)entry.Length / entry.CompressedLength;
        if (compressionRatio > limits.MaxCompressionRatio)
        {
            throw new InvalidDataException(
                $"Archive entry '{entry.FullName}' compression ratio exceeds the limit of " +
                $"{limits.MaxCompressionRatio:N0}:1.");
        }
    }

    private void ValidateDimensions(string archivePath, int width, int height)
    {
        if (width > limits.MaxImageWidth || height > limits.MaxImageHeight)
        {
            throw new InvalidDataException(
                $"Image '{archivePath}' dimensions {width:N0}x{height:N0} exceed the " +
                $"limit of {limits.MaxImageWidth:N0}x{limits.MaxImageHeight:N0}.");
        }

        long pixelCount = checked((long)width * height);
        if (pixelCount > limits.MaxImagePixels)
        {
            throw new InvalidDataException(
                $"Image '{archivePath}' pixel count exceeds the limit of " +
                $"{limits.MaxImagePixels:N0}.");
        }
    }

    private static async Task CopyWithLimitAsync(
        Stream source,
        Stream destination,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81_920];
        long copied = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            copied = checked(copied + read);
            if (copied > maxBytes)
            {
                throw new InvalidDataException(
                    $"Extracted archive entry exceeds the limit of {maxBytes:N0} bytes.");
            }

            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private static bool ShouldIgnoreEntry(string normalizedPath)
    {
        if (normalizedPath.StartsWith(
                "__MACOSX/",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string[] segments = normalizedPath.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(segment => segment.StartsWith('.')))
        {
            return true;
        }

        if (segments
            .SkipLast(1)
            .Any(segment => ThumbnailDirectoryNames.Contains(segment)))
        {
            return true;
        }

        string fileBaseName = segments.Length == 0
            ? string.Empty
            : Path.GetFileNameWithoutExtension(segments[^1]);
        return string.Equals(fileBaseName, "thumb", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileBaseName, "thumbnail", StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateSourceExtension(string sourcePath)
    {
        if (!string.Equals(
                Path.GetExtension(sourcePath),
                ".cbz",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only CBZ files are supported.");
        }
    }

    private sealed class CbzArchiveSession : IComicArchiveSession
    {
        private readonly CbzArchiveReader owner;
        private readonly ZipArchive archive;
        private bool disposed;

        public CbzArchiveSession(
            CbzArchiveReader owner,
            string sourcePath)
        {
            this.owner = owner;
            archive = ZipFile.OpenRead(sourcePath);
        }

        public async Task ExtractPageAsync(
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();

            ZipArchiveEntry? entry = archive.GetEntry(page.ArchivePath) ??
                archive.Entries.FirstOrDefault(
                    candidate => string.Equals(
                        NormalizePath(candidate.FullName),
                        page.ArchivePath,
                        StringComparison.Ordinal));

            if (entry is null)
            {
                throw new InvalidDataException(
                    $"Missing archive page: {page.ArchivePath}");
            }

            owner.ValidateEntry(entry);
            await using Stream source = entry.Open();
            await CopyWithLimitAsync(
                source,
                destination,
                owner.limits.MaxEntryUncompressedBytes,
                cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            if (!disposed)
            {
                archive.Dispose();
                disposed = true;
            }

            return ValueTask.CompletedTask;
        }
    }

    private static async Task<(int Width, int Height)> ReadDimensionsAsync(
        Stream stream,
        string mediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            ImageInfo? info = await Image.IdentifyAsync(stream, cancellationToken);
            if (info is null)
            {
                throw new InvalidDataException(
                    $"Cannot identify {mediaType} image.");
            }

            return (info.Width, info.Height);
        }
        catch (ImageFormatException exception)
        {
            throw new InvalidDataException(
                $"Cannot read dimensions for {mediaType}.",
                exception);
        }
    }
}
