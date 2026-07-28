namespace ABC.Converter.Core.Epub;

public interface IEpubValidator
{
    Task ValidateAsync(string epubPath, CancellationToken cancellationToken);
}
