using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Files;

public sealed class SeriesOutputPathPolicy : IOutputPathPolicy
{
    private static readonly HashSet<char> WindowsInvalidCharacters =
        new("<>:\"/\\|?*".ToCharArray());

    public string GetOutputPath(string outputRoot, ComicMetadata metadata)
    {
        string series = SanitizeRequired(metadata.Series);
        string? volume = SanitizeOptional(metadata.Volume);
        string? group = volume is not null
            ? $"Volume {volume}"
            : metadata.PublicationYear?.ToString("0000", CultureInfo.InvariantCulture);
        string? title = SanitizeOptional(metadata.Title);
        var parts = new List<string> { series };
        if (int.TryParse(
                metadata.IssueNumber,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out int issue))
        {
            parts.Add(issue.ToString("D3", CultureInfo.InvariantCulture));
        }

        if (title is not null)
        {
            parts.Add(title);
        }

        string fileName = string.Join(" - ", parts) + ".epub";
        string directory = group is null
            ? Path.Combine(outputRoot, series)
            : Path.Combine(outputRoot, series, group);
        string preferred = Path.Combine(directory, fileName);

        return File.Exists(preferred)
            ? AlternatePath(directory, fileName, metadata)
            : preferred;
    }

    private static string AlternatePath(
        string directory,
        string fileName,
        ComicMetadata metadata)
    {
        string fingerprint = MetadataFingerprint(metadata);
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        return Path.Combine(directory, $"{baseName} ({fingerprint}).epub");
    }

    private static string MetadataFingerprint(ComicMetadata metadata)
    {
        string canonical = string.Join(
            "\u001F",
            metadata.Series,
            metadata.Volume,
            metadata.IssueNumber,
            metadata.Title,
            metadata.PublicationYear?.ToString(CultureInfo.InvariantCulture));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
    }

    private static string SanitizeRequired(string value) =>
        SanitizeOptional(value) ?? "Unknown";

    private static string? SanitizeOptional(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        string clean = new(value.Where(IsWindowsSafeCharacter).ToArray());
        clean = clean.Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(clean))
        {
            return null;
        }

        return IsReservedWindowsName(clean)
            ? DisambiguateReservedWindowsName(clean)
            : clean;
    }

    private static bool IsWindowsSafeCharacter(char character) =>
        character > '\u001F' && !WindowsInvalidCharacters.Contains(character);

    private static bool IsReservedWindowsName(string value)
    {
        string stem = value
            .TrimEnd('.', ' ')
            .Split('.', 2)[0]
            .ToUpperInvariant();
        return stem is "CON" or "PRN" or "AUX" or "NUL" ||
            (stem.Length == 4 &&
                (stem.StartsWith("COM", StringComparison.Ordinal) ||
                    stem.StartsWith("LPT", StringComparison.Ordinal)) &&
                stem[3] is >= '1' and <= '9');
    }

    private static string DisambiguateReservedWindowsName(string value)
    {
        int extension = value.IndexOf('.', StringComparison.Ordinal);
        return extension < 0
            ? value + "_"
            : value.Insert(extension, "_");
    }
}
