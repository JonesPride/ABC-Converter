using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Epub;

public interface IEpubBuilder
{
    Task BuildAsync(
        ComicSource source,
        ComicMetadata metadata,
        ReadingDirection direction,
        string destinationPath,
        CancellationToken cancellationToken);
}
