namespace ABC.Converter.Infrastructure.Persistence;

public sealed class SourceFingerprintProvider : ISourceFingerprintProvider
{
    public Task<string> GetFingerprintAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);

        string fullPath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullPath);
        source.Refresh();
        long length = source.Length;
        long lastWriteTicks = source.LastWriteTimeUtc.Ticks;

        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(FormattableString.Invariant(
            $"v1:{length}:{lastWriteTicks}"));
    }
}
