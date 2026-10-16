namespace ABC.Converter.Infrastructure.Persistence;

public interface ISourceFingerprintProvider
{
    Task<string> GetFingerprintAsync(
        string sourcePath,
        CancellationToken cancellationToken);
}
