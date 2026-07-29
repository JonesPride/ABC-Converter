using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Archives;

public interface IComicArchiveSession : IAsyncDisposable
{
    Task ExtractPageAsync(
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken);
}

internal sealed class PerPageComicArchiveSession(
    IComicArchiveReader reader,
    string sourcePath) : IComicArchiveSession
{
    public Task ExtractPageAsync(
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken) =>
        reader.ExtractPageAsync(
            sourcePath,
            page,
            destination,
            cancellationToken);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
