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

    public string GetOutputPath(string outputRoot, ComicMetadata metadata) =>
        GetOutputPath(outputRoot, metadata, string.Empty);

    public string GetOutputPath(
        string outputRoot,
        ComicMetadata metadata,
        string sourcePath)
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

        if (!File.Exists(preferred))
        {
            return preferred;
        }

        string fingerprint = MetadataFingerprint(metadata, sourcePath);
        string baseName = Path.GetFileNameWithoutExtension(fileName);
        string alternate = Path.Combine(directory, $"{baseName} ({fingerprint}).epub");
        if (!File.Exists(alternate))
        {
            return alternate;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = Path.Combine(
                directory,
                $"{baseName} ({fingerprint}) ({suffix}).epub");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string MetadataFingerprint(
        ComicMetadata metadata,
        string sourcePath)
    {
        string sourceIdentity = string.IsNullOrWhiteSpace(sourcePath)
            ? string.Empty
            : Path.GetFullPath(sourcePath);
        string canonical = string.Join(
            "\u001F",
            metadata.Series,
            metadata.Volume,
            metadata.IssueNumber,
            metadata.Title,
            metadata.PublicationYear?.ToString(CultureInfo.InvariantCulture),
            sourceIdentity);
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
                IsReservedPortSuffix(stem[3]));
    }

    private static bool IsReservedPortSuffix(char suffix) =>
        (suffix >= '1' && suffix <= '9') ||
        suffix == '¹' ||
        suffix == '²' ||
        suffix == '³';

    private static string DisambiguateReservedWindowsName(string value)
    {
        int extension = value.IndexOf('.', StringComparison.Ordinal);
        return extension < 0
            ? value + "_"
            : value.Insert(extension, "_");
    }
}
