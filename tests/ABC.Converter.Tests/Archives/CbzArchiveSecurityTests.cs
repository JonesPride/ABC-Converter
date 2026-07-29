using System.IO.Compression;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Archives;

public sealed class CbzArchiveSecurityTests
{
    [Fact]
    public async Task RejectsMorePagesThanConfigured()
    {
        string path = FixtureArchive.CreateCbz("page1.png", "page2.png");

        try
        {
            var reader = new CbzArchiveReader(new CbzArchiveLimits { MaxPageCount = 1 });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("page count", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsOversizedUncompressedEntryBeforeImageDecoding()
    {
        string path = CreateArchive(
            ("page.png", new byte[1024], CompressionLevel.NoCompression));

        try
        {
            var reader = new CbzArchiveReader(
                new CbzArchiveLimits { MaxEntryUncompressedBytes = 512 });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("uncompressed size", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsOversizedCompressedEntry()
    {
        string path = CreateArchive(
            ("page.png", new byte[1024], CompressionLevel.NoCompression));

        try
        {
            var reader = new CbzArchiveReader(
                new CbzArchiveLimits { MaxEntryCompressedBytes = 512 });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("compressed size", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsExcessiveTotalUncompressedBytes()
    {
        string path = CreateArchive(
            ("page1.png", new byte[400], CompressionLevel.NoCompression),
            ("page2.png", new byte[400], CompressionLevel.NoCompression));

        try
        {
            var reader = new CbzArchiveReader(
                new CbzArchiveLimits
                {
                    MaxEntryUncompressedBytes = 500,
                    MaxTotalUncompressedBytes = 700
                });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("total uncompressed", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsSuspiciousCompressionRatioBeforeImageDecoding()
    {
        string path = CreateArchive(
            ("page.png", new byte[64 * 1024], CompressionLevel.SmallestSize));

        try
        {
            var reader = new CbzArchiveReader(
                new CbzArchiveLimits
                {
                    MaxEntryUncompressedBytes = 128 * 1024,
                    MaxCompressionRatio = 10
                });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("compression ratio", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsImageWhosePixelCountExceedsConfiguredLimit()
    {
        string path = FixtureArchive.CreateCbz(
            ("page.png", FixtureArchive.ValidImage(".png", 20, 20)));

        try
        {
            var reader = new CbzArchiveReader(
                new CbzArchiveLimits
                {
                    MaxImageWidth = 100,
                    MaxImageHeight = 100,
                    MaxImagePixels = 399
                });

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("pixel", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string CreateArchive(
        params (string Name, byte[] Contents, CompressionLevel Compression)[] entries)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach ((string name, byte[] contents, CompressionLevel compression) in entries)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name, compression);
            using Stream stream = entry.Open();
            stream.Write(contents);
        }

        return path;
    }
}
