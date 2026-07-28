namespace ABC.Converter.Core.Models;

public sealed record ConversionResult(
    string SourcePath,
    string OutputPath,
    int PageCount);
