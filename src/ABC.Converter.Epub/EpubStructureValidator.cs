using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using ABC.Converter.Core.Epub;

namespace ABC.Converter.Epub;

public sealed class EpubStructureValidator : IEpubValidator
{
    private const string MimetypeContent = "application/epub+zip";

    private static readonly byte[] MimetypeBytes =
        Encoding.ASCII.GetBytes(MimetypeContent);

    private static readonly byte[] MimetypeFileNameBytes =
        Encoding.ASCII.GetBytes("mimetype");

    private static readonly XNamespace ContainerNamespace =
        "urn:oasis:names:tc:opendocument:xmlns:container";

    private static readonly XNamespace OpfNamespace =
        "http://www.idpf.org/2007/opf";

    public async Task ValidateAsync(
        string epubPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        using ZipArchive archive = OpenArchive(epubPath);
        await ValidateMimetypeAsync(archive, epubPath, cancellationToken);

        XDocument container = await ReadXmlAsync(
            GetRequiredEntry(archive, "META-INF/container.xml", "container"),
            "container",
            cancellationToken);
        string packagePath = GetPackagePath(container);
        ZipArchiveEntry packageEntry = GetRequiredEntry(
            archive,
            packagePath,
            "package");
        XDocument package = await ReadXmlAsync(
            packageEntry,
            "package",
            cancellationToken);

        IReadOnlyList<XElement> manifestItems = package
            .Descendants(OpfNamespace + "item")
            .ToList();
        ValidateManifestResources(archive, packagePath, manifestItems);
        ValidateSingleProperty(manifestItems, "nav");
        ValidateSingleProperty(manifestItems, "cover-image");
        ValidateSpine(package, manifestItems);
        await ValidateManifestXhtmlAsync(
            archive,
            packagePath,
            manifestItems,
            cancellationToken);
    }

    private static ZipArchive OpenArchive(string epubPath)
    {
        ZipArchive? archive = null;
        try
        {
            archive = ZipFile.OpenRead(epubPath);
            _ = archive.Entries.Count;
            return archive;
        }
        catch (InvalidDataException exception)
        {
            archive?.Dispose();
            throw Invalid("archive cannot be opened or enumerated.", exception);
        }
        catch (IOException exception)
        {
            archive?.Dispose();
            throw Invalid("archive cannot be opened or enumerated.", exception);
        }
    }

    private static async Task ValidateMimetypeAsync(
        ZipArchive archive,
        string epubPath,
        CancellationToken cancellationToken)
    {
        ValidateFirstCentralMimetypeEntry(archive);
        await ValidateFirstLocalMimetypeAsync(epubPath, cancellationToken);
    }

