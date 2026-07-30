using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Files;

public interface IOutputPathPolicy
{
    string GetOutputPath(string outputRoot, ComicMetadata metadata);

    string GetOutputPath(
        string outputRoot,
        ComicMetadata metadata,
        string sourcePath) =>
        GetOutputPath(outputRoot, metadata);
}
