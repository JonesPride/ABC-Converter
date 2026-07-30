using System.Text;
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Metadata;
using ABC.Converter.Tests.Fixtures;
using Xunit;

namespace ABC.Converter.Tests.Metadata;

public sealed class ComicInfoMetadataResolverTests
{
    [Fact]
    public async Task UsesComicInfoFieldsAndKeepsDefaultsForMissingValues()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <ComicInfo>
              <Title>The Long Halloween</Title>
              <Series>Batman</Series>
              <Number>13</Number>
              <Year>1997</Year>
              <LanguageISO>en-US</LanguageISO>
            </ComicInfo>
            """;
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(xml)));
        var defaults = new ComicMetadata(
            "Filename title",
            "Filename series",
            "7",
            "2",
            2001,
            "en");

        try
        {
            ComicMetadata resolved = await new ComicInfoMetadataResolver()
                .ResolveAsync(path, defaults, CancellationToken.None);

            Assert.Equal("The Long Halloween", resolved.Title);
            Assert.Equal("Batman", resolved.Series);
            Assert.Equal("13", resolved.IssueNumber);
            Assert.Equal("2", resolved.Volume);
            Assert.Equal(1997, resolved.PublicationYear);
            Assert.Equal("en-US", resolved.Language);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task FindsComicInfoCaseInsensitivelyAtArchiveRoot()
    {
        const string xml = "<ComicInfo><Series>Usagi Yojimbo</Series></ComicInfo>";
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("comicinfo.XML", Encoding.UTF8.GetBytes(xml)));

        try
        {
            ComicMetadata resolved = await new ComicInfoMetadataResolver()
                .ResolveAsync(
                    path,
                    new ComicMetadata("Issue", "Fallback"),
                    CancellationToken.None);

            Assert.Equal("Usagi Yojimbo", resolved.Series);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task IgnoresNestedComicInfoAndReturnsDefaults()
    {
        const string xml = "<ComicInfo><Series>Wrong</Series></ComicInfo>";
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("metadata/ComicInfo.xml", Encoding.UTF8.GetBytes(xml)));
        var defaults = new ComicMetadata("Issue", "Fallback");

        try
        {
            ComicMetadata resolved = await new ComicInfoMetadataResolver()
                .ResolveAsync(path, defaults, CancellationToken.None);

            Assert.Same(defaults, resolved);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task RejectsMalformedComicInfoWithoutApplyingPartialMetadata()
    {
        const string xml = "<ComicInfo><Series>Broken";
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(xml)));
        var defaults = new ComicMetadata("Issue", "Fallback");

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new ComicInfoMetadataResolver().ResolveAsync(
                    path,
                    defaults,
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ProhibitsDocumentTypeDeclarations()
    {
        const string xml = """
            <!DOCTYPE ComicInfo [<!ENTITY xxe SYSTEM "file:///C:/Windows/win.ini">]>
            <ComicInfo><Series>&xxe;</Series></ComicInfo>
            """;
        string path = FixtureArchive.CreateCbz(
            ("page1.png", FixtureArchive.ValidImage(".png")),
            ("ComicInfo.xml", Encoding.UTF8.GetBytes(xml)));

        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(
                () => new ComicInfoMetadataResolver().ResolveAsync(
                    path,
                    new ComicMetadata("Issue", "Fallback"),
                    CancellationToken.None));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
