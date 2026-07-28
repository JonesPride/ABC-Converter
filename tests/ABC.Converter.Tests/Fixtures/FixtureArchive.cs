using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace ABC.Converter.Tests.Fixtures;

internal static class FixtureArchive
{
    private static readonly DateTimeOffset EntryTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png1x1 = EncodeImage(new PngEncoder());

    private static readonly byte[] Jpeg1x1 = EncodeImage(new JpegEncoder());

    private static readonly byte[] WebP1x1 = EncodeImage(new WebpEncoder());

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
        IImageEncoder encoder = extension.ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => new JpegEncoder(),
            ".png" => new PngEncoder(),
            ".webp" => new WebpEncoder(),
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
        IImageEncoder encoder,
        int width = 1,
        int height = 1)
    {
        using var image = new Image<Rgba32>(width, height);
        using var stream = new MemoryStream();
        image.Save(stream, encoder);
        return stream.ToArray();
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
