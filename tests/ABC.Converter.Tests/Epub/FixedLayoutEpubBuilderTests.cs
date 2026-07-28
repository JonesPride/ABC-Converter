using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Epub;

public sealed class FixedLayoutEpubBuilderTests
{
    private static readonly XNamespace ContainerNamespace =
        "urn:oasis:names:tc:opendocument:xmlns:container";

    private static readonly XNamespace DublinCoreNamespace =
        "http://purl.org/dc/elements/1.1/";

    private static readonly XNamespace EpubNamespace =
        "http://www.idpf.org/2007/ops";

    private static readonly XNamespace HtmlNamespace =
        "http://www.w3.org/1999/xhtml";

    private static readonly XNamespace OpfNamespace =
        "http://www.idpf.org/2007/opf";

    [Fact]
    public async Task BuildsRequiredEntriesWithUncompressedMimetypeFirst()
    {
        using var files = new TemporaryFiles();
        byte[] png = FixtureArchive.ValidImage(".png");
        byte[] jpeg = FixtureArchive.ValidImage(".jpg");
        string cbz = files.Add(FixtureArchive.CreateCbz(
            ("page1.png", png),
            ("page2.jpg", jpeg)));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);

        await builder.BuildAsync(
            source,
            new ComicMetadata("Issue 1", "Example Series", "1"),
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        Assert.Equal(
            [
                "mimetype",
                "META-INF/container.xml",
                "META-INF/com.apple.ibooks.display-options.xml",
                "EPUB/images/page-0001.png",
                "EPUB/pages/page-0001.xhtml",
                "EPUB/images/page-0002.jpg",
                "EPUB/pages/page-0002.xhtml",
                "EPUB/nav.xhtml",
                "EPUB/package.opf"
            ],
            zip.Entries.Select(entry => entry.FullName));
        Assert.Equal(
            "application/epub+zip",
            await ReadTextAsync(GetEntry(zip, "mimetype")));

        AssertFirstLocalEntryIsStoredMimetype(epub);
    }

