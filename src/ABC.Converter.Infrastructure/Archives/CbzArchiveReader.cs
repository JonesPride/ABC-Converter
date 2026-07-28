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
            "thumb",
            "thumbs",
            "thumbnail",
            "thumbnails"
        };

    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    public async Task<ComicSource> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                Path.GetExtension(sourcePath),
                ".cbz",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("Only CBZ files are supported.");
        }

        cancellationToken.ThrowIfCancellationRequested();

        using ZipArchive zip = ZipFile.OpenRead(sourcePath);
        var pages = new List<ComicPage>();

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

            await using Stream stream = entry.Open();
            (int width, int height) = await ReadDimensionsAsync(
                stream,
                mediaType,
                cancellationToken);
            pages.Add(new ComicPage(normalizedPath, mediaType, width, height));
        }

        pages.Sort(
            (left, right) => NaturalPathComparer.Instance.Compare(
                left.ArchivePath,
                right.ArchivePath));

        if (pages.Count == 0)
        {
            throw new InvalidDataException(
                "The CBZ contains no supported page images.");
        }

        return new ComicSource(Path.GetFullPath(sourcePath), pages, 0);
    }

    public async Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using ZipArchive zip = ZipFile.OpenRead(sourcePath);
        ZipArchiveEntry? entry = zip.GetEntry(page.ArchivePath) ??
            zip.Entries.FirstOrDefault(
                candidate => string.Equals(
                    NormalizePath(candidate.FullName),
                    page.ArchivePath,
                    StringComparison.Ordinal));

        if (entry is null)
        {
            throw new InvalidDataException(
                $"Missing archive page: {page.ArchivePath}");
        }

        await using Stream source = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
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
        return string.Equals(
                fileBaseName,
                "thumb",
                StringComparison.OrdinalIgnoreCase) ||
            string.Equals(
                fileBaseName,
                "thumbnail",
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<(int Width, int Height)> ReadDimensionsAsync(
        Stream stream,
        string mediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            ImageInfo? info = await Image.IdentifyAsync(
                stream,
                cancellationToken);

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
