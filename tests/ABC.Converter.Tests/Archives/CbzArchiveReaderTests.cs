using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Archives;

public sealed class CbzArchiveReaderTests
{
    [Fact]
    public async Task ReadsSupportedImagesInNaturalOrder()
    {
        string path = FixtureArchive.CreateCbz(
            "__MACOSX/._page1.png",
            "page10.png",
            "notes.txt",
            "pages/._page3.png",
            "page2.png");

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal(
                ["page2.png", "page10.png"],
                source.Pages.Select(page => page.ArchivePath));
            Assert.Equal(0, source.CoverIndex);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IgnoresHiddenRootImages()
    {
        string path = FixtureArchive.CreateCbz(
            ".cover.png",
            "page1.png");

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal(
                ["page1.png"],
                source.Pages.Select(page => page.ArchivePath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IgnoresImagesInsideHiddenDirectories()
    {
        string path = FixtureArchive.CreateCbz(
            ".draft/page2.png",
            "chapter/.cache/page3.png",
            "page1.png");

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal(
                ["page1.png"],
                source.Pages.Select(page => page.ArchivePath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IgnoresThumbnailConventionWithoutPrefixMatching()
    {
        string path = FixtureArchive.CreateCbz(
            "thumb/page1.png",
            "THUMBS/page2.png",
            "chapter/thumbnail/page3.png",
            "chapter/THUMBNAILS/page4.png",
            "thumb.png",
            "THUMBNAIL.jpg",
            "thumb01.jpg",
            "thumbnail-page.png",
            "thumbs-up.jpg");

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal(
                ["thumb01.jpg", "thumbnail-page.png", "thumbs-up.jpg"],
                source.Pages.Select(page => page.ArchivePath));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsArchiveWithNoComicPages()
    {
        string path = FixtureArchive.CreateCbz("notes.txt");

        try
        {
            var reader = new CbzArchiveReader();

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("supported page images", error.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ExtractsRequestedPageBytes()
    {
        byte[] expected = FixtureArchive.ValidImage(".png");
        string path = FixtureArchive.CreateCbz(("pages/page1.png", expected));

        try
        {
            var reader = new CbzArchiveReader();
            var source = await reader.ReadAsync(path, CancellationToken.None);
            using var destination = new MemoryStream();

            await reader.ExtractPageAsync(
                path,
                source.Pages.Single(),
                destination,
                CancellationToken.None);

            Assert.Equal(expected, destination.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task AcceptsUppercaseCbzExtension()
    {
        string path = FixtureArchive.CreateArchive(
            ".CBZ",
            ("page1.png", FixtureArchive.ValidImage(".png")));

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Equal("page1.png", source.Pages.Single().ArchivePath);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsNonCbzInput()
    {
        string path = FixtureArchive.CreateArchive(
            ".zip",
            ("page1.png", FixtureArchive.ValidImage(".png")));

        try
        {
            var reader = new CbzArchiveReader();

            InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));

            Assert.Contains("CBZ", error.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadsDimensionsAndMediaTypesForSupportedImages()
    {
        string path = FixtureArchive.CreateCbz(
            ("page1.jpg", FixtureArchive.ValidImage(".jpg")),
            ("page2.jpeg", FixtureArchive.ValidImage(".jpeg")),
            ("page3.png", FixtureArchive.ValidImage(".png")),
            ("page4.webp", FixtureArchive.ValidImage(".webp")));

        try
        {
            var reader = new CbzArchiveReader();

            var source = await reader.ReadAsync(path, CancellationToken.None);

            Assert.Collection(
                source.Pages,
                page => AssertPage(page.ArchivePath, page.MediaType, page.Width, page.Height,
                    "page1.jpg", "image/jpeg"),
                page => AssertPage(page.ArchivePath, page.MediaType, page.Width, page.Height,
                    "page2.jpeg", "image/jpeg"),
                page => AssertPage(page.ArchivePath, page.MediaType, page.Width, page.Height,
                    "page3.png", "image/png"),
                page => AssertPage(page.ArchivePath, page.MediaType, page.Width, page.Height,
                    "page4.webp", "image/webp"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsTruncatedSupportedImage()
    {
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.TruncatedPng()));

        try
        {
            var reader = new CbzArchiveReader();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsUnknownSupportedImage()
    {
        string path = FixtureArchive.CreateCbz(
            ("page1.webp", FixtureArchive.UnknownImage()));

        try
        {
            var reader = new CbzArchiveReader();

            await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    // Encoded independently with Pillow; these do not depend on the reader's codec.
    [Theory]
    [InlineData("jpg", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0aHBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/2wBDAQkJCQwLDBgNDRgyIRwhMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjIyMjL/wAARCAALAAcDASIAAhEBAxEB/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/8QAHwEAAwEBAQEBAQEBAQAAAAAAAAECAwQFBgcICQoL/8QAtREAAgECBAQDBAcFBAQAAQJ3AAECAxEEBSExBhJBUQdhcRMiMoEIFEKRobHBCSMzUvAVYnLRChYkNOEl8RcYGRomJygpKjU2Nzg5OkNERUZHSElKU1RVVldYWVpjZGVmZ2hpanN0dXZ3eHl6goOEhYaHiImKkpOUlZaXmJmaoqOkpaanqKmqsrO0tba3uLm6wsPExcbHyMnK0tPU1dbX2Nna4uPk5ebn6Onq8vP09fb3+Pn6/9oADAMBAAIRAxEAPwDzKiiivUPLP//Z")]
    [InlineData("png", "iVBORw0KGgoAAAANSUhEUgAAAAcAAAALCAIAAAA88gD/AAAAFElEQVR4nGMUsYliwABMmELDRhQAqnIAwOG6lhIAAAAASUVORK5CYII=")]
    [InlineData("webp", "UklGRjQAAABXRUJQVlA4ICgAAACQAQCdASoHAAsAAUAmJYgCdLoAA5gA/vjqf+j64RZGX+N8QXtSYAAA")]
    public async Task ReadsIndependentNonSquareImageDimensions(string extension, string base64)
    {
        string path = FixtureArchive.CreateCbz(("page1." + extension, Convert.FromBase64String(base64)));
        try
        {
            var source = await new CbzArchiveReader().ReadAsync(path, CancellationToken.None);
            var page = Assert.Single(source.Pages);
            Assert.Equal(7, page.Width);
            Assert.Equal(11, page.Height);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertPage(
        string archivePath,
        string mediaType,
        int width,
        int height,
        string expectedArchivePath,
        string expectedMediaType)
    {
        Assert.Equal(expectedArchivePath, archivePath);
        Assert.Equal(expectedMediaType, mediaType);
        Assert.Equal(1, width);
        Assert.Equal(1, height);
    }
}
