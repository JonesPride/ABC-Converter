using System.Collections.Concurrent;
using System.ComponentModel;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ABC.Converter.Infrastructure.ComicVine;

public sealed record ComicVineIssue(
    long Id,
    string Name,
    string? IssueNumber,
    string Series,
    int? StartYear);

public enum ComicVineFailureKind
{
    Authentication,
    RateLimited,
    Service,
    InvalidResponse,
}

public sealed class ComicVineApiException : Exception
{
    public ComicVineApiException(
        ComicVineFailureKind kind,
        string message,
        TimeSpan? retryAfter = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        RetryAfter = retryAfter;
    }

    public ComicVineFailureKind Kind { get; }

    public TimeSpan? RetryAfter { get; }
}

public sealed class ComicVineClient
{
    private static readonly Uri ApiBaseAddress =
        new("https://comicvine.gamespot.com/api/");
    private readonly HttpClient httpClient;
    private readonly string apiKey;
    private readonly string userAgent;

    public ComicVineClient(
        HttpClient httpClient,
        string apiKey,
        string userAgent)
    {
        this.httpClient = httpClient ??
            throw new ArgumentNullException(nameof(httpClient));
        this.apiKey = Require(apiKey, nameof(apiKey));
        this.userAgent = Require(userAgent, nameof(userAgent));
    }

    public async Task<IReadOnlyList<ComicVineIssue>> SearchIssuesAsync(
        string series,
        string? issueNumber,
        CancellationToken cancellationToken)
    {
        series = Require(series, nameof(series));
        string query = string.IsNullOrWhiteSpace(issueNumber)
            ? series
            : $"{series} {issueNumber.Trim()}";
        string requestPath =
            $"search/?api_key={Uri.EscapeDataString(apiKey)}" +
            "&format=json&resources=issue&limit=20" +
            $"&query={Uri.EscapeDataString(query)}";

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(ApiBaseAddress, requestPath));
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(userAgent);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            throw new ComicVineApiException(
                ComicVineFailureKind.Service,
                "ComicVine could not be reached.",
                innerException: exception);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw CreateHttpFailure(response);
            }

            return ParseIssues(payload);
        }
    }

    private static IReadOnlyList<ComicVineIssue> ParseIssues(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            JsonElement root = document.RootElement;
            int statusCode = root.TryGetProperty(
                    "status_code",
                    out JsonElement status)
                ? status.GetInt32()
                : 0;
            if (statusCode != 1)
            {
                string error = root.TryGetProperty(
                        "error",
                        out JsonElement errorElement)
                    ? errorElement.GetString() ?? "Unknown API error."
                    : "Unknown API error.";
                ComicVineFailureKind kind = statusCode is 100 or 107
                    ? ComicVineFailureKind.RateLimited
                    : statusCode is 101 or 102 or 103
                        ? ComicVineFailureKind.Authentication
                        : ComicVineFailureKind.Service;
                throw new ComicVineApiException(
                    kind,
                    $"ComicVine rejected the request: {error}");
            }

            if (!root.TryGetProperty(
                    "results",
                    out JsonElement results) ||
                results.ValueKind != JsonValueKind.Array)
            {
                throw new ComicVineApiException(
                    ComicVineFailureKind.InvalidResponse,
                    "ComicVine returned an invalid results document.");
            }

            var issues = new List<ComicVineIssue>();
            foreach (JsonElement result in results.EnumerateArray())
            {
                long id = result.GetProperty("id").GetInt64();
                string name = result.TryGetProperty(
                        "name",
                        out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;
                string? number = result.TryGetProperty(
                        "issue_number",
                        out JsonElement numberElement)
                    ? numberElement.GetString()
                    : null;
                string series = string.Empty;
                int? startYear = null;
                if (result.TryGetProperty(
                        "volume",
                        out JsonElement volume) &&
                    volume.ValueKind == JsonValueKind.Object)
                {
                    if (volume.TryGetProperty(
                            "name",
                            out JsonElement seriesElement))
                    {
                        series = seriesElement.GetString() ?? string.Empty;
                    }

                    if (volume.TryGetProperty(
                            "start_year",
                            out JsonElement yearElement))
                    {
                        string? text = yearElement.GetString();
                        if (int.TryParse(text, out int year))
                        {
                            startYear = year;
                        }
                    }
                }

                issues.Add(new ComicVineIssue(
                    id,
                    name,
                    number,
                    series,
                    startYear));
            }

            return issues;
        }
        catch (ComicVineApiException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw new ComicVineApiException(
                ComicVineFailureKind.InvalidResponse,
                "ComicVine returned malformed JSON.",
                innerException: exception);
        }
        catch (InvalidOperationException exception)
        {
            throw new ComicVineApiException(
                ComicVineFailureKind.InvalidResponse,
                "ComicVine returned an unexpected response shape.",
                innerException: exception);
        }
        catch (KeyNotFoundException exception)
        {
            throw new ComicVineApiException(
                ComicVineFailureKind.InvalidResponse,
                "ComicVine returned an incomplete issue.",
                innerException: exception);
        }
    }

    private static ComicVineApiException CreateHttpFailure(
        HttpResponseMessage response)
    {
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter =
                response.Headers.RetryAfter?.Delta;
            return new ComicVineApiException(
                ComicVineFailureKind.RateLimited,
                "ComicVine rate limit reached. Try again later.",
                retryAfter);
        }

        ComicVineFailureKind kind =
            response.StatusCode is HttpStatusCode.Unauthorized or
                HttpStatusCode.Forbidden
                ? ComicVineFailureKind.Authentication
                : ComicVineFailureKind.Service;
        return new ComicVineApiException(
            kind,
            $"ComicVine request failed with HTTP {(int)response.StatusCode}.");
    }

    private static string Require(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "A non-empty value is required.",
                parameterName);
        }

        return value.Trim();
    }
}

