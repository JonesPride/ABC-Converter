using System.Security.Cryptography;
using ABC.Converter.Core.Queue;

namespace ABC.Converter.Infrastructure.Persistence;

public static class PublishedOutputIdentity
{
    public static bool Matches(QueueJobRecord record, string path)
    {
        if (!File.Exists(path)) return false;
        // Ordinary acknowledged completions retain the original existence-only policy.
        if (record.ErrorStage != "Publishing") return true;
        const string prefix = "sha256:";
        if (record.TechnicalError is not { } receipt || !receipt.StartsWith(prefix, StringComparison.Ordinal) || receipt.Length != prefix.Length + 64)
            return false;
        try
        {
            using Stream stream = File.OpenRead(path);
            return string.Equals(receipt[prefix.Length..], Convert.ToHexString(SHA256.HashData(stream)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
