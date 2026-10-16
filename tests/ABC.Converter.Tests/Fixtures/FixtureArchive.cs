using System.IO.Compression;
using SkiaSharp;

namespace ABC.Converter.Tests.Fixtures;

internal static class FixtureArchive
{
    private static readonly DateTimeOffset EntryTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png1x1 = EncodeImage(SKEncodedImageFormat.Png);

    private static readonly byte[] Jpeg1x1 = EncodeImage(SKEncodedImageFormat.Jpeg);

    private static readonly byte[] WebP1x1 = EncodeImage(SKEncodedImageFormat.Webp);

    public static byte[] ValidImage(string extension)
    {
        byte[] image = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => Jpeg1x1,
            ".png" => Png1x1,
            ".webp" => WebP1x1,
            _ => throw new ArgumentOutOfRangeException(
                nameof(extension),
                extension,
                "No deterministic image fixture is available for this extension.")
        };

        return image.ToArray();
    }

    public static byte[] ValidImage(
        string extension,
        int width,
        int height)
    {
        SKEncodedImageFormat encoder = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => SKEncodedImageFormat.Jpeg,
            ".png" => SKEncodedImageFormat.Png,
            ".webp" => SKEncodedImageFormat.Webp,
            _ => throw new ArgumentOutOfRangeException(
                nameof(extension),
                extension,
                "No deterministic image fixture is available for this extension.")
        };

        return EncodeImage(encoder, width, height);
    }

    public static byte[] TruncatedPng() => Png1x1[..16];

    public static byte[] UnknownImage() => [0x00, 0x01, 0x02, 0x03];

    private static byte[] EncodeImage(
        SKEncodedImageFormat encoder,
        int width = 1,
        int height = 1)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.Transparent);
        using SKImage image = SKImage.FromBitmap(bitmap);
        using SKData data = image.Encode(encoder, 100)
            ?? throw new InvalidOperationException($"Cannot encode {encoder} test image.");
        return data.ToArray();
    }

    public static string CreateCbz(params string[] entryNames)
    {
        (string EntryName, byte[] Contents)[] entries = entryNames
            .Select(name => (name, Png1x1))
            .ToArray();
        return CreateArchive(".cbz", entries);
    }

    public static string CreateCbz(
        params (string EntryName, byte[] Contents)[] entries) =>
        CreateArchive(".cbz", entries);

    public static string CreateArchive(
        string extension,
        params (string EntryName, byte[] Contents)[] entries)
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            $"{Guid.NewGuid():N}{extension}");

        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string entryName, byte[] contents) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(
                entryName,
                CompressionLevel.NoCompression);
            entry.LastWriteTime = EntryTimestamp;
            using Stream stream = entry.Open();
            stream.Write(contents);
        }

        return path;
    }
}