public sealed class MemoryComicVineCache
{
    private readonly TimeSpan lifetime;
    private readonly Func<DateTimeOffset> clock;
    private readonly ConcurrentDictionary<string, CacheEntry> entries =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates =
        new(StringComparer.Ordinal);

    public MemoryComicVineCache(
        TimeSpan lifetime,
        Func<DateTimeOffset>? clock = null)
    {
        if (lifetime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lifetime));
        }

        this.lifetime = lifetime;
        this.clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyList<ComicVineIssue>> GetOrCreateAsync(
        string key,
        Func<CancellationToken, Task<IReadOnlyList<ComicVineIssue>>> factory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);

        if (TryGetFresh(key, out IReadOnlyList<ComicVineIssue>? value))
        {
            return value;
        }

        SemaphoreSlim gate = gates.GetOrAdd(
            key,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFresh(key, out value))
            {
                return value;
            }

            value = await factory(cancellationToken);
            entries[key] = new CacheEntry(
                value,
                clock().Add(lifetime));
            return value;
        }
        finally
        {
            gate.Release();
        }
    }

    private bool TryGetFresh(
        string key,
        out IReadOnlyList<ComicVineIssue> value)
    {
        if (entries.TryGetValue(key, out CacheEntry? entry) &&
            entry.ExpiresAt > clock())
        {
            value = entry.Value;
            return true;
        }

        entries.TryRemove(key, out _);
        value = Array.Empty<ComicVineIssue>();
        return false;
    }

    private sealed record CacheEntry(
        IReadOnlyList<ComicVineIssue> Value,
        DateTimeOffset ExpiresAt);
}

public enum ComicVineMatchStatus
{
    NoMatch,
    Matched,
    Ambiguous,
}

public sealed record ComicVineMatch(
    ComicVineMatchStatus Status,
    ComicVineIssue? Issue,
    IReadOnlyList<ComicVineIssue> Candidates);

public static class ComicVineMatcher
{
    public static ComicVineMatch Match(
        string series,
        string? issueNumber,
        int? publicationYear,
        IEnumerable<ComicVineIssue> candidates)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(series);
        ArgumentNullException.ThrowIfNull(candidates);

        string normalizedSeries = Normalize(series);
        string? normalizedIssue = string.IsNullOrWhiteSpace(issueNumber)
            ? null
            : Normalize(issueNumber);
        var scored = candidates
            .Select(issue => new
            {
                Issue = issue,
                Score = Score(
                    issue,
                    normalizedSeries,
                    normalizedIssue,
                    publicationYear),
            })
            .Where(item => item.Score > 0)
            .OrderByDescending(item => item.Score)
            .ThenBy(item => item.Issue.Id)
            .ToArray();

