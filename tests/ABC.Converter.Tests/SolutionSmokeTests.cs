using Xunit;

namespace ABC.Converter.Tests;

public sealed class SolutionSmokeTests
{
    [Fact]
    public void CoreAssemblyHasNoDesktopOrInfrastructureDependencies()
    {
        var references = System.Reflection.Assembly
            .Load("ABC.Converter.Core")
            .GetReferencedAssemblies()
            .Select(reference => reference.Name)
            .ToArray();

        Assert.DoesNotContain("PresentationFramework", references);
        Assert.DoesNotContain("Microsoft.Data.Sqlite", references);
        Assert.DoesNotContain("System.Net.Http", references);
        Assert.DoesNotContain("System.IO.Packaging", references);
    }
}
