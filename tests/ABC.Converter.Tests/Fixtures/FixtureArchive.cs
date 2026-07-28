using System.IO.Compression;

namespace ABC.Converter.Tests.Fixtures;

internal static class FixtureArchive
{
    private static readonly DateTimeOffset EntryTimestamp =
        new(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static readonly byte[] Png1x1 =
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z7gAAAABJRU5ErkJggg==");

    private static readonly byte[] Jpeg1x1 =
        Convert.FromBase64String(
            "/9j/4AAQSkZJRgABAQAAAAAAAAD/2wBDAAYEBQYFBAYGBQYHBwYIChAKCgkJChQODwwQFxQYGBcUFhYaHSUfGhsjHBYWICwgIyYnKSopGR8tMC0oMCUoKSj/wAALCAABAAEBAREA/8QAFAABAAAAAAAAAAAAAAAAAAAACP/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AVL//2Q==");

    private static readonly byte[] WebP1x1 =
        Convert.FromBase64String(
            "UklGRh4AAABXRUJQVlA4TBEAAAAvAAAAAAfQ//73v/+BiOh/AAA=");

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

    public static byte[] TruncatedPng() => Png1x1[..16];

    public static byte[] UnknownImage() => [0x00, 0x01, 0x02, 0x03];

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