    [Fact]
    public async Task WritesValidLeftToRightFixedLayoutXml()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("page2.jpg", FixtureArchive.ValidImage(".jpg"))));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);
        var metadata = new ComicMetadata(
            "Issue 7",
            "Example Series",
            "7",
            Language: "en-GB");

        await builder.BuildAsync(
            source,
            metadata,
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        XDocument container = await ReadXmlAsync(
            GetEntry(zip, "META-INF/container.xml"));
        XElement rootfile = Assert.Single(
            container
                .Descendants(ContainerNamespace + "rootfile"));
        Assert.Equal("EPUB/package.opf", (string?)rootfile.Attribute("full-path"));
        Assert.Equal(
            "application/oebps-package+xml",
            (string?)rootfile.Attribute("media-type"));

        XDocument displayOptions = await ReadXmlAsync(
            GetEntry(zip, "META-INF/com.apple.ibooks.display-options.xml"));
        XElement platform = Assert.Single(
            displayOptions.Descendants("platform"));
        Assert.Equal("*", (string?)platform.Attribute("name"));
        Assert.Equal(
            "true",
            FindOption(platform, "fixed-layout").Value);
        Assert.Equal(
            "false",
            FindOption(platform, "open-to-spread").Value);

        XDocument package = await ReadXmlAsync(
            GetEntry(zip, "EPUB/package.opf"));
        XElement packageRoot = Assert.IsType<XElement>(package.Root);
        Assert.Equal(OpfNamespace + "package", packageRoot.Name);
        Assert.Equal("3.0", (string?)packageRoot.Attribute("version"));
        Assert.Equal(
            "pub-id",
            (string?)packageRoot.Attribute("unique-identifier"));

        XElement metadataElement = Assert.Single(
            packageRoot.Elements(OpfNamespace + "metadata"));
        XElement identifier = Assert.Single(
            metadataElement.Elements(DublinCoreNamespace + "identifier"));
        Assert.Equal("pub-id", (string?)identifier.Attribute("id"));
        Assert.StartsWith("urn:uuid:", identifier.Value);
        Assert.True(Guid.TryParse(identifier.Value["urn:uuid:".Length..], out _));
        Assert.Equal(
            metadata.Title,
            Assert.Single(
                metadataElement.Elements(DublinCoreNamespace + "title")).Value);
        Assert.Equal(
            metadata.Language,
            Assert.Single(
                metadataElement.Elements(DublinCoreNamespace + "language")).Value);
        Assert.Equal(
            metadata.Series,
            FindMetadata(metadataElement, "belongs-to-collection").Value);
        Assert.Equal(
            "series",
            FindMetadata(metadataElement, "collection-type").Value);
        Assert.Equal(
            "pre-paginated",
            FindMetadata(metadataElement, "rendition:layout").Value);
        Assert.Equal(
            "auto",
            FindMetadata(metadataElement, "rendition:orientation").Value);
        Assert.Equal(
            "auto",
            FindMetadata(metadataElement, "rendition:spread").Value);

        XElement manifest = Assert.Single(
            packageRoot.Elements(OpfNamespace + "manifest"));
        AssertManifestItem(
            manifest,
            "nav",
            "nav.xhtml",
            "application/xhtml+xml",
            "nav");
        AssertManifestItem(
            manifest,
            "image-0001",
            "images/page-0001.png",
            "image/png",
            "cover-image");
        AssertManifestItem(
            manifest,
            "page-0001",
            "pages/page-0001.xhtml",
            "application/xhtml+xml");
        AssertManifestItem(
            manifest,
            "image-0002",
            "images/page-0002.jpg",
            "image/jpeg");
        AssertManifestItem(
            manifest,
            "page-0002",
            "pages/page-0002.xhtml",
            "application/xhtml+xml");

        XElement spine = Assert.Single(
            packageRoot.Elements(OpfNamespace + "spine"));
        Assert.Equal(
            "ltr",
            (string?)spine.Attribute("page-progression-direction"));
        Assert.Equal(
            ["page-0001", "page-0002"],
            spine
                .Elements(OpfNamespace + "itemref")
                .Select(item => (string?)item.Attribute("idref")));

        XDocument navigation = await ReadXmlAsync(
            GetEntry(zip, "EPUB/nav.xhtml"));
        Assert.Equal(HtmlNamespace + "html", navigation.Root?.Name);
        Assert.Equal(
            metadata.Title,
            Assert.Single(
                navigation.Descendants(HtmlNamespace + "title")).Value);
        XElement navigationElement = Assert.Single(
            navigation.Descendants(HtmlNamespace + "nav"));
        Assert.Equal(
            "toc",
            (string?)navigationElement.Attribute(EpubNamespace + "type"));
        Assert.Equal(
            ["pages/page-0001.xhtml", "pages/page-0002.xhtml"],
            navigationElement
                .Descendants(HtmlNamespace + "a")
                .Select(link => (string?)link.Attribute("href")));
        Assert.Equal(
            ["Page 1", "Page 2"],
            navigationElement
                .Descendants(HtmlNamespace + "a")
                .Select(link => link.Value));

        AssertPageDocument(
            await ReadXmlAsync(GetEntry(zip, "EPUB/pages/page-0001.xhtml")),
            "width=1,height=1",
            "../images/page-0001.png",
            "Page 1");
        AssertPageDocument(
            await ReadXmlAsync(GetEntry(zip, "EPUB/pages/page-0002.xhtml")),
            "width=1,height=1",
            "../images/page-0002.jpg",
            "Page 2");
    }

    [Fact]
    public async Task WritesRightToLeftPageProgression()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz("page1.png"));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);

        await builder.BuildAsync(
            source,
            new ComicMetadata("Issue 1", "Example Series"),
            ReadingDirection.RightToLeft,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        XDocument package = await ReadXmlAsync(
            GetEntry(zip, "EPUB/package.opf"));
        XElement spine = Assert.Single(
            package.Descendants(OpfNamespace + "spine"));
        Assert.Equal(
            "rtl",
            (string?)spine.Attribute("page-progression-direction"));
    }

    [Fact]
    public async Task MarksTheSelectedSourcePageAsTheCover()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("page2.jpg", FixtureArchive.ValidImage(".jpg"))));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource scanned = await reader.ReadAsync(cbz, CancellationToken.None);
        ComicSource source = scanned with { CoverIndex = 1 };
        var builder = new FixedLayoutEpubBuilder(reader);

        await builder.BuildAsync(
            source,
            new ComicMetadata("Issue 1", "Example Series"),
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        XDocument package = await ReadXmlAsync(
            GetEntry(zip, "EPUB/package.opf"));
        XElement manifest = Assert.Single(
            package.Descendants(OpfNamespace + "manifest"));
        XElement cover = Assert.Single(
            manifest
                .Elements(OpfNamespace + "item")
                .Where(item =>
                    (string?)item.Attribute("properties") == "cover-image"));
        Assert.Equal("image-0002", (string?)cover.Attribute("id"));
        Assert.Equal(
            "images/page-0002.jpg",
            (string?)cover.Attribute("href"));
    }

    [Fact]
    public async Task PreservesEverySourceImageByteForByte()
    {
        using var files = new TemporaryFiles();
        byte[] jpg = FixtureArchive.ValidImage(".jpg");
        byte[] jpeg = FixtureArchive.ValidImage(".jpeg");
        byte[] png = FixtureArchive.ValidImage(".png");
        byte[] webp = FixtureArchive.ValidImage(".webp");
        string cbz = files.Add(FixtureArchive.CreateCbz(
            ("page1.jpg", jpg),
            ("page2.jpeg", jpeg),
            ("page3.png", png),
            ("page4.webp", webp)));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);

        await builder.BuildAsync(
            source,
            new ComicMetadata("Issue 1", "Example Series"),
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        Assert.Equal(
            jpg,
            await ReadBytesAsync(GetEntry(zip, "EPUB/images/page-0001.jpg")));
        Assert.Equal(
            jpeg,
            await ReadBytesAsync(GetEntry(zip, "EPUB/images/page-0002.jpg")));
        Assert.Equal(
            png,
            await ReadBytesAsync(GetEntry(zip, "EPUB/images/page-0003.png")));
        Assert.Equal(
            webp,
            await ReadBytesAsync(GetEntry(zip, "EPUB/images/page-0004.webp")));
    }

    [Fact]
    public async Task EscapesMetadataWhilePreservingItsTextValues()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz("page1.png"));
        string epub = files.PathFor("book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);
        var metadata = new ComicMetadata(
            "Rock & Roll <Special>",
            "Heroes & Villains <Series>");

        await builder.BuildAsync(
            source,
            metadata,
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        using ZipArchive zip = ZipFile.OpenRead(epub);
        string packageText = await ReadTextAsync(
            GetEntry(zip, "EPUB/package.opf"));
        string navigationText = await ReadTextAsync(
            GetEntry(zip, "EPUB/nav.xhtml"));
        Assert.Contains("Rock &amp; Roll &lt;Special&gt;", packageText);
        Assert.Contains("Heroes &amp; Villains &lt;Series&gt;", packageText);
        Assert.Contains("Rock &amp; Roll &lt;Special&gt;", navigationText);

        XDocument package = XDocument.Parse(packageText);
        XDocument navigation = XDocument.Parse(navigationText);
        Assert.Equal(
            metadata.Title,
            Assert.Single(
                package.Descendants(DublinCoreNamespace + "title")).Value);
        Assert.Equal(
            metadata.Series,
            FindMetadata(
                Assert.Single(
                    package.Descendants(OpfNamespace + "metadata")),
                "belongs-to-collection").Value);
        Assert.Equal(
            metadata.Title,
            Assert.Single(
                navigation.Descendants(HtmlNamespace + "title")).Value);
    }

    [Fact]
    public async Task CancellationRemovesThePartiallyBuiltDestination()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz(
            "page1.png",
            "page2.png"));
        string epub = files.PathFor("book.epub");
        var innerReader = new CbzArchiveReader();
        ComicSource source = await innerReader.ReadAsync(
            cbz,
            CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var reader = new CancelAfterFirstExtractionArchiveReader(
            innerReader,
            cancellation);
        var builder = new FixedLayoutEpubBuilder(reader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => builder.BuildAsync(
                source,
                new ComicMetadata("Issue 1", "Example Series"),
                ReadingDirection.LeftToRight,
                epub,
                cancellation.Token));

        Assert.Equal(1, reader.ExtractionCount);
        Assert.False(File.Exists(epub));
    }

    [Fact]
    public async Task CreatesMissingDestinationDirectories()
    {
        using var files = new TemporaryFiles();
        string cbz = files.Add(FixtureArchive.CreateCbz("page1.png"));
        string destinationDirectory = files.PathFor(
            Path.Combine("missing", "nested"));
        string epub = Path.Combine(destinationDirectory, "book.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);

        await builder.BuildAsync(
            source,
            new ComicMetadata("Issue 1", "Example Series"),
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        Assert.True(Directory.Exists(destinationDirectory));
        Assert.True(File.Exists(epub));
    }

    private static void AssertFirstLocalEntryIsStoredMimetype(string epubPath)
    {
        byte[] bytes = File.ReadAllBytes(epubPath);
        Assert.True(bytes.Length >= 30);
        Assert.Equal(0x04034b50u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        Assert.Equal(
            0,
            BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8, 2)));

        int fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(26, 2));
        string fileName = Encoding.UTF8.GetString(bytes, 30, fileNameLength);
        Assert.Equal("mimetype", fileName);
    }

    private static void AssertManifestItem(
        XElement manifest,
        string id,
        string href,
        string mediaType,
        string? properties = null)
    {
        XElement item = Assert.Single(
            manifest
                .Elements(OpfNamespace + "item")
                .Where(element => (string?)element.Attribute("id") == id));
        Assert.Equal(href, (string?)item.Attribute("href"));
        Assert.Equal(mediaType, (string?)item.Attribute("media-type"));
        Assert.Equal(properties, (string?)item.Attribute("properties"));
    }

    private static void AssertPageDocument(
        XDocument page,
        string viewport,
        string imageSource,
        string alternativeText)
    {
        Assert.Equal(HtmlNamespace + "html", page.Root?.Name);
        XElement viewportElement = Assert.Single(
            page
                .Descendants(HtmlNamespace + "meta")
                .Where(element =>
                    (string?)element.Attribute("name") == "viewport"));
        Assert.Equal(
            viewport,
            (string?)viewportElement.Attribute("content"));
        XElement image = Assert.Single(
            page.Descendants(HtmlNamespace + "img"));
        Assert.Equal(imageSource, (string?)image.Attribute("src"));
        Assert.Equal(alternativeText, (string?)image.Attribute("alt"));
        string style = Assert.Single(
            page.Descendants(HtmlNamespace + "style")).Value;
        Assert.Contains("margin:0", style);
        Assert.Contains("width:100%", style);
        Assert.Contains("height:100%", style);
        Assert.Contains("object-fit:contain", style);
    }

    private static XElement FindMetadata(XElement metadata, string property) =>
        Assert.Single(
            metadata
                .Elements(OpfNamespace + "meta")
                .Where(element =>
                    (string?)element.Attribute("property") == property));

    private static XElement FindOption(XElement platform, string name) =>
        Assert.Single(
            platform
                .Elements("option")
                .Where(element =>
                    (string?)element.Attribute("name") == name));

    private static ZipArchiveEntry GetEntry(ZipArchive zip, string path) =>
        zip.GetEntry(path) ??
        throw new Xunit.Sdk.XunitException($"Missing EPUB entry: {path}");

    private static async Task<byte[]> ReadBytesAsync(ZipArchiveEntry entry)
    {
        await using Stream stream = entry.Open();
        using var destination = new MemoryStream();
        await stream.CopyToAsync(destination);
        return destination.ToArray();
    }

    private static async Task<string> ReadTextAsync(ZipArchiveEntry entry)
    {
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task<XDocument> ReadXmlAsync(ZipArchiveEntry entry) =>
        XDocument.Parse(await ReadTextAsync(entry));

    private sealed class CancelAfterFirstExtractionArchiveReader(
        IComicArchiveReader inner,
        CancellationTokenSource cancellation) : IComicArchiveReader
    {
        public int ExtractionCount { get; private set; }

        public Task<ComicSource> ReadAsync(
            string sourcePath,
            CancellationToken cancellationToken) =>
            inner.ReadAsync(sourcePath, cancellationToken);

        public async Task ExtractPageAsync(
            string sourcePath,
            ComicPage page,
            Stream destination,
            CancellationToken cancellationToken)
        {
            await inner.ExtractPageAsync(
                sourcePath,
                page,
                destination,
                cancellationToken);
            ExtractionCount++;
            cancellation.Cancel();
        }
    }

    private sealed class TemporaryFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(
            Path.GetTempPath(),
            $"ABC.Converter.Tests-{Guid.NewGuid():N}");

        private readonly List<string> _externalFiles = [];

        public TemporaryFiles()
        {
            Directory.CreateDirectory(_directory);
        }

        public string Add(string path)
        {
            _externalFiles.Add(path);
            return path;
        }

        public string PathFor(string relativePath) =>
            Path.Combine(_directory, relativePath);

        public void Dispose()
        {
            foreach (string path in _externalFiles)
            {
                File.Delete(path);
            }

            Directory.Delete(_directory, recursive: true);
        }
    }
}
