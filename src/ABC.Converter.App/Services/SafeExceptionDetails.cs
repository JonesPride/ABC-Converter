using System.Text.RegularExpressions;
namespace ABC.Converter.App.Services;

public static class SafeExceptionDetails
{
    public static string Redact(string text) => Regex.Replace(text,
        @"(?i)(api_key=)[^&\s#]+", "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Format(Exception exception, bool includeInnerExceptions = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var details = new List<string>();
        for (Exception? current = exception; current is not null;
             current = includeInnerExceptions ? current.InnerException : null)
        {
            details.Add($"{current.GetType().Name}: {current.Message}\n{current.StackTrace}");
        }
        return Redact(string.Join("\nCaused by: ", details));
    }
}