    private static void ValidateFirstCentralMimetypeEntry(ZipArchive archive)
    {
        try
        {
            if (archive.Entries.Count == 0 ||
                !string.Equals(
                    archive.Entries[0].FullName,
                    "mimetype",
                    StringComparison.Ordinal))
            {
                throw Invalid("mimetype must be the first ZIP entry.");
            }

        }
        catch (InvalidDataException exception) when (
            !exception.Message.Contains("mimetype", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("mimetype central ZIP entry cannot be read.", exception);
        }
        catch (IOException exception)
        {
            throw Invalid("mimetype central ZIP entry cannot be read.", exception);
        }
    }

    private static async Task ValidateFirstLocalMimetypeAsync(
        string epubPath,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[30];
        try
        {
            await using FileStream stream = new(
                epubPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                useAsync: true);
            await stream.ReadExactlyAsync(header, cancellationToken);

            const uint LocalFileHeaderSignature = 0x04034B50;
            if (BinaryPrimitives.ReadUInt32LittleEndian(header) !=
                LocalFileHeaderSignature)
            {
                throw Invalid("mimetype local ZIP header is invalid.");
            }

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(6, 2));
            if ((flags & 0x0008) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(8, 2)) != 0)
            {
                throw Invalid("mimetype must be stored without compression.");
            }

            uint compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(18, 4));
            uint uncompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                header.AsSpan(22, 4));
            if (compressedSize != (uint)MimetypeBytes.Length ||
                uncompressedSize != (uint)MimetypeBytes.Length)
            {
                throw Invalid("mimetype content is invalid.");
            }

            ushort fileNameLength = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(26, 2));
            ushort extraFieldLength = BinaryPrimitives.ReadUInt16LittleEndian(
                header.AsSpan(28, 2));
            if (fileNameLength != MimetypeFileNameBytes.Length)
            {
                throw Invalid("mimetype local ZIP filename is invalid.");
            }

            byte[] fileName = new byte[fileNameLength];
            await stream.ReadExactlyAsync(fileName, cancellationToken);
            if (!fileName.SequenceEqual(MimetypeFileNameBytes))
            {
                throw Invalid("mimetype local ZIP filename is invalid.");
            }

            await SkipExactlyAsync(stream, extraFieldLength, cancellationToken);
            byte[] contents = new byte[MimetypeBytes.Length];
            await stream.ReadExactlyAsync(contents, cancellationToken);
            if (!contents.SequenceEqual(MimetypeBytes))
            {
                throw Invalid("mimetype content is invalid.");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidDataException exception) when (
            !exception.Message.Contains("mimetype", StringComparison.OrdinalIgnoreCase))
        {
            throw Invalid("mimetype local ZIP header cannot be read.", exception);
        }
        catch (IOException exception)
        {
            throw Invalid("mimetype local ZIP header cannot be read.", exception);
        }
    }

    private static async Task SkipExactlyAsync(
        Stream stream,
        int byteCount,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[Math.Min(byteCount, 4096)];
        int remaining = byteCount;
        while (remaining > 0)
        {
            int count = Math.Min(remaining, buffer.Length);
            await stream.ReadExactlyAsync(buffer.AsMemory(0, count), cancellationToken);
            remaining -= count;
        }
    }

    private static ZipArchiveEntry GetRequiredEntry(
        ZipArchive archive,
        string path,
        string rule)
    {
        ZipArchiveEntry? entry = archive.GetEntry(path);
        return entry ?? throw Invalid($"{rule} entry is missing: {path}");
    }

    private static async Task<XDocument> ReadXmlAsync(
        ZipArchiveEntry entry,
        string rule,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream stream = entry.Open();
            return await XDocument.LoadAsync(
                stream,
                LoadOptions.None,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (XmlException exception)
        {
            throw Invalid($"{rule} XML is malformed.", exception);
        }
        catch (InvalidDataException exception)
        {
            throw Invalid($"{rule} XML cannot be read.", exception);
        }
        catch (IOException exception)
        {
            throw Invalid($"{rule} XML cannot be read.", exception);
        }
    }

    private static string GetPackagePath(XDocument container)
    {
        string? packagePath = container
            .Descendants(ContainerNamespace + "rootfile")
            .Select(rootfile => (string?)rootfile.Attribute("full-path"))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw Invalid("container has no usable package rootfile.");
        }

        return packagePath;
    }

    private static void ValidateManifestResources(
        ZipArchive archive,
        string packagePath,
        IReadOnlyList<XElement> manifestItems)
    {
        foreach (XElement item in manifestItems)
        {
            string href = GetRequiredManifestHref(item);
            if (archive.GetEntry(ResolveManifestPath(packagePath, href)) is null)
            {
                throw Invalid("manifest references a missing resource.");
            }
        }
    }

    private static string GetRequiredManifestHref(XElement item)
    {
        string? href = (string?)item.Attribute("href");
        if (string.IsNullOrWhiteSpace(href))
        {
            throw Invalid("manifest item href is missing.");
        }

        return href;
    }

    private static void ValidateSingleProperty(
        IReadOnlyList<XElement> manifestItems,
        string property)
    {
        int declarations = manifestItems.Sum(item => PropertyTokens(item)
            .Count(token => string.Equals(token, property, StringComparison.Ordinal)));
        if (declarations != 1)
        {
            throw Invalid($"manifest must declare exactly one {property} item.");
        }
    }

    private static void ValidateSpine(
        XDocument package,
        IReadOnlyList<XElement> manifestItems)
    {
        var manifestIds = new HashSet<string>(
            manifestItems
                .Select(item => (string?)item.Attribute("id"))
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Select(id => id!),
            StringComparer.Ordinal);

        foreach (XElement itemref in package.Descendants(OpfNamespace + "itemref"))
        {
            string? idref = (string?)itemref.Attribute("idref");
            if (string.IsNullOrWhiteSpace(idref) || !manifestIds.Contains(idref))
            {
                throw Invalid("spine references an unknown manifest item.");
            }
        }
    }

    private static async Task ValidateManifestXhtmlAsync(
        ZipArchive archive,
        string packagePath,
        IReadOnlyList<XElement> manifestItems,
        CancellationToken cancellationToken)
    {
        foreach (XElement item in manifestItems.Where(IsXhtml))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string href = GetRequiredManifestHref(item);
            ZipArchiveEntry entry = GetRequiredEntry(
                archive,
                ResolveManifestPath(packagePath, href),
                "XHTML");
            _ = await ReadXmlAsync(entry, "XHTML", cancellationToken);
        }
    }

    private static bool IsXhtml(XElement item) =>
        string.Equals(
            (string?)item.Attribute("media-type"),
            "application/xhtml+xml",
            StringComparison.Ordinal);

    private static IEnumerable<string> PropertyTokens(XElement item) =>
        ((string?)item.Attribute("properties") ?? string.Empty)
            .Split(
                [' ', '\t', '\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries);

    private static string ResolveManifestPath(string packagePath, string href)
    {
        string relativeHref = href.Split('#', 2)[0];
        string packageDirectory = packagePath.Contains('/')
            ? packagePath[..(packagePath.LastIndexOf('/') + 1)]
            : string.Empty;
        var segments = new List<string>();
        foreach (string segment in (packageDirectory + relativeHref).Split('/'))
        {
            if (string.IsNullOrEmpty(segment) || segment == ".")
            {
                continue;
            }

            if (segment == "..")
            {
                if (segments.Count == 0)
                {
                    throw Invalid("manifest href escapes the package path.");
                }

                segments.RemoveAt(segments.Count - 1);
                continue;
            }

            segments.Add(segment);
        }

        return string.Join('/', segments);
    }

    private static InvalidDataException Invalid(
        string message,
        Exception? innerException = null) =>
        new(message, innerException);
}
