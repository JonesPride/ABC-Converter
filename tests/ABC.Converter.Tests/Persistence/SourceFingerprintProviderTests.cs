using ABC.Converter.Infrastructure.Persistence;
using Xunit;

namespace ABC.Converter.Tests.Persistence;

public sealed class SourceFingerprintProviderTests
{
    [Fact]
    public async Task ReturnsCanonicalVersionOneFingerprint()
    {
        using var source = new TemporarySourceFile([0x10, 0x20, 0x30, 0x40, 0x50]);
        ISourceFingerprintProvider provider = new SourceFingerprintProvider();
        var file = new FileInfo(source.Path);
        file.Refresh();

        string fingerprint = await provider.GetFingerprintAsync(
            source.Path,
            CancellationToken.None);

        Assert.Equal($"v1:{file.Length}:{file.LastWriteTimeUtc.Ticks}", fingerprint);
    }

    [Fact]
    public async Task CanonicalizesEquivalentSourcePaths()
    {
        using var source = new TemporarySourceFile([0x01]);
        string nestedDirectory = Path.Combine(source.DirectoryPath, "nested");
        Directory.CreateDirectory(nestedDirectory);
        string equivalentPath = Path.Combine(nestedDirectory, "..", Path.GetFileName(source.Path));
        ISourceFingerprintProvider provider = new SourceFingerprintProvider();

        string canonical = await provider.GetFingerprintAsync(
            Path.GetFullPath(source.Path),
            CancellationToken.None);
        string equivalent = await provider.GetFingerprintAsync(
            equivalentPath,
            CancellationToken.None);

        Assert.Equal(canonical, equivalent);
    }

    [Fact]
    public async Task FingerprintChangesWhenLengthChanges()
    {
        using var source = new TemporarySourceFile([0x01, 0x02]);
        ISourceFingerprintProvider provider = new SourceFingerprintProvider();
        DateTime originalWriteTimeUtc = File.GetLastWriteTimeUtc(source.Path);
        string original = await provider.GetFingerprintAsync(source.Path, CancellationToken.None);

        await File.WriteAllBytesAsync(source.Path, [0x01, 0x02, 0x03], CancellationToken.None);
        File.SetLastWriteTimeUtc(source.Path, originalWriteTimeUtc);
        string changed = await provider.GetFingerprintAsync(source.Path, CancellationToken.None);

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public async Task FingerprintChangesWhenUtcWriteTimeChanges()
    {
        using var source = new TemporarySourceFile([0x01, 0x02]);
        ISourceFingerprintProvider provider = new SourceFingerprintProvider();
        string original = await provider.GetFingerprintAsync(source.Path, CancellationToken.None);
        DateTime originalWriteTimeUtc = File.GetLastWriteTimeUtc(source.Path);

        File.SetLastWriteTimeUtc(source.Path, originalWriteTimeUtc.AddSeconds(5));
        string changed = await provider.GetFingerprintAsync(source.Path, CancellationToken.None);

        Assert.NotEqual(original, changed);
    }

    [Fact]
    public async Task HonorsACancelledToken()
    {
        using var source = new TemporarySourceFile([0x01]);
        ISourceFingerprintProvider provider = new SourceFingerprintProvider();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.GetFingerprintAsync(source.Path, cancellation.Token));
    }

    private sealed class TemporarySourceFile : IDisposable
    {
        public TemporarySourceFile(byte[] contents)
        {
            DirectoryPath = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"abc-converter-fingerprint-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(DirectoryPath);
            Path = System.IO.Path.Combine(DirectoryPath, "source.cbz");
            File.WriteAllBytes(Path, contents);
        }

        public string DirectoryPath { get; }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(DirectoryPath))
            {
                Directory.Delete(DirectoryPath, recursive: true);
            }
        }
    }
}
