namespace ABC.Converter.Infrastructure.Conversion;

public interface ITemporaryFileCleaner
{
    void Delete(string path);
}

internal sealed class FileSystemTemporaryFileCleaner : ITemporaryFileCleaner
{
    public void Delete(string path) => File.Delete(path);
}
