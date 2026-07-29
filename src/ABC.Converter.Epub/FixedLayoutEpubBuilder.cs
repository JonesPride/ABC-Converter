using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Epub;

public sealed class FixedLayoutEpubBuilder(
    IComicArchiveReader archiveReader) : IEpubBuilder
{
    public async Task BuildAsync(
        ComicSource source,
        ComicMetadata metadata,
        ReadingDirection direction,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        string? destinationDirectory = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        if (File.Exists(destinationPath))
        {
            File.Delete(destinationPath);
        }

        try
        {
            using FileStream file = File.Create(destinationPath);
            using var zip = new ZipArchive(
                file,
                ZipArchiveMode.Create,
                leaveOpen: false);
            WriteText(
                zip,
                "mimetype",
                "application/epub+zip",
                CompressionLevel.NoCompression);
            WriteText(zip, "META-INF/container.xml", ContainerXml());
            WriteText(
                zip,
                "META-INF/com.apple.ibooks.display-options.xml",
                AppleDisplayOptions());

            for (int index = 0; index < source.Pages.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ComicPage page = source.Pages[index];
                string number = (index + 1).ToString("D4");
                string extension = MediaExtension(page.MediaType);
                string imagePath =
                    $"EPUB/images/page-{number}{extension}";
                ZipArchiveEntry imageEntry = zip.CreateEntry(
                    imagePath,
                    CompressionLevel.NoCompression);
                await using (Stream imageOut = imageEntry.Open())
                {
                    await archiveReader.ExtractPageAsync(
                        source.SourcePath,
                        page,
                        imageOut,
                        cancellationToken);
                }

                cancellationToken.ThrowIfCancellationRequested();
                WriteText(
                    zip,
                    $"EPUB/pages/page-{number}.xhtml",
                    PageXhtml(
                        number,
                        extension,
                        page.Width,
                        page.Height));
            }

            cancellationToken.ThrowIfCancellationRequested();
            WriteText(
                zip,
                "EPUB/nav.xhtml",
                NavigationXhtml(metadata, source.Pages.Count));
            cancellationToken.ThrowIfCancellationRequested();
            WriteText(
                zip,
                "EPUB/package.opf",
                PackageOpf(metadata, source, direction));
        }
        catch
        {
            File.Delete(destinationPath);
            throw;
        }
    }

    private static void WriteText(
        ZipArchive zip,
        string path,
        string content,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, compression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        writer.Write(content);
    }

    private static string MediaExtension(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new InvalidDataException(
            $"Unsupported page media type: {mediaType}")
    };

    private static string ContainerXml()
    {
        XNamespace container =
            "urn:oasis:names:tc:opendocument:xmlns:container";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                container + "container",
                new XAttribute("version", "1.0"),
                new XElement(
                    container + "rootfiles",
                    new XElement(
                        container + "rootfile",
                        new XAttribute(
                            "full-path",
                            "EPUB/package.opf"),
                        new XAttribute(
                            "media-type",
                            "application/oebps-package+xml")))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string AppleDisplayOptions() =>
        new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                "display_options",
                new XElement(
                    "platform",
                    new XAttribute("name", "*"),
                    new XElement(
                        "option",
                        new XAttribute("name", "fixed-layout"),
                        "true"),
                    new XElement(
                        "option",
                        new XAttribute("name", "open-to-spread"),
                        "false"))))
            .ToString(SaveOptions.DisableFormatting);

    private static string PageXhtml(
        string number,
        string extension,
        int width,
        int height)
    {
        XNamespace html = "http://www.w3.org/1999/xhtml";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                html + "html",
                new XElement(
                    html + "head",
                    new XElement(html + "title", $"Page {number}"),
                    new XElement(
                        html + "meta",
                        new XAttribute("name", "viewport"),
                        new XAttribute(
                            "content",
                            $"width={width},height={height}")),
                    new XElement(
                        html + "style",
                        "html,body{margin:0;padding:0;width:100%;" +
                        "height:100%;overflow:hidden}" +
                        "img{display:block;width:100%;height:100%;" +
                        "object-fit:contain}")),
                new XElement(
                    html + "body",
                    new XElement(
                        html + "img",
                        new XAttribute(
                            "src",
                            $"../images/page-{number}{extension}"),
                        new XAttribute(
                            "alt",
                            $"Page {int.Parse(number)}")))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string NavigationXhtml(
        ComicMetadata metadata,
        int pageCount)
    {
        XNamespace html = "http://www.w3.org/1999/xhtml";
        XNamespace epub = "http://www.idpf.org/2007/ops";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                html + "html",
                new XAttribute(XNamespace.Xmlns + "epub", epub),
                new XElement(
                    html + "head",
                    new XElement(html + "title", metadata.Title)),
                new XElement(
                    html + "body",
                    new XElement(
                        html + "nav",
                        new XAttribute(epub + "type", "toc"),
                        new XElement(html + "h1", metadata.Title),
                        new XElement(
                            html + "ol",
                            Enumerable
                                .Range(1, pageCount)
                                .Select(index =>
                                    new XElement(
                                        html + "li",
                                        new XElement(
                                            html + "a",
                                            new XAttribute(
                                                "href",
                                                $"pages/page-{index:D4}.xhtml"),
                                            $"Page {index}"))))))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string PackageOpf(
        ComicMetadata metadata,
        ComicSource source,
        ReadingDirection direction)
    {
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        string progression = direction == ReadingDirection.RightToLeft
            ? "rtl"
            : "ltr";
        string identifier = $"urn:uuid:{Guid.NewGuid():D}";

        var manifest = new List<XElement>
        {
            new(
                opf + "item",
                new XAttribute("id", "nav"),
                new XAttribute("href", "nav.xhtml"),
                new XAttribute(
                    "media-type",
                    "application/xhtml+xml"),
                new XAttribute("properties", "nav"))
        };
        var spine = new List<XElement>();

        for (int index = 0; index < source.Pages.Count; index++)
        {
            ComicPage page = source.Pages[index];
            string number = (index + 1).ToString("D4");
            string extension = MediaExtension(page.MediaType);
            manifest.Add(
                new XElement(
                    opf + "item",
                    new XAttribute("id", $"image-{number}"),
                    new XAttribute(
                        "href",
                        $"images/page-{number}{extension}"),
                    new XAttribute("media-type", page.MediaType),
                    index == source.CoverIndex
                        ? new XAttribute(
                            "properties",
                            "cover-image")
                        : null));
            manifest.Add(
                new XElement(
                    opf + "item",
                    new XAttribute("id", $"page-{number}"),
                    new XAttribute(
                        "href",
                        $"pages/page-{number}.xhtml"),
                    new XAttribute(
                        "media-type",
                        "application/xhtml+xml")));
            spine.Add(
                new XElement(
                    opf + "itemref",
                    new XAttribute("idref", $"page-{number}")));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(
                opf + "package",
                new XAttribute("version", "3.0"),
                new XAttribute("unique-identifier", "pub-id"),
                new XElement(
                    opf + "metadata",
                    new XElement(
                        dc + "identifier",
                        new XAttribute("id", "pub-id"),
                        identifier),
                    new XElement(dc + "title", metadata.Title),
                    new XElement(dc + "language", metadata.Language),
                    new XElement(
                        opf + "meta",
                        new XAttribute(
                            "property",
                            "dcterms:modified"),
                        DateTimeOffset.UtcNow.ToString(
                            "yyyy-MM-dd'T'HH:mm:ss'Z'",
                            CultureInfo.InvariantCulture)),
                    new XElement(
                        opf + "meta",
                        new XAttribute("id", "series"),
                        new XAttribute(
                            "property",
                            "belongs-to-collection"),
                        metadata.Series),
                    new XElement(
                        opf + "meta",
                        new XAttribute("refines", "#series"),
                        new XAttribute(
                            "property",
                            "collection-type"),
                        "series"),
                    new XElement(
                        opf + "meta",
                        new XAttribute(
                            "property",
                            "rendition:layout"),
                        "pre-paginated"),
                    new XElement(
                        opf + "meta",
                        new XAttribute(
                            "property",
                            "rendition:orientation"),
                        "auto"),
                    new XElement(
                        opf + "meta",
                        new XAttribute(
                            "property",
                            "rendition:spread"),
                        "auto")),
                new XElement(opf + "manifest", manifest),
                new XElement(
                    opf + "spine",
                    new XAttribute(
                        "page-progression-direction",
                        progression),
                    spine)))
            .ToString(SaveOptions.DisableFormatting);
    }
}
