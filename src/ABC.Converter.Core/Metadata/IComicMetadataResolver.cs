using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Metadata;

public interface IComicMetadataResolver
{
    Task<ComicMetadata> ResolveAsync(
        string sourcePath,
        ComicMetadata defaults,
        CancellationToken cancellationToken);
}
