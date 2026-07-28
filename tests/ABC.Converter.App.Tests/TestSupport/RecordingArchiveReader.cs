using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;

namespace ABC.Converter.App.Tests.TestSupport;

internal sealed class RecordingArchiveReader(
    Action<string>? onRead = null,
    Exception? readException = null) : IComicArchiveReader
{
    public int ReadCount { get; private set; }

    public Task<ComicSource> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReadCount++;
        onRead?.Invoke(sourcePath);
        if (readException is not null)
        {
            throw readException;
        }

        return Task.FromResult(
            new ComicSource(
                sourcePath,
                [
                    new ComicPage(
                        "page1.jpg",
                        "image/jpeg",
                        1,
                        1),
                ],
                0));
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