        if (scored.Length == 0)
        {
            return new ComicVineMatch(
                ComicVineMatchStatus.NoMatch,
                null,
                Array.Empty<ComicVineIssue>());
        }

        int bestScore = scored[0].Score;
        ComicVineIssue[] best = scored
            .Where(item => item.Score == bestScore)
            .Select(item => item.Issue)
            .ToArray();
        if (best.Length != 1)
        {
            return new ComicVineMatch(
                ComicVineMatchStatus.Ambiguous,
                null,
                best);
        }

        return new ComicVineMatch(
            ComicVineMatchStatus.Matched,
            best[0],
            best);
    }

    private static int Score(
        ComicVineIssue issue,
        string series,
        string? issueNumber,
        int? year)
    {
        int score = Normalize(issue.Series) == series ? 6 : 0;
        if (issueNumber is not null &&
            issue.IssueNumber is not null &&
            Normalize(issue.IssueNumber) == issueNumber)
        {
            score += 3;
        }

        if (year.HasValue && issue.StartYear == year)
        {
            score += 2;
        }

        return score;
    }

    private static string Normalize(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            if (char.IsLetterOrDigit(character))
            {
                builder.Append(char.ToUpperInvariant(character));
            }
        }

        return builder.ToString();
    }
}

public interface IComicVineApiKeyStore
{
    void Save(string apiKey);

    string? Load();

    void Delete();
}

public sealed class WindowsCredentialComicVineApiKeyStore :
    IComicVineApiKeyStore
{
    private const string TargetName =
        "ABC-Converter/ComicVineApiKey";

    public void Save(string apiKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        EnsureWindows();

        byte[] secret = Encoding.Unicode.GetBytes(apiKey);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialType.Generic,
                TargetName = TargetName,
                CredentialBlobSize = checked((uint)secret.Length),
                Persist = CredentialPersistence.LocalMachine,
                UserName = Environment.UserName,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                Comment = null,
                TargetAlias = null,
            };
            credential.CredentialBlob = Marshal.AllocCoTaskMem(secret.Length);
            Marshal.Copy(secret, 0, credential.CredentialBlob, secret.Length);
            if (!CredentialWrite(
                    ref credential,
                    0))
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not store the ComicVine API key.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
        }
    }

    public string? Load()
    {
        EnsureWindows();
        if (!CredentialRead(
                TargetName,
                CredentialType.Generic,
                0,
                out IntPtr pointer))
        {
            int error = Marshal.GetLastWin32Error();
            const int NotFound = 1168;
            if (error == NotFound)
            {
                return null;
            }

            throw new Win32Exception(
                error,
                "Could not read the ComicVine API key.");
        }

        try
        {
            NativeCredential credential =
                Marshal.PtrToStructure<NativeCredential>(pointer);
            if (credential.CredentialBlob == IntPtr.Zero ||
                credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            return Marshal.PtrToStringUni(
                credential.CredentialBlob,
                checked((int)credential.CredentialBlobSize / 2));
        }
        finally
        {
            CredentialFree(pointer);
        }
    }

    public void Delete()
    {
        EnsureWindows();
        if (!CredentialDelete(
                TargetName,
                CredentialType.Generic,
                0))
        {
            int error = Marshal.GetLastWin32Error();
            const int NotFound = 1168;
            if (error != NotFound)
            {
                throw new Win32Exception(
                    error,
                    "Could not delete the ComicVine API key.");
            }
        }
    }

    private static void EnsureWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException(
                "Windows Credential Manager is only available on Windows.");
        }
    }

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredWriteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredentialWrite(
        ref NativeCredential credential,
        uint flags);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredReadW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredentialRead(
        string target,
        CredentialType type,
        uint flags,
        out IntPtr credential);

    [DllImport(
        "Advapi32.dll",
        EntryPoint = "CredDeleteW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredentialDelete(
        string target,
        CredentialType type,
        uint flags);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree")]
    private static extern void CredentialFree(IntPtr credential);

    private enum CredentialType : uint
    {
        Generic = 1,
    }

    private enum CredentialPersistence : uint
    {
        LocalMachine = 2,
    }

    [StructLayout(
        LayoutKind.Sequential,
        CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public CredentialType Type;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public CredentialPersistence Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string UserName;
    }
}
