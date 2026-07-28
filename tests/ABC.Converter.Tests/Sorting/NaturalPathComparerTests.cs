using ABC.Converter.Core.Sorting;
using Xunit;

namespace ABC.Converter.Tests.Sorting;

public sealed class NaturalPathComparerTests
{
    [Fact]
    public void SortsNumericRunsAsNumbers()
    {
        string[] names = ["page10.jpg", "page2.jpg", "page1.jpg"];
        Array.Sort(names, NaturalPathComparer.Instance);
        Assert.Equal(["page1.jpg", "page2.jpg", "page10.jpg"], names);
    }

    [Fact]
    public void SortsNormalizedPathSegments()
    {
        string[] names = ["chapter2/page1.jpg", "chapter10/page1.jpg", "chapter1/page2.jpg"];
        Array.Sort(names, NaturalPathComparer.Instance);
        Assert.Equal(
            ["chapter1/page2.jpg", "chapter2/page1.jpg", "chapter10/page1.jpg"],
            names);
    }
}
