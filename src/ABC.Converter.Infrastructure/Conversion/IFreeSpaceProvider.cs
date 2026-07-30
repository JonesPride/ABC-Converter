namespace ABC.Converter.Infrastructure.Conversion;

public interface IFreeSpaceProvider
{
    long GetAvailableBytes(string path);
}

public sealed class DriveFreeSpaceProvider : IFreeSpaceProvider
{
    public long GetAvailableBytes(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ??
            throw new IOException($"Cannot determine the drive for '{fullPath}'.");
        return new DriveInfo(root).AvailableFreeSpace;
    }
}
