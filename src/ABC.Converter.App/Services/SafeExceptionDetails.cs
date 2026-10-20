using System.Text.RegularExpressions;
namespace ABC.Converter.App.Services;

public static class SafeExceptionDetails
{
    public static string Redact(string text) => Regex.Replace(text,
        @"(?i)(api_key=)[^&\s#]+", "$1[REDACTED]", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static string Format(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return Redact(exception.ToString());
    }
}
