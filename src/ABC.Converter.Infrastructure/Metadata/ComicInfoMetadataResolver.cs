using System.IO.Compression;
using System.Xml;
using System.Xml.Linq;
using ABC.Converter.Core.Metadata;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Metadata;

public sealed class ComicInfoMetadataResolver : IComicMetadataResolver
{
    private const int MaximumComicInfoBytes = 1024 * 1024;

    public async Task<ComicMetadata> ResolveAsync(
        string sourcePath,
        ComicMetadata defaults,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(defaults);
        cancellationToken.ThrowIfCancellationRequested();

        using ZipArchive archive = ZipFile.OpenRead(sourcePath);
        ZipArchiveEntry[] candidates = archive.Entries
            .Where(IsRootComicInfoEntry)
            .ToArray();
        if (candidates.Length == 0)
        {
            return defaults;
        }

        if (candidates.Length > 1)
        {
            throw new InvalidDataException(
                "The CBZ contains more than one root-level ComicInfo.xml file.");
        }

        ZipArchiveEntry entry = candidates[0];
        if (entry.Length > MaximumComicInfoBytes)
        {
            throw new InvalidDataException(
                $"ComicInfo.xml exceeds the {MaximumComicInfoBytes:N0}-byte limit.");
        }

        try
        {
            await using Stream source = entry.Open();
            using var bounded = new MemoryStream(
                capacity: checked((int)Math.Min(entry.Length, MaximumComicInfoBytes)));
            await CopyBoundedAsync(source, bounded, cancellationToken);
            bounded.Position = 0;

            var settings = new XmlReaderSettings
            {
                Async = true,
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumComicInfoBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using XmlReader reader = XmlReader.Create(bounded, settings);
            XDocument document = await XDocument.LoadAsync(
                reader,
                LoadOptions.None,
                cancellationToken);
            XElement root = document.Root ??
                throw new InvalidDataException("ComicInfo.xml has no root element.");
            if (!string.Equals(
                    root.Name.LocalName,
                    "ComicInfo",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "ComicInfo.xml must have a ComicInfo root element.");
            }

            string title = Value(root, "Title") ?? defaults.Title;
            string series = Value(root, "Series") ?? defaults.Series;
            string? issueNumber = Value(root, "Number") ?? defaults.IssueNumber;
            string? volume = Value(root, "Volume") ?? defaults.Volume;
            string language = Value(root, "LanguageISO") ?? defaults.Language;
            int? publicationYear = ParseYear(Value(root, "Year")) ??
                defaults.PublicationYear;

            if (title == defaults.Title &&
                series == defaults.Series &&
                issueNumber == defaults.IssueNumber &&
                volume == defaults.Volume &&
                publicationYear == defaults.PublicationYear &&
                language == defaults.Language)
            {
                return defaults;
            }

            return new ComicMetadata(
                title,
                series,
                issueNumber,
                volume,
                publicationYear,
                language);
        }
        catch (XmlException exception)
        {
            throw new InvalidDataException(
                "ComicInfo.xml is malformed or contains prohibited XML content.",
                exception);
        }
    }

    private static bool IsRootComicInfoEntry(ZipArchiveEntry entry)
    {
        string normalized = entry.FullName.Replace('\\', '/');
        return !normalized.Contains('/') &&
            string.Equals(
                normalized,
                "ComicInfo.xml",
                StringComparison.OrdinalIgnoreCase);
    }

    private static string? Value(XElement root, string name)
    {
        string? value = root.Elements()
            .FirstOrDefault(element => string.Equals(
                element.Name.LocalName,
                name,
                StringComparison.Ordinal))?
            .Value
            .Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static int? ParseYear(string? value) =>
        int.TryParse(value, out int year) && year is >= 1 and <= 9999
            ? year
            : null;

    private static async Task CopyBoundedAsync(
        Stream source,
        Stream destination,
        CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = await source.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                return;
            }

            total = checked(total + read);
            if (total > MaximumComicInfoBytes)
            {
                throw new InvalidDataException(
                    $"ComicInfo.xml exceeds the {MaximumComicInfoBytes:N0}-byte limit.");
            }

            await destination.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }
}
