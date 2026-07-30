using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Files;

public sealed class SeriesOutputPathPolicy : IOutputPathPolicy
{
    public const int MaximumComponentLength = 120;
    public const int MaximumOutputPathLength = 240;

    private const int MinimumDirectoryComponentLength = 12;
    private const int MinimumFileNameLength = 24;
    private const string EpubExtension = ".epub";

    private static readonly HashSet<char> WindowsInvalidCharacters =
        new("<>:\"/\\|?*".ToCharArray());

    public string GetOutputPath(string outputRoot, ComicMetadata metadata) =>
        GetOutputPath(outputRoot, metadata, string.Empty);

    public string GetOutputPath(
        string outputRoot,
        ComicMetadata metadata,
        string sourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRoot);

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

        string baseName = string.Join(" - ", parts);
        string preferred = BuildBoundedPath(
            outputRoot,
            series,
            group,
            baseName + EpubExtension);

        if (!File.Exists(preferred))
        {
            return preferred;
        }

        string fingerprint = MetadataFingerprint(metadata, sourcePath);
        string alternate = BuildBoundedPath(
            outputRoot,
            series,
            group,
            $"{baseName} ({fingerprint}){EpubExtension}");
        if (!File.Exists(alternate))
        {
            return alternate;
        }

        for (int suffix = 2; ; suffix++)
        {
            string candidate = BuildBoundedPath(
                outputRoot,
                series,
                group,
                $"{baseName} ({fingerprint}) ({suffix}){EpubExtension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string BuildBoundedPath(
        string outputRoot,
        string series,
        string? group,
        string fileName)
    {
        string[] originals = group is null
            ? [series, fileName]
            : [series, group, fileName];
        int[] minimumLengths = group is null
            ? [MinimumDirectoryComponentLength, MinimumFileNameLength]
            : [
                MinimumDirectoryComponentLength,
                MinimumDirectoryComponentLength,
                MinimumFileNameLength
            ];
        int[] targetLengths = originals
            .Select(component => Math.Min(component.Length, MaximumComponentLength))
            .ToArray();

        int minimumPathLength = outputRoot.Length + originals.Length;
        for (int index = 0; index < originals.Length; index++)
        {
            minimumPathLength += Math.Min(originals[index].Length, minimumLengths[index]);
        }

        if (minimumPathLength > MaximumOutputPathLength)
        {
            throw new PathTooLongException(
                $"The output root is too long to create a safe comic path. " +
                $"Choose a shorter output root than '{outputRoot}'.");
        }

        while (CalculatePathLength(outputRoot, targetLengths) > MaximumOutputPathLength)
        {
            int component = FindLongestReducibleComponent(targetLengths, minimumLengths);
            if (component < 0)
            {
                throw new PathTooLongException(
                    $"The output root is too long to create a safe comic path. " +
                    $"Choose a shorter output root than '{outputRoot}'.");
            }

            targetLengths[component]--;
        }

        string[] bounded = originals
            .Select((component, index) =>
                BoundComponent(component, targetLengths[index], index == originals.Length - 1))
            .ToArray();
        return Path.Combine([outputRoot, .. bounded]);
    }

    private static int CalculatePathLength(string outputRoot, int[] componentLengths) =>
        outputRoot.Length + componentLengths.Sum() + componentLengths.Length;

    private static int FindLongestReducibleComponent(
        int[] componentLengths,
        int[] minimumLengths)
    {
        int selected = -1;
        int longest = -1;
        for (int index = 0; index < componentLengths.Length; index++)
        {
            if (componentLengths[index] > minimumLengths[index] &&
                componentLengths[index] > longest)
            {
                selected = index;
                longest = componentLengths[index];
            }
        }

        return selected;
    }

    private static string BoundComponent(string value, int maximumLength, bool isFileName)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        string extension = isFileName ? Path.GetExtension(value) : string.Empty;
        string stem = isFileName ? Path.GetFileNameWithoutExtension(value) : value;
        string hash = ShortHash(value);
        string suffix = "~" + hash;
        int stemBudget = maximumLength - extension.Length;
        int prefixBudget = stemBudget - suffix.Length;
        if (prefixBudget < 1)
        {
            throw new PathTooLongException(
                "The configured component-length budget cannot preserve a safe unique name.");
        }

        return TruncateWithoutSplittingSurrogatePairs(stem, prefixBudget) + suffix + extension;
    }

    private static string TruncateWithoutSplittingSurrogatePairs(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        int length = maximumLength;
        if (length > 0 && char.IsHighSurrogate(value[length - 1]))
        {
            length--;
        }

        return value[..length];
    }

    private static string ShortHash(string value)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash[..4]).ToLowerInvariant();
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
        return ShortHash(canonical);
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
