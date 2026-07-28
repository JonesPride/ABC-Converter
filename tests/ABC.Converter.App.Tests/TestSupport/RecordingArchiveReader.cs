using System.IO;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;

namespace ABC.Converter.App.Tests.TestSupport;

internal sealed class RecordingArchiveReader(
    Action<string>? onRead = null,
    Exception? readException = null,
    Func<string, CancellationToken, Task>? beforeReturn = null)
    : IComicArchiveReader
{
    private readonly List<string> _readPaths = [];

    public int ReadCount { get; private set; }

    public IReadOnlyList<string> ReadPaths => _readPaths;

    public async Task<ComicSource> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        _readPaths.Add(sourcePath);
        onRead?.Invoke(sourcePath);
        if (readException is not null)
        {
            throw readException;
        }

        if (beforeReturn is not null)
        {
            await beforeReturn(sourcePath, cancellationToken);
        }

        return new ComicSource(
            sourcePath,
            [
                new ComicPage(
                    "page1.jpg",
                    "image/jpeg",
                    1,
                    1),
            ],
            0);
    }

    public Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException(
            "The queue tests use a builder that does not extract page data.");
    }
}
