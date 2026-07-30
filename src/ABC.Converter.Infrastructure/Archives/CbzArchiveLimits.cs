namespace ABC.Converter.Infrastructure.Archives;

public sealed record CbzArchiveLimits
{
    public static CbzArchiveLimits Default { get; } = new();

    public int MaxPageCount { get; init; } = 2_000;

    public long MaxEntryCompressedBytes { get; init; } = 256L * 1024 * 1024;

    public long MaxEntryUncompressedBytes { get; init; } = 512L * 1024 * 1024;

    public long MaxTotalUncompressedBytes { get; init; } = 8L * 1024 * 1024 * 1024;

    public double MaxCompressionRatio { get; init; } = 200;

    public int MaxImageWidth { get; init; } = 32_768;

    public int MaxImageHeight { get; init; } = 32_768;

    public long MaxImagePixels { get; init; } = 160_000_000;

    internal void Validate()
    {
        if (MaxPageCount <= 0 ||
            MaxEntryCompressedBytes <= 0 ||
            MaxEntryUncompressedBytes <= 0 ||
            MaxTotalUncompressedBytes <= 0 ||
            MaxCompressionRatio <= 0 ||
            MaxImageWidth <= 0 ||
            MaxImageHeight <= 0 ||
            MaxImagePixels <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(CbzArchiveLimits),
                "All CBZ resource limits must be greater than zero.");
        }
    }
}
