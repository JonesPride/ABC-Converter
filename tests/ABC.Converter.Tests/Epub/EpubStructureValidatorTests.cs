using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Epub;

public sealed class EpubStructureValidatorTests
{
    [Fact]
    public async Task AcceptsAValidEpubBuiltByTaskFour()
    {
        using var files = new TemporaryDirectory();
        string cbz = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("page2.jpg", FixtureArchive.ValidImage(".jpg")));
        files.Track(cbz);
        string epub = files.PathFor("valid.epub");
        var reader = new CbzArchiveReader();
        ComicSource source = await reader.ReadAsync(cbz, CancellationToken.None);
        var builder = new FixedLayoutEpubBuilder(reader);
        var validator = new EpubStructureValidator();

        await builder.BuildAsync(
            source,
            new ComicMetadata("Pilot", "Example Series", "1"),
            ReadingDirection.LeftToRight,
            epub,
            CancellationToken.None);

        await validator.ValidateAsync(epub, CancellationToken.None);
    }

    [Fact]
    public async Task ResolvesThePackageAtThePathDeclaredByContainer()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("nested-package.epub");
        WriteEpub(
            epub,
            packagePath: "content/package.opf",
            entries:
            [
                Entry("content/nav.xhtml", ValidXhtml()),
                Entry("content/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("content/pages/page.xhtml", ValidXhtml()),
                Entry("content/package.opf", PackageOpf())
            ]);
        var validator = new EpubStructureValidator();

        await validator.ValidateAsync(epub, CancellationToken.None);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task RejectsAMimetypeThatIsNotTheFirstStoredEntry(
        bool writeBeforeMimetype,
        bool compressMimetype)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-mimetype.epub");
        WriteEpub(
            epub,
            packagePath: "EPUB/package.opf",
            entries:
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf())
            ],
            writeBeforeMimetype: writeBeforeMimetype,
            compressMimetype: compressMimetype);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "mimetype");
    }

    [Fact]
    public async Task RejectsAMimetypeWithAnythingOtherThanTheRequiredExactContent()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("wrong-mimetype.epub");
        WriteEpub(
            epub,
            packagePath: "EPUB/package.opf",
            entries:
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf())
            ],
            mimetypeContent: "application/epub+zip\n");
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "mimetype");
    }

    [Fact]
    public async Task RejectsAnArchiveWithoutAMimetypeEntry()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-mimetype.epub");
        WriteEpub(
            epub,
            packagePath: "EPUB/package.opf",
            entries:
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf())
            ],
            includeMimetype: false);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "mimetype");
    }

    [Fact]
    public async Task RejectsWhenCentralDirectoryOrderDisagreesWithPhysicalLocalOrder()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("central-order-mismatch.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf())
            ],
            writeBeforeMimetype: true);
        Assert.Equal((ushort)0, ReadFirstLocalCompressionMethod(epub));
        RewriteCentralDirectoryWithMimetypeFirst(epub);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "mimetype");
    }

    [Fact]
    public async Task RejectsInvalidPhysicalMimetypeWhenCentralFirstDuplicateIsValid()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("duplicate-mimetype.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf()),
                Entry(
                    "mimetype",
                    Encoding.ASCII.GetBytes("application/epub+zip"),
                    CompressionLevel.NoCompression)
            ],
            mimetypeContent: "12345678901234567890");
        RewriteCentralDirectoryWithMimetypeFirst(
            epub,
            selectLastMimetype: true);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "mimetype");
    }

    [Fact]
    public async Task RejectsATruncatedArchiveWithAnArchiveRule()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("truncated.epub");
        await File.WriteAllBytesAsync(epub, [0x50]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "archive");
    }

    [Fact]
    public async Task PropagatesCancellationBeforeReadingTheArchive()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = new EpubStructureValidator();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => validator.ValidateAsync("unused.epub", cancellation.Token));
    }

    [Fact]
    public async Task RejectsAMissingPackageDeclaredByContainer()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-package.epub");
        WriteEpub(
            epub,
            packagePath: "EPUB/missing.opf",
            entries:
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml())
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "package");
    }

    [Fact]
    public async Task RejectsAMissingContainerDocument()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-container.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [Entry("EPUB/package.opf", PackageOpf())],
            includeContainer: false);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "container");
    }

    [Theory]
    [InlineData("<container>")]
    [InlineData("<container xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\"><rootfiles><rootfile /></rootfiles></container>")]
    public async Task RejectsMalformedOrUnusableContainerDocument(string containerContents)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-container.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [Entry("EPUB/package.opf", PackageOpf())],
            containerContent: containerContents);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "container");
    }

    [Fact]
    public async Task RejectsMalformedDeclaredPackageDocument()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-package.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [Entry("EPUB/package.opf", "<package>")]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "package");
    }

    [Theory]
    [InlineData("nav.xhtml")]
    [InlineData("images/cover.png")]
    [InlineData("pages/page.xhtml")]
    public async Task RejectsEveryMissingManifestResource(string missingHref)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-resource.epub");
        var entries = new List<ArchiveEntry>
        {
            Entry("EPUB/package.opf", PackageOpf())
        };
        foreach ((string href, string path, byte[] contents) in ManifestResources())
        {
            if (!string.Equals(href, missingHref, StringComparison.Ordinal))
            {
                entries.Add(Entry(path, contents));
            }
        }

        WriteEpub(epub, "EPUB/package.opf", entries);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "manifest");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task RejectsXhtmlManifestItemWithoutAUsableHref(string? href)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-xhtml-href.epub");
        string replacement = href is null ? string.Empty : $" href=\"{href}\"";
        string package = PackageOpf().Replace(
            " href=\"pages/page.xhtml\"",
            replacement,
            StringComparison.Ordinal);
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", package)
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "manifest");
    }

    [Theory]
    [InlineData(false, false, true, false, "nav")]
    [InlineData(true, true, true, false, "nav")]
    [InlineData(true, false, false, false, "cover-image")]
    [InlineData(true, false, true, true, "cover-image")]
    public async Task RequiresExactlyOneNavigationAndCoverImageDeclaration(
        bool includeNavigation,
        bool duplicateNavigation,
        bool includeCover,
        bool duplicateCover,
        string failedRule)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("missing-declaration.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf(
                    includeNavigation ? "nav" : "",
                    includeCover ? "cover-image" : "",
                    includeDuplicateNavigation: duplicateNavigation,
                    includeDuplicateCover: duplicateCover))
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            failedRule);
    }

    [Fact]
    public async Task RejectsSpineReferencesThatDoNotResolveToAManifestItem()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-spine.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf(spineIdref: "missing-page"))
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "spine");
    }

    [Theory]
    [InlineData("EPUB/nav.xhtml")]
    [InlineData("EPUB/pages/page.xhtml")]
    public async Task RejectsMalformedEveryManifestXhtmlDocument(string malformedPath)
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-xhtml.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry(
                    "EPUB/nav.xhtml",
                    malformedPath == "EPUB/nav.xhtml" ? "<html>" : ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry(
                    "EPUB/pages/page.xhtml",
                    malformedPath == "EPUB/pages/page.xhtml" ? "<html>" : ValidXhtml()),
                Entry("EPUB/package.opf", PackageOpf())
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "XHTML");
    }

    [Fact]
    public async Task RejectsMalformedNonSpineManifestXhtmlDocument()
    {
        using var files = new TemporaryDirectory();
        string epub = files.PathFor("bad-supplemental-xhtml.epub");
        WriteEpub(
            epub,
            "EPUB/package.opf",
            [
                Entry("EPUB/nav.xhtml", ValidXhtml()),
                Entry("EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]),
                Entry("EPUB/pages/page.xhtml", ValidXhtml()),
                Entry("EPUB/pages/bonus.xhtml", "<html>"),
                Entry("EPUB/package.opf", PackageOpf(includeSupplementalXhtml: true))
            ]);
        var validator = new EpubStructureValidator();

        await AssertInvalidAsync(
            () => validator.ValidateAsync(epub, CancellationToken.None),
            "XHTML");
    }

    private static async Task AssertInvalidAsync(
        Func<Task> action,
        string failedRule)
    {
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(action);
        Assert.Contains(failedRule, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Href, string Path, byte[] Contents)> ManifestResources()
    {
        yield return ("nav.xhtml", "EPUB/nav.xhtml", Encoding.UTF8.GetBytes(ValidXhtml()));
        yield return ("images/cover.png", "EPUB/images/cover.png", [0x89, 0x50, 0x4E, 0x47]);
        yield return ("pages/page.xhtml", "EPUB/pages/page.xhtml", Encoding.UTF8.GetBytes(ValidXhtml()));
    }

    private static ArchiveEntry Entry(string path, string contents) =>
        Entry(path, Encoding.UTF8.GetBytes(contents));

    private static ArchiveEntry Entry(
        string path,
        byte[] contents,
        CompressionLevel compression = CompressionLevel.Optimal) =>
        new(path, contents, compression);

    private static string ContainerXml(string packagePath) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <container xmlns="urn:oasis:names:tc:opendocument:xmlns:container" version="1.0">
          <rootfiles><rootfile full-path="{packagePath}" media-type="application/oebps-package+xml" /></rootfiles>
        </container>
        """;

    private static string PackageOpf(
        string navigationProperties = "nav",
        string coverProperties = "cover-image",
        string spineIdref = "page",
        bool includeDuplicateNavigation = false,
        bool includeDuplicateCover = false,
        bool includeSupplementalXhtml = false) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <package xmlns="http://www.idpf.org/2007/opf" version="3.0">
          <metadata />
          <manifest>
            <item id="nav" href="nav.xhtml" media-type="application/xhtml+xml" properties="{navigationProperties}" />
            <item id="cover" href="images/cover.png" media-type="image/png" properties="{coverProperties}" />
            {(includeDuplicateNavigation ? "<item id=\"nav-duplicate\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\" />" : string.Empty)}
            {(includeDuplicateCover ? "<item id=\"cover-duplicate\" href=\"images/cover.png\" media-type=\"image/png\" properties=\"cover-image\" />" : string.Empty)}
            <item id="page" href="pages/page.xhtml" media-type="application/xhtml+xml" />
            {(includeSupplementalXhtml ? "<item id=\"bonus\" href=\"pages/bonus.xhtml\" media-type=\"application/xhtml+xml\" />" : string.Empty)}
          </manifest>
          <spine><itemref idref="{spineIdref}" /></spine>
        </package>
        """;

    private static string ValidXhtml() =>
        "<html xmlns=\"http://www.w3.org/1999/xhtml\"><head><title>Page</title></head><body /></html>";

    private static void WriteEpub(
        string path,
        string packagePath,
        IEnumerable<ArchiveEntry> entries,
        bool writeBeforeMimetype = false,
        bool compressMimetype = false,
        string mimetypeContent = "application/epub+zip",
        bool includeMimetype = true,
        bool includeContainer = true,
        string? containerContent = null)
    {
        using FileStream stream = File.Create(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);
        if (writeBeforeMimetype)
        {
            WriteEntry(
                archive,
                "before-mimetype",
                [0x00],
                CompressionLevel.NoCompression);
        }

        if (includeMimetype)
        {
            WriteEntry(
                archive,
                "mimetype",
                Encoding.ASCII.GetBytes(mimetypeContent),
                compressMimetype ? CompressionLevel.Optimal : CompressionLevel.NoCompression);
        }
        if (includeContainer)
        {
            WriteEntry(
                archive,
                "META-INF/container.xml",
                Encoding.UTF8.GetBytes(containerContent ?? ContainerXml(packagePath)));
        }
        foreach (ArchiveEntry entry in entries)
        {
            WriteEntry(archive, entry.Path, entry.Contents, entry.Compression);
        }
    }

    private static void WriteEntry(
        ZipArchive archive,
        string path,
        byte[] contents,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path, compression);
        using Stream stream = entry.Open();
        stream.Write(contents);
    }

    private static ushort ReadFirstLocalCompressionMethod(string path)
    {
        byte[] header = File.ReadAllBytes(path)[..10];
        return BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2));
    }

    private static void RewriteCentralDirectoryWithMimetypeFirst(
        string path,
        bool selectLastMimetype = false)
    {
        byte[] archive = File.ReadAllBytes(path);
        int endOfCentralDirectory = FindEndOfCentralDirectory(archive);
        int entryCount = BinaryPrimitives.ReadUInt16LittleEndian(
            archive.AsSpan(endOfCentralDirectory + 10, 2));
        int centralDirectorySize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            archive.AsSpan(endOfCentralDirectory + 12, 4)));
        int centralDirectoryOffset = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            archive.AsSpan(endOfCentralDirectory + 16, 4)));
        int centralDirectoryEnd = checked(centralDirectoryOffset + centralDirectorySize);
        var records = new List<CentralDirectoryRecord>(entryCount);
        int offset = centralDirectoryOffset;

        for (int index = 0; index < entryCount; index++)
        {
            records.Add(ReadCentralDirectoryRecord(archive, ref offset));
        }

        Assert.Equal(centralDirectoryEnd, offset);
        CentralDirectoryRecord[] mimetypes = records
            .Where(record => record.Name == "mimetype")
            .ToArray();
        CentralDirectoryRecord mimetype = selectLastMimetype
            ? mimetypes[^1]
            : Assert.Single(mimetypes);
        byte[] reordered = records
            .Where(record => record != mimetype)
            .Prepend(mimetype)
            .SelectMany(record => record.Bytes)
            .ToArray();
        Assert.Equal(centralDirectorySize, reordered.Length);
        reordered.CopyTo(archive, centralDirectoryOffset);
        File.WriteAllBytes(path, archive);
    }

    private static int FindEndOfCentralDirectory(byte[] archive)
    {
        const uint EndOfCentralDirectorySignature = 0x06054B50;
        int firstPossibleOffset = Math.Max(0, archive.Length - 22 - ushort.MaxValue);
        for (int offset = archive.Length - 22; offset >= firstPossibleOffset; offset--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset, 4)) ==
                EndOfCentralDirectorySignature)
            {
                return offset;
            }
        }

        throw new InvalidDataException("Test fixture has no end of central directory.");
    }

    private static CentralDirectoryRecord ReadCentralDirectoryRecord(
        byte[] archive,
        ref int offset)
    {
        const uint CentralDirectorySignature = 0x02014B50;
        const int FixedHeaderLength = 46;
        Assert.Equal(
            CentralDirectorySignature,
            BinaryPrimitives.ReadUInt32LittleEndian(archive.AsSpan(offset, 4)));
        int fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
            archive.AsSpan(offset + 28, 2));
        int extraLength = BinaryPrimitives.ReadUInt16LittleEndian(
            archive.AsSpan(offset + 30, 2));
        int commentLength = BinaryPrimitives.ReadUInt16LittleEndian(
            archive.AsSpan(offset + 32, 2));
        int recordLength = checked(
            FixedHeaderLength + fileNameLength + extraLength + commentLength);
        string name = Encoding.UTF8.GetString(
            archive,
            offset + FixedHeaderLength,
            fileNameLength);
        byte[] bytes = archive.AsSpan(offset, recordLength).ToArray();
        offset += recordLength;
        return new CentralDirectoryRecord(name, bytes);
    }

    private sealed record ArchiveEntry(
        string Path,
        byte[] Contents,
        CompressionLevel Compression = CompressionLevel.Optimal);

    private sealed record CentralDirectoryRecord(string Name, byte[] Bytes);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"abc-converter-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string PathFor(string name) => System.IO.Path.Combine(Path, name);

        public void Track(string path) => _trackedFiles.Add(path);

        public void Dispose()
        {
            foreach (string path in _trackedFiles)
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }

        private readonly List<string> _trackedFiles = [];
    }
}
