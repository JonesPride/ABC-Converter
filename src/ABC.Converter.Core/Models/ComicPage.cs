namespace ABC.Converter.Core.Models;

public sealed record ComicPage(
    string ArchivePath,
    string MediaType,
    int Width,
    int Height);
