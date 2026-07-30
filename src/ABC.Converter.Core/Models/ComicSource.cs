using System.Collections.Generic;

namespace ABC.Converter.Core.Models;

public sealed record ComicSource(
    string SourcePath,
    IReadOnlyList<ComicPage> Pages,
    int CoverIndex)
{
    public long TotalUncompressedBytes { get; init; }
}
