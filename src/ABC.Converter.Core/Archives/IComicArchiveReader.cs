using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Archives;

public interface IComicArchiveReader
{
    Task<ComicSource> ReadAsync(string sourcePath, CancellationToken cancellationToken);

    Task<IComicArchiveSession> OpenSessionAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IComicArchiveSession>(
            new PerPageComicArchiveSession(this, sourcePath));
    }

    Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken);
}
