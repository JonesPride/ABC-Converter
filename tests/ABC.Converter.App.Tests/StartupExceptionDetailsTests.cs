using ABC.Converter.App.Services;
using Xunit;

namespace ABC.Converter.App.Tests;

public sealed class StartupExceptionDetailsTests
{
    [Fact]
    public void IncludesTheUnderlyingFailureWithoutExposingApiKeys()
    {
        var inner = new InvalidOperationException("Button style cannot apply to ToggleButton; api_key=secret-value&format=json");
        var outer = new InvalidOperationException("Window initialization failed.", inner);

        string details = SafeExceptionDetails.Format(outer);

        Assert.Contains("Window initialization failed.", details);
        Assert.Contains("Button style cannot apply to ToggleButton", details);
        Assert.Contains("api_key=[REDACTED]&format=json", details);
        Assert.DoesNotContain("secret-value", details);
    }
}
