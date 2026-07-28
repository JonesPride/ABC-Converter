namespace ABC.Converter.Core.Models;

public sealed record ConversionRequest(
    string SourcePath,
    string OutputRoot,
    ComicMetadata Metadata,
    ReadingDirection Direction = ReadingDirection.LeftToRight);
