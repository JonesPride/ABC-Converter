using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Files;
using Xunit;

namespace ABC.Converter.Tests.Files;

public sealed class SeriesOutputPathPolicyLengthTests
{
    [Fact]
    public void TruncatesLongComponentsWithStableHashSuffixes()
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();
        string longSeries = new('S', 400);
        string longTitle = new('T', 400);
        var metadata = new ComicMetadata(longTitle, longSeries, "12", "400", 2026);

        string first = policy.GetOutputPath(directory.Path, metadata, "source.cbz");
        string repeated = policy.GetOutputPath(directory.Path, metadata, "source.cbz");
        string[] components = RelativeComponents(directory.Path, first);

        Assert.Equal(first, repeated);
        Assert.All(
            components,
            component => Assert.True(
                component.Length <= SeriesOutputPathPolicy.MaximumComponentLength,
                $"Component was {component.Length} characters: {component}"));
        Assert.Contains('~', components[0]);
        Assert.Contains('~', Path.GetFileNameWithoutExtension(components[^1]));
        Assert.EndsWith(".epub", first, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void KeepsTheCompleteOutputPathWithinTheConfiguredWindowsBudget()
    {
        using var directory = new TemporaryDirectory(prefixLength: 90);
        var policy = new SeriesOutputPathPolicy();
        var metadata = new ComicMetadata(
            new string('T', 400),
            new string('S', 400),
            "123",
            new string('V', 200),
            2026);

        string path = policy.GetOutputPath(directory.Path, metadata, "source.cbz");

        Assert.True(
            path.Length <= SeriesOutputPathPolicy.MaximumOutputPathLength,
            $"Output path was {path.Length} characters: {path}");
    }

    [Fact]
    public void DifferentLongValuesRemainDistinctAfterTruncation()
    {
        using var directory = new TemporaryDirectory();
        var policy = new SeriesOutputPathPolicy();
        string sharedPrefix = new('A', 350);

        string first = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata("Pilot", sharedPrefix + "first"),
            "source.cbz");
        string second = policy.GetOutputPath(
            directory.Path,
            new ComicMetadata("Pilot", sharedPrefix + "second"),
            "source.cbz");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void RejectsAnOutputRootThatLeavesNoRoomForASafeRelativePath()
    {
        string outputRoot = Path.DirectorySeparatorChar + new string(
            'R',
            SeriesOutputPathPolicy.MaximumOutputPathLength);
        var policy = new SeriesOutputPathPolicy();

        var exception = Assert.Throws<PathTooLongException>(
            () => policy.GetOutputPath(
                outputRoot,
                new ComicMetadata("Pilot", "Series"),
                "source.cbz"));

        Assert.Contains("output root", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static string[] RelativeComponents(string root, string path) =>
        Path.GetRelativePath(root, path)
            .Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory(int prefixLength = 0)
        {
            string prefix = prefixLength == 0 ? string.Empty : new string('r', prefixLength);
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"abc-{prefix}-{Guid.NewGuid():N}");
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
