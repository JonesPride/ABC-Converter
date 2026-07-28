using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Infrastructure.Conversion;
using ABC.Converter.Infrastructure.Files;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Acceptance;

public sealed class FoundationAcceptanceTests
{
    private static readonly XNamespace HtmlNamespace =
        "http://www.w3.org/1999/xhtml";

    private static readonly XNamespace OpfNamespace =
        "http://www.idpf.org/2007/opf";

    [Theory]
    [InlineData("uppercase-cbz")]
    [InlineData("nested-natural-order")]
    [InlineData("jpeg-pages")]
    [InlineData("webp-pages")]
    [InlineData("unicode-and-spaces")]
    [InlineData("left-to-right")]
    [InlineData("right-to-left")]
    public async Task SupportedFixturesCompleteTheValidatedFoundationPipeline(
        string fixtureName)
    {
        SuccessFixture fixture = GetSuccessFixture(fixtureName);
        using var workspace = new AcceptanceWorkspace();
        string sourcePath = workspace.CreateArchive(
            fixture.SourceFileName,
            fixture.Entries);
        byte[] sourceHash = await HashAsync(sourcePath);
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(
            sourcePath,
            CancellationToken.None);
        Assert.Equal(
            fixture.ExpectedArchivePaths,
            source.Pages.Select(page => page.ArchivePath));
        Assert.All(
            source.Pages,
            page =>
            {
                Assert.Equal(1, page.Width);
                Assert.Equal(1, page.Height);
            });
        var validator = new EpubStructureValidator();
        var service = new ComicConversionService(
            reader,
            new FixedLayoutEpubBuilder(reader),
            validator,
            new SeriesOutputPathPolicy());

        ConversionResult result = await service.ConvertAsync(
            Request(sourcePath, workspace.OutputRoot, fixture.Direction),
            CancellationToken.None);

        await validator.ValidateAsync(result.OutputPath, CancellationToken.None);
        Assert.Equal(source.Pages.Count, result.PageCount);
        Assert.True(File.Exists(result.OutputPath));
        Assert.Equal(sourceHash, await HashAsync(sourcePath));
        await AssertPackageAsync(result.OutputPath, source, fixture.Direction);
        Assert.Empty(workspace.TemporaryOutputArtifacts());
    }

    [Fact]
    public async Task CorruptZipInputFailsWithoutPublishingArtifacts()
    {
        using var workspace = new AcceptanceWorkspace();
        string sourcePath = workspace.Write(
            "corrupt.cbz",
            Encoding.UTF8.GetBytes("not a ZIP archive"));
        byte[] sourceHash = await HashAsync(sourcePath);
        var reader = new CbzArchiveReader();
        ComicConversionService service = CreateService(reader);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ConvertAsync(
                Request(
                    sourcePath,
                    workspace.OutputRoot,
                    ReadingDirection.LeftToRight),
                CancellationToken.None));

