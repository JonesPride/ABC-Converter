namespace ABC.Converter.Core.Models;

public sealed record ComicMetadata(
    string Title,
    string Series,
    string? IssueNumber = null,
    string? Volume = null,
    int? PublicationYear = null,
    string Language = "en");
