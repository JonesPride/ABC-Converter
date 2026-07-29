using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Files;
using Xunit;

namespace ABC.Converter.Tests.Files;

public sealed class SeriesOutputPathPolicyTests
{
    [Fact]
    public void OrganizesBySeriesAndVolume()
    {
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Pilot", "Example: Series", "2", "1", 2024));

        Assert.Equal(
            Path.Combine(
                @"D:\Books",
                "Example Series",
                "Volume 1",
                "Example Series - 002 - Pilot.epub"),
            path);
    }

    [Fact]
    public void FallsBackToYearAndOmitsMissingIssue()
    {
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Annual", "Example Series", PublicationYear: 2024));

        Assert.Equal(
            Path.Combine(
                @"D:\Books",
                "Example Series",
                "2024",
                "Example Series - Annual.epub"),
            path);
    }

    [Fact]
    public void OmitsOptionalGroupIssueAndTitleWhenTheyAreUnavailable()
    {
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("", "Example Series", "not-an-issue"));

        Assert.Equal(
            Path.Combine(
                @"D:\Books",
                "Example Series",
                "Example Series.epub"),
            path);
    }

    [Fact]
    public void UsesVolumeInPreferenceToPublicationYear()
    {
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Pilot", "Example Series", Volume: "12", PublicationYear: 2024));

        Assert.Equal(
            Path.Combine(
                @"D:\Books",
                "Example Series",
                "Volume 12",
                "Example Series - Pilot.epub"),
            path);
    }

    [Fact]
    public void PadsPublicationYearToExactlyFourDigits()
    {
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Annual", "Example Series", PublicationYear: 24));

        Assert.Equal(
            Path.Combine(
                @"D:\Books",
                "Example Series",
                "0024",
                "Example Series - Annual.epub"),
            path);
    }

    [Fact]
    public void SanitizesWindowsCharactersAndTrailingDotsAndSpacesIndependentlyOfHost()
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata(
                " Pilot<>:\"/\\|?*\u0001 . ",
                " Example<>:\"/\\|?*\u0001 Series. ",
                "7",
                " 3. "));

        Assert.Equal(
            [
                "Example Series",
                "Volume 3",
                "Example Series - 007 - Pilot.epub"
            ],
            RelativeComponents(directory.Path, path));
        Assert.All(RelativeComponents(directory.Path, path), AssertWindowsSafeComponent);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("aux.txt")]
    [InlineData("lPt9 ")]
    public void SanitizesReservedWindowsNamesCaseInsensitively(string reservedName)
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata(reservedName, reservedName, Volume: reservedName));

        string[] components = RelativeComponents(directory.Path, path);
        Assert.All(components, AssertWindowsSafeComponent);
        Assert.DoesNotContain(
            components,
            IsReservedWindowsName);
    }

    [Theory]
    [InlineData("COM¹")]
    [InlineData("com¹.txt")]
    [InlineData("COM²")]
    [InlineData("cOm².log")]
    [InlineData("COM³")]
    [InlineData("CoM³.dat")]
    [InlineData("LPT¹")]
    [InlineData("lpt¹.txt")]
    [InlineData("LPT²")]
    [InlineData("lPt².log")]
    [InlineData("LPT³")]
    [InlineData("LpT³.dat")]
    public void SanitizesSuperscriptReservedWindowsAliasesInFoldersAndFiles(
        string reservedName)
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata("", reservedName));

        string[] components = RelativeComponents(directory.Path, path);
        Assert.Equal(2, components.Length);
        Assert.All(components, AssertWindowsSafeComponent);
        Assert.DoesNotContain(
            components,
            IsReservedWindowsName);
    }

    [Fact]
    public void FallsBackWhenSanitizationRemovesEveryCharacter()
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();

        string path = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata("<>:\"/\\|?* .", "<>:\"/\\|?* ."));

        string[] components = RelativeComponents(directory.Path, path);
        Assert.Equal("Unknown", components[0]);
        Assert.Equal("Unknown.epub", components[^1]);
        Assert.All(components, AssertWindowsSafeComponent);
    }

    [Fact]
    public void UsesAStableAlternateWithoutOverwritingAnExistingPreferredOutput()
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();
        var metadata = new ComicMetadata("Pilot", "Example Series", "2", "1");
        string preferred = Path.Combine(
            directory.Path,
            "Example Series",
            "Volume 1",
            "Example Series - 002 - Pilot.epub");
        Directory.CreateDirectory(Path.GetDirectoryName(preferred)!);
        File.WriteAllBytes(preferred, [0x01, 0x02, 0x03]);

        string alternate = policy.GetOutputPath(directory.Path, metadata);
        string repeated = policy.GetOutputPath(directory.Path, metadata);

        Assert.NotEqual(preferred, alternate);
        Assert.Equal(alternate, repeated);
        Assert.Equal([0x01, 0x02, 0x03], File.ReadAllBytes(preferred));
        Assert.False(File.Exists(alternate));
    }

    private static string[] RelativeComponents(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

    private static void AssertWindowsSafeComponent(string component)
    {
        Assert.False(component.Contains('<'));
        Assert.False(component.Contains('>'));
        Assert.False(component.Contains(':'));
        Assert.False(component.Contains('"'));
        Assert.False(component.Contains('/'));
        Assert.False(component.Contains('\\'));
        Assert.False(component.Contains('|'));
        Assert.False(component.Contains('?'));
        Assert.False(component.Contains('*'));
        Assert.DoesNotContain(component, character => character is >= '\0' and <= '\u001F');
        Assert.False(component.EndsWith(' '));
        Assert.False(component.EndsWith('.'));
    }

    private static bool IsReservedWindowsName(string name)
    {
        string normalized = name
            .TrimEnd('.', ' ')
            .Split('.', 2)[0]
            .ToUpperInvariant();
        return normalized is "CON" or "PRN" or "AUX" or "NUL" ||
            (normalized.Length == 4 &&
                (normalized.StartsWith("COM", StringComparison.Ordinal) ||
                    normalized.StartsWith("LPT", StringComparison.Ordinal)) &&
                IsReservedPortSuffix(normalized[3]));
    }

    private static bool IsReservedPortSuffix(char suffix) =>
        (suffix >= '1' && suffix <= '9') ||
        suffix == '¹' ||
        suffix == '²' ||
        suffix == '³';

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

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