        Assert.Equal(sourceHash, await HashAsync(sourcePath));
        Assert.Empty(workspace.OutputArtifacts());
    }

    [Fact]
    public async Task ArchiveWithoutSupportedPagesFailsWithoutPublishingArtifacts()
    {
        using var workspace = new AcceptanceWorkspace();
        string sourcePath = workspace.CreateArchive(
            "no-supported-pages.cbz",
            [("notes.txt", Encoding.UTF8.GetBytes("no comic pages"))]);
        byte[] sourceHash = await HashAsync(sourcePath);
        var reader = new CbzArchiveReader();
        ComicConversionService service = CreateService(reader);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => service.ConvertAsync(
                Request(
                    sourcePath,
                    workspace.OutputRoot,
                    ReadingDirection.LeftToRight),
                CancellationToken.None));

        Assert.Contains(
            "supported page images",
            error.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(sourceHash, await HashAsync(sourcePath));
        Assert.Empty(workspace.OutputArtifacts());
    }

    [Fact]
    public async Task CancellationAfterTemporaryBuildStartsRemovesAllArtifacts()
    {
        using var workspace = new AcceptanceWorkspace();
        byte[] page = FixtureArchive.ValidImage(".png");
        string sourcePath = workspace.CreateArchive(
            "cancel-me.cbz",
            Enumerable
                .Range(1, 250)
                .Select(index => ($"page{index}.png", page))
                .ToArray());
        byte[] sourceHash = await HashAsync(sourcePath);
        using var cancellation = new CancellationTokenSource();
        var reader = new CbzArchiveReader();
        ComicConversionService service = CreateService(reader);
        Directory.CreateDirectory(workspace.OutputRoot);

        Task<ConversionResult> conversion = service.ConvertAsync(
            Request(
                sourcePath,
                workspace.OutputRoot,
                ReadingDirection.LeftToRight),
            cancellation.Token);
        try
        {
            await WaitForTemporaryBuildAsync(workspace, conversion);
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => conversion);
        }
        finally
        {
            if (!conversion.IsCompleted)
            {
                cancellation.Cancel();
                try
                {
                    await conversion;
                }
                catch (OperationCanceledException)
                {
                }
            }
        }

        Assert.Equal(sourceHash, await HashAsync(sourcePath));
        Assert.Empty(workspace.OutputArtifacts());
    }

    private static ComicConversionService CreateService(
        IComicArchiveReader reader) =>
        new(
            reader,
            new FixedLayoutEpubBuilder(reader),
            new EpubStructureValidator(),
            new SeriesOutputPathPolicy());

    private static ConversionRequest Request(
        string sourcePath,
        string outputRoot,
        ReadingDirection direction) =>
        new(
            sourcePath,
            outputRoot,
            new ComicMetadata(
                "Foundation Acceptance",
                "ABC Test Series",
                "1",
                "1"),
            direction);

    private static SuccessFixture GetSuccessFixture(string fixtureName) =>
        fixtureName switch
        {
            "uppercase-cbz" => new(
                "UPPERCASE.CBZ",
                ReadingDirection.LeftToRight,
                [("page1.png", FixtureArchive.ValidImage(".png"))],
                ["page1.png"]),
            "nested-natural-order" => new(
                "nested.cbz",
                ReadingDirection.LeftToRight,
                [
                    ("chapter/page10.jpg", FixtureArchive.ValidImage(".jpg")),
                    ("chapter/page2.png", FixtureArchive.ValidImage(".png"))
                ],
                ["chapter/page2.png", "chapter/page10.jpg"]),
            "jpeg-pages" => new(
                "jpeg.cbz",
                ReadingDirection.LeftToRight,
                [
                    ("page1.jpg", FixtureArchive.ValidImage(".jpg")),
                    ("page2.jpeg", FixtureArchive.ValidImage(".jpeg"))
                ],
                ["page1.jpg", "page2.jpeg"]),
            "webp-pages" => new(
                "webp.cbz",
                ReadingDirection.LeftToRight,
                [("page1.webp", FixtureArchive.ValidImage(".webp"))],
                ["page1.webp"]),
            "unicode-and-spaces" => new(
                "漫画 サンプル.cbz",
                ReadingDirection.LeftToRight,
                [
                    (
                        "章 1/ページ 1.png",
                        FixtureArchive.ValidImage(".png"))
                ],
                ["章 1/ページ 1.png"]),
            "left-to-right" => new(
                "left-to-right.cbz",
                ReadingDirection.LeftToRight,
                [("page1.png", FixtureArchive.ValidImage(".png"))],
                ["page1.png"]),
            "right-to-left" => new(
                "right-to-left.cbz",
                ReadingDirection.RightToLeft,
                [("page1.webp", FixtureArchive.ValidImage(".webp"))],
                ["page1.webp"]),
            _ => throw new ArgumentOutOfRangeException(
                nameof(fixtureName),
                fixtureName,
                "Unknown foundation acceptance fixture.")
        };

    private static async Task AssertPackageAsync(
        string epubPath,
        ComicSource source,
        ReadingDirection direction)
    {
        using ZipArchive packageArchive = ZipFile.OpenRead(epubPath);
        XDocument package = await ReadXmlAsync(
            GetEntry(packageArchive, "EPUB/package.opf"));
        XElement manifest = Assert.Single(
            package.Descendants(OpfNamespace + "manifest"));
        XElement spine = Assert.Single(
            package.Descendants(OpfNamespace + "spine"));
        XElement[] imageItems = manifest
            .Elements(OpfNamespace + "item")
            .Where(item => ((string?)item.Attribute("media-type"))
                ?.StartsWith("image/", StringComparison.Ordinal) == true)
            .ToArray();

        Assert.Equal(source.Pages.Count, imageItems.Length);
        Assert.Equal(
            source.Pages.Count,
            spine.Elements(OpfNamespace + "itemref").Count());
        Assert.Equal(
            direction == ReadingDirection.RightToLeft ? "rtl" : "ltr",
            (string?)spine.Attribute("page-progression-direction"));

        XElement cover = Assert.Single(
            imageItems,
            item => PropertyTokens(item).Contains(
                "cover-image",
                StringComparer.Ordinal));
        Assert.Equal(
            $"image-{source.CoverIndex + 1:D4}",
            (string?)cover.Attribute("id"));

        using ZipArchive sourceArchive = ZipFile.OpenRead(source.SourcePath);

        for (int index = 0; index < source.Pages.Count; index++)
        {
            ComicPage expectedPage = source.Pages[index];
            int pageNumber = index + 1;
            string number = pageNumber.ToString("D4");
            XElement imageItem = Assert.Single(
                imageItems,
                item => (string?)item.Attribute("id") == $"image-{number}");
            Assert.Equal(
                expectedPage.MediaType,
                (string?)imageItem.Attribute("media-type"));
            Assert.Equal(
                $"images/page-{number}{EpubImageExtension(expectedPage.MediaType)}",
                (string?)imageItem.Attribute("href"));
            Assert.Equal(
                await ReadBytesAsync(
                    GetEntry(sourceArchive, expectedPage.ArchivePath)),
                await ReadBytesAsync(
                    GetEntry(
                        packageArchive,
                        $"EPUB/images/page-{number}" +
                        EpubImageExtension(expectedPage.MediaType))));

            XDocument page = await ReadXmlAsync(
                GetEntry(
                    packageArchive,
                    $"EPUB/pages/page-{number}.xhtml"));
            XElement viewport = Assert.Single(
                page.Descendants(HtmlNamespace + "meta"),
                element => (string?)element.Attribute("name") == "viewport");
            Assert.Equal(
                $"width={expectedPage.Width},height={expectedPage.Height}",
                (string?)viewport.Attribute("content"));
        }
    }

    private static string EpubImageExtension(string mediaType) =>
        mediaType switch
        {
            "image/jpeg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            _ => throw new ArgumentOutOfRangeException(
                nameof(mediaType),
                mediaType,
                "Unsupported fixture media type.")
        };

    private static IEnumerable<string> PropertyTokens(XElement item) =>
        ((string?)item.Attribute("properties") ?? string.Empty)
            .Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);

    private static ZipArchiveEntry GetEntry(ZipArchive archive, string path) =>
        archive.GetEntry(path) ??
        throw new Xunit.Sdk.XunitException($"Missing EPUB entry: {path}");

    private static async Task<XDocument> ReadXmlAsync(ZipArchiveEntry entry)
    {
        await using Stream stream = entry.Open();
        return await XDocument.LoadAsync(
            stream,
            LoadOptions.None,
            CancellationToken.None);
    }

    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry)
    {
        await using Stream stream = entry.Open();
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task<byte[]> HashAsync(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static async Task WaitForTemporaryBuildAsync(
        AcceptanceWorkspace workspace,
        Task<ConversionResult> conversion)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            if (workspace.TemporaryOutputArtifacts().Any())
            {
                return;
            }

            if (conversion.IsCompleted)
            {
                _ = await conversion;
                throw new Xunit.Sdk.XunitException(
                    "Conversion completed before cancellation could be requested.");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(1));
        }

        throw new TimeoutException(
            "Timed out waiting for the conversion temporary file.");
    }

    private sealed record SuccessFixture(
        string SourceFileName,
        ReadingDirection Direction,
        (string EntryName, byte[] Contents)[] Entries,
        string[] ExpectedArchivePaths);

    private sealed class AcceptanceWorkspace : IDisposable
    {
        public AcceptanceWorkspace()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                $"abc-converter-acceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public string OutputRoot => Path.Combine(Root, "Output");

        public string CreateArchive(
            string fileName,
            params (string EntryName, byte[] Contents)[] entries)
        {
            string generated = FixtureArchive.CreateArchive(
                Path.GetExtension(fileName),
                entries);
            string destination = Path.Combine(Root, fileName);
            try
            {
                File.Move(generated, destination);
                return destination;
            }
            finally
            {
                if (File.Exists(generated))
                {
                    File.Delete(generated);
                }
            }
        }

        public string Write(string fileName, byte[] contents)
        {
            string path = Path.Combine(Root, fileName);
            File.WriteAllBytes(path, contents);
            return path;
        }

        public IEnumerable<string> OutputArtifacts() =>
            Directory.Exists(OutputRoot)
                ? Directory.EnumerateFiles(
                    OutputRoot,
                    "*",
                    SearchOption.AllDirectories)
                : [];

        public IEnumerable<string> TemporaryOutputArtifacts() =>
            Directory.Exists(OutputRoot)
                ? Directory.EnumerateFiles(
                    OutputRoot,
                    "*.tmp",
                    SearchOption.AllDirectories)
                : [];

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }
}
