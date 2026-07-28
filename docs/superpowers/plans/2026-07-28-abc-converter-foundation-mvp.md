# ABC Converter Foundation MVP Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a working Windows desktop slice that accepts a CBZ by drag and drop, creates a fixed-layout EPUB 3 file, validates its package structure, and leaves the source untouched.

**Architecture:** A .NET 8 WPF shell calls application-neutral services through Core interfaces. Infrastructure reads CBZ archives, Epub builds publications in temporary directories, and an atomic publisher moves validated files into organized output folders. This first slice keeps queue state in memory while establishing interfaces used by later persistence and ComicVine plans.

**Tech Stack:** C# 12, .NET 8, WPF, `System.IO.Compression`, `System.Xml.Linq`, PowerShell 7 or Windows PowerShell 5.1, xUnit from the .NET test template.

## Global Constraints

- Product name: Apple Books Comic Converter, shortened to ABC Converter.
- Target Windows 10 version 22H2 and Windows 11 on x64.
- Accept `.cbz` files only, case-insensitively.
- Supported page images: JPG, JPEG, PNG, and WebP.
- Preserve source CBZ files byte-for-byte.
- Default reading direction is left to right.
- Produce fixed-layout EPUB 3 output with Apple Books display options.
- All setup, build, test, and run workflows must have PowerShell entry points.
- Do not require Bash, WSL, winget, or manual Visual Studio interaction.

## Delivery Sequence Beyond This Plan

This specification is intentionally split into independently testable plans:

1. Foundation MVP, this plan.
2. Persistent queue, concurrency, pause, cancellation, recovery, and logs.
3. ComicInfo.xml, ComicVine integration, confidence scoring, caching, and metadata review.
4. Complete queue-focused interface, settings, history, filters, accessibility, and polish.
5. Installer, GitHub Releases updates, release automation, Windows compatibility, and 10,000-job performance qualification.

Each later plan extends the contracts created here without changing the EPUB engine's public API.

---

### Task 1: Solution Skeleton and PowerShell Workflow

**Files:**
- Create: `ABC.Converter.sln`
- Create: `Directory.Build.props`
- Create: `src/ABC.Converter.App/ABC.Converter.App.csproj`
- Create: `src/ABC.Converter.Core/ABC.Converter.Core.csproj`
- Create: `src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj`
- Create: `src/ABC.Converter.Epub/ABC.Converter.Epub.csproj`
- Create: `tests/ABC.Converter.Tests/ABC.Converter.Tests.csproj`
- Create: `scripts/Setup.ps1`
- Create: `scripts/Build.ps1`
- Create: `scripts/Test.ps1`
- Create: `tests/ABC.Converter.Tests/SolutionSmokeTests.cs`

**Interfaces:**
- Consumes: .NET 8 SDK available through `dotnet`.
- Produces: A buildable solution and stable project references for all later tasks.

- [ ] **Step 1: Scaffold the solution and projects**

Run from PowerShell:

```powershell
dotnet new sln --name ABC.Converter
dotnet new wpf --framework net8.0-windows --name ABC.Converter.App --output src/ABC.Converter.App
dotnet new classlib --framework net8.0 --name ABC.Converter.Core --output src/ABC.Converter.Core
dotnet new classlib --framework net8.0 --name ABC.Converter.Infrastructure --output src/ABC.Converter.Infrastructure
dotnet new classlib --framework net8.0 --name ABC.Converter.Epub --output src/ABC.Converter.Epub
dotnet new xunit --framework net8.0 --name ABC.Converter.Tests --output tests/ABC.Converter.Tests
dotnet sln ABC.Converter.sln add (Get-ChildItem -Recurse -Filter *.csproj).FullName
dotnet add src/ABC.Converter.App/ABC.Converter.App.csproj reference src/ABC.Converter.Core/ABC.Converter.Core.csproj
dotnet add src/ABC.Converter.App/ABC.Converter.App.csproj reference src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj
dotnet add src/ABC.Converter.App/ABC.Converter.App.csproj reference src/ABC.Converter.Epub/ABC.Converter.Epub.csproj
dotnet add src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj reference src/ABC.Converter.Core/ABC.Converter.Core.csproj
dotnet add src/ABC.Converter.Epub/ABC.Converter.Epub.csproj reference src/ABC.Converter.Core/ABC.Converter.Core.csproj
dotnet add tests/ABC.Converter.Tests/ABC.Converter.Tests.csproj reference src/ABC.Converter.Core/ABC.Converter.Core.csproj
dotnet add tests/ABC.Converter.Tests/ABC.Converter.Tests.csproj reference src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj
dotnet add tests/ABC.Converter.Tests/ABC.Converter.Tests.csproj reference src/ABC.Converter.Epub/ABC.Converter.Epub.csproj
dotnet add src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj package SixLabors.ImageSharp
```

Expected: all projects are added and every reference command succeeds.

- [ ] **Step 2: Add shared build settings**

Create `Directory.Build.props`:

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>12.0</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <Deterministic>true</Deterministic>
  </PropertyGroup>
</Project>
```

- [ ] **Step 3: Write the PowerShell scripts**

Create `scripts/Setup.ps1`:

```powershell
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$sdk = dotnet --version
if (-not $sdk.StartsWith('8.')) {
    throw "ABC Converter requires the .NET 8 SDK. Found: $sdk"
}
dotnet restore "$PSScriptRoot\..\ABC.Converter.sln"
```

Create `scripts/Build.ps1`:

```powershell
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
dotnet build "$PSScriptRoot\..\ABC.Converter.sln" --configuration $Configuration --no-restore
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

Create `scripts/Test.ps1`:

```powershell
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
dotnet test "$PSScriptRoot\..\ABC.Converter.sln" --configuration $Configuration --no-build
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

- [ ] **Step 4: Add and run a solution smoke test**

Create `tests/ABC.Converter.Tests/SolutionSmokeTests.cs`:

```csharp
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
```

Run:

```powershell
.\scripts\Setup.ps1
.\scripts\Build.ps1
.\scripts\Test.ps1
```

Expected: build succeeds and the architecture test passes.

- [ ] **Step 5: Commit**

```powershell
git add ABC.Converter.sln Directory.Build.props src tests scripts
git commit -m "build: scaffold ABC Converter solution"
```

---

### Task 2: Core Models and Natural Page Sorting

**Files:**
- Create: `src/ABC.Converter.Core/Models/ReadingDirection.cs`
- Create: `src/ABC.Converter.Core/Models/ComicMetadata.cs`
- Create: `src/ABC.Converter.Core/Models/ComicPage.cs`
- Create: `src/ABC.Converter.Core/Models/ComicSource.cs`
- Create: `src/ABC.Converter.Core/Models/ConversionRequest.cs`
- Create: `src/ABC.Converter.Core/Models/ConversionResult.cs`
- Create: `src/ABC.Converter.Core/Sorting/NaturalPathComparer.cs`
- Create: `tests/ABC.Converter.Tests/Sorting/NaturalPathComparerTests.cs`

**Interfaces:**
- Consumes: None.
- Produces: `ReadingDirection`, immutable conversion records, and `NaturalPathComparer.Instance`.

- [ ] **Step 1: Write failing natural-sort tests**

Create `tests/ABC.Converter.Tests/Sorting/NaturalPathComparerTests.cs`:

```csharp
using ABC.Converter.Core.Sorting;

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
```

- [ ] **Step 2: Verify the test fails**

Run:

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~NaturalPathComparerTests
```

Expected: compilation fails because `NaturalPathComparer` does not exist.

- [ ] **Step 3: Add immutable core models**

Create the files under `src/ABC.Converter.Core/Models`:

```csharp
// ReadingDirection.cs
namespace ABC.Converter.Core.Models;
public enum ReadingDirection { LeftToRight, RightToLeft }

// ComicMetadata.cs
namespace ABC.Converter.Core.Models;
public sealed record ComicMetadata(
    string Title,
    string Series,
    string? IssueNumber = null,
    string? Volume = null,
    int? PublicationYear = null,
    string Language = "en");

// ComicPage.cs
namespace ABC.Converter.Core.Models;
public sealed record ComicPage(
    string ArchivePath,
    string MediaType,
    int Width,
    int Height);

// ComicSource.cs
namespace ABC.Converter.Core.Models;
public sealed record ComicSource(
    string SourcePath,
    IReadOnlyList<ComicPage> Pages,
    int CoverIndex);

// ConversionRequest.cs
namespace ABC.Converter.Core.Models;
public sealed record ConversionRequest(
    string SourcePath,
    string OutputRoot,
    ComicMetadata Metadata,
    ReadingDirection Direction = ReadingDirection.LeftToRight);

// ConversionResult.cs
namespace ABC.Converter.Core.Models;
public sealed record ConversionResult(
    string SourcePath,
    string OutputPath,
    int PageCount);
```

- [ ] **Step 4: Implement the comparer**

Create `src/ABC.Converter.Core/Sorting/NaturalPathComparer.cs`:

```csharp
namespace ABC.Converter.Core.Sorting;

public sealed class NaturalPathComparer : IComparer<string>
{
    public static NaturalPathComparer Instance { get; } = new();

    public int Compare(string? left, string? right)
    {
        if (ReferenceEquals(left, right)) return 0;
        if (left is null) return -1;
        if (right is null) return 1;

        string a = left.Replace('\\', '/');
        string b = right.Replace('\\', '/');
        int ai = 0, bi = 0;

        while (ai < a.Length && bi < b.Length)
        {
            if (char.IsDigit(a[ai]) && char.IsDigit(b[bi]))
            {
                int aStart = ai, bStart = bi;
                while (ai < a.Length && char.IsDigit(a[ai])) ai++;
                while (bi < b.Length && char.IsDigit(b[bi])) bi++;

                string aDigits = a[aStart..ai].TrimStart('0');
                string bDigits = b[bStart..bi].TrimStart('0');
                if (aDigits.Length == 0) aDigits = "0";
                if (bDigits.Length == 0) bDigits = "0";

                int lengthResult = aDigits.Length.CompareTo(bDigits.Length);
                if (lengthResult != 0) return lengthResult;
                int numberResult = string.CompareOrdinal(aDigits, bDigits);
                if (numberResult != 0) return numberResult;
                continue;
            }

            int charResult = char.ToUpperInvariant(a[ai]).CompareTo(char.ToUpperInvariant(b[bi]));
            if (charResult != 0) return charResult;
            ai++;
            bi++;
        }

        return a.Length.CompareTo(b.Length);
    }
}
```

- [ ] **Step 5: Run tests**

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~NaturalPathComparerTests
```

Expected: two tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ABC.Converter.Core tests/ABC.Converter.Tests/Sorting
git commit -m "feat: add conversion models and natural page sorting"
```

---

### Task 3: CBZ Reader and Image Inspection

**Files:**
- Create: `src/ABC.Converter.Core/Archives/IComicArchiveReader.cs`
- Create: `src/ABC.Converter.Infrastructure/Archives/CbzArchiveReader.cs`
- Create: `tests/ABC.Converter.Tests/Archives/CbzArchiveReaderTests.cs`
- Create: `tests/ABC.Converter.Tests/Fixtures/FixtureArchive.cs`

**Interfaces:**
- Consumes: `ComicSource`, `ComicPage`, and `NaturalPathComparer.Instance`.
- Produces: `IComicArchiveReader.ReadAsync(string, CancellationToken)` and `ExtractPageAsync(string, ComicPage, Stream, CancellationToken)`.

- [ ] **Step 1: Define the archive-reader contract**

Create `src/ABC.Converter.Core/Archives/IComicArchiveReader.cs`:

```csharp
using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Archives;

public interface IComicArchiveReader
{
    Task<ComicSource> ReadAsync(string sourcePath, CancellationToken cancellationToken);
    Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Add a deterministic CBZ fixture helper and failing test**

Create `tests/ABC.Converter.Tests/Fixtures/FixtureArchive.cs`:

```csharp
using System.IO.Compression;

namespace ABC.Converter.Tests.Fixtures;

internal static class FixtureArchive
{
    private static readonly byte[] Png1x1 =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAusB9Y9Z7gAAAABJRU5ErkJggg==");

    public static string CreateCbz(params string[] entryNames)
    {
        string path = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.cbz");
        using ZipArchive zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (string name in entryNames)
        {
            ZipArchiveEntry entry = zip.CreateEntry(name);
            using Stream stream = entry.Open();
            stream.Write(Png1x1);
        }
        return path;
    }
}
```

Create `tests/ABC.Converter.Tests/Archives/CbzArchiveReaderTests.cs`:

```csharp
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;

namespace ABC.Converter.Tests.Archives;

public sealed class CbzArchiveReaderTests
{
    [Fact]
    public async Task ReadsSupportedImagesInNaturalOrder()
    {
        string path = FixtureArchive.CreateCbz(
            "__MACOSX/._page1.png", "page10.png", "notes.txt", "page2.png");
        try
        {
            var reader = new CbzArchiveReader();
            var source = await reader.ReadAsync(path, CancellationToken.None);
            Assert.Equal(["page2.png", "page10.png"], source.Pages.Select(p => p.ArchivePath));
            Assert.Equal(0, source.CoverIndex);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task RejectsArchiveWithNoComicPages()
    {
        string path = FixtureArchive.CreateCbz("notes.txt");
        try
        {
            var reader = new CbzArchiveReader();
            var error = await Assert.ThrowsAsync<InvalidDataException>(
                () => reader.ReadAsync(path, CancellationToken.None));
            Assert.Contains("supported page images", error.Message);
        }
        finally { File.Delete(path); }
    }
}
```

- [ ] **Step 3: Verify the tests fail**

Run:

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~CbzArchiveReaderTests
```

Expected: compilation fails because `CbzArchiveReader` does not exist.

- [ ] **Step 4: Implement CBZ reading**

Create `src/ABC.Converter.Infrastructure/Archives/CbzArchiveReader.cs`:

```csharp
using System.IO.Compression;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Sorting;
using SixLabors.ImageSharp;

namespace ABC.Converter.Infrastructure.Archives;

public sealed class CbzArchiveReader : IComicArchiveReader
{
    private static readonly IReadOnlyDictionary<string, string> MediaTypes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".png"] = "image/png",
            [".webp"] = "image/webp"
        };

    public async Task<ComicSource> ReadAsync(
        string sourcePath,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(Path.GetExtension(sourcePath), ".cbz", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only CBZ files are supported.");

        using ZipArchive zip = ZipFile.OpenRead(sourcePath);
        var pages = new List<ComicPage>();

        foreach (ZipArchiveEntry entry in zip.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = entry.FullName.Replace('\\', '/');
            if (normalized.StartsWith("__MACOSX/", StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileName(normalized).StartsWith("._", StringComparison.Ordinal))
                continue;

            string extension = Path.GetExtension(normalized);
            if (!MediaTypes.TryGetValue(extension, out string? mediaType))
                continue;

            await using Stream stream = entry.Open();
            (int width, int height) = await ReadDimensionsAsync(stream, mediaType, cancellationToken);
            pages.Add(new ComicPage(normalized, mediaType, width, height));
        }

        pages.Sort((a, b) => NaturalPathComparer.Instance.Compare(a.ArchivePath, b.ArchivePath));
        if (pages.Count == 0)
            throw new InvalidDataException("The CBZ contains no supported page images.");

        return new ComicSource(Path.GetFullPath(sourcePath), pages, 0);
    }

    public async Task ExtractPageAsync(
        string sourcePath,
        ComicPage page,
        Stream destination,
        CancellationToken cancellationToken)
    {
        using ZipArchive zip = ZipFile.OpenRead(sourcePath);
        ZipArchiveEntry entry = zip.GetEntry(page.ArchivePath)
            ?? throw new InvalidDataException($"Missing archive page: {page.ArchivePath}");
        await using Stream source = entry.Open();
        await source.CopyToAsync(destination, cancellationToken);
    }

    private static async Task<(int Width, int Height)> ReadDimensionsAsync(
        Stream stream,
        string mediaType,
        CancellationToken cancellationToken)
    {
        try
        {
            ImageInfo info = await Image.IdentifyAsync(stream, cancellationToken)
                ?? throw new InvalidDataException($"Cannot identify {mediaType} image.");
            return (info.Width, info.Height);
        }
        catch (UnknownImageFormatException exception)
        {
            throw new InvalidDataException($"Cannot read dimensions for {mediaType}.", exception);
        }
    }
}
```

Add one fixture and assertion for JPG, JPEG, PNG, and WebP. Add a truncated-image fixture and assert `InvalidDataException`. `Image.IdentifyAsync` inspects headers without decoding full image pixels.

- [ ] **Step 5: Run archive tests**

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~CbzArchiveReaderTests
```

Expected: all CBZ reader tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src/ABC.Converter.Core/Archives src/ABC.Converter.Infrastructure/Archives tests/ABC.Converter.Tests
git commit -m "feat: read and inspect CBZ comic pages"
```

---

### Task 4: Fixed-Layout EPUB Builder

**Files:**
- Create: `src/ABC.Converter.Core/Epub/IEpubBuilder.cs`
- Create: `src/ABC.Converter.Epub/FixedLayoutEpubBuilder.cs`
- Create: `tests/ABC.Converter.Tests/Epub/FixedLayoutEpubBuilderTests.cs`

**Interfaces:**
- Consumes: `IComicArchiveReader`, `ComicSource`, `ComicMetadata`, and `ReadingDirection`.
- Produces: `IEpubBuilder.BuildAsync(ComicSource, ComicMetadata, ReadingDirection, string, CancellationToken)`.

- [ ] **Step 1: Define the builder contract**

Create `src/ABC.Converter.Core/Epub/IEpubBuilder.cs`:

```csharp
using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Epub;

public interface IEpubBuilder
{
    Task BuildAsync(
        ComicSource source,
        ComicMetadata metadata,
        ReadingDirection direction,
        string destinationPath,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 2: Write a failing package-structure test**

Create `tests/ABC.Converter.Tests/Epub/FixedLayoutEpubBuilderTests.cs`:

```csharp
using System.IO.Compression;
using ABC.Converter.Core.Models;
using ABC.Converter.Epub;
using ABC.Converter.Infrastructure.Archives;
using ABC.Converter.Tests.Fixtures;

namespace ABC.Converter.Tests.Epub;

public sealed class FixedLayoutEpubBuilderTests
{
    [Fact]
    public async Task BuildsRequiredFixedLayoutEntries()
    {
        string cbz = FixtureArchive.CreateCbz("page1.png", "page2.png");
        string epub = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.epub");
        try
        {
            var reader = new CbzArchiveReader();
            var source = await reader.ReadAsync(cbz, CancellationToken.None);
            var builder = new FixedLayoutEpubBuilder(reader);
            await builder.BuildAsync(
                source,
                new ComicMetadata("Issue 1", "Example Series", "1"),
                ReadingDirection.LeftToRight,
                epub,
                CancellationToken.None);

            using ZipArchive zip = ZipFile.OpenRead(epub);
            Assert.Equal("mimetype", zip.Entries[0].FullName);
            Assert.Contains(zip.Entries, e => e.FullName == "META-INF/container.xml");
            Assert.Contains(zip.Entries, e => e.FullName == "META-INF/com.apple.ibooks.display-options.xml");
            Assert.Contains(zip.Entries, e => e.FullName == "EPUB/package.opf");
            Assert.Contains(zip.Entries, e => e.FullName == "EPUB/nav.xhtml");
            Assert.Contains(zip.Entries, e => e.FullName == "EPUB/pages/page-0001.xhtml");
            Assert.Contains(zip.Entries, e => e.FullName == "EPUB/images/page-0001.png");
        }
        finally
        {
            File.Delete(cbz);
            File.Delete(epub);
        }
    }
}
```

- [ ] **Step 3: Verify the test fails**

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~FixedLayoutEpubBuilderTests
```

Expected: compilation fails because `FixedLayoutEpubBuilder` does not exist.

- [ ] **Step 4: Implement the builder**

Create `src/ABC.Converter.Epub/FixedLayoutEpubBuilder.cs`. The implementation must:

```csharp
using System.IO.Compression;
using System.Text;
using System.Xml.Linq;
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Epub;

public sealed class FixedLayoutEpubBuilder(IComicArchiveReader archiveReader) : IEpubBuilder
{
    public async Task BuildAsync(
        ComicSource source,
        ComicMetadata metadata,
        ReadingDirection direction,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        if (File.Exists(destinationPath)) File.Delete(destinationPath);

        using FileStream file = File.Create(destinationPath);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false);
        WriteText(zip, "mimetype", "application/epub+zip", CompressionLevel.NoCompression);
        WriteText(zip, "META-INF/container.xml", ContainerXml());
        WriteText(zip, "META-INF/com.apple.ibooks.display-options.xml", AppleDisplayOptions());

        for (int index = 0; index < source.Pages.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ComicPage page = source.Pages[index];
            string number = (index + 1).ToString("D4");
            string extension = MediaExtension(page.MediaType);
            string imagePath = $"EPUB/images/page-{number}{extension}";
            ZipArchiveEntry imageEntry = zip.CreateEntry(imagePath, CompressionLevel.NoCompression);
            await using Stream imageOut = imageEntry.Open();
            await archiveReader.ExtractPageAsync(
                source.SourcePath, page, imageOut, cancellationToken);

            WriteText(
                zip,
                $"EPUB/pages/page-{number}.xhtml",
                PageXhtml(number, extension, page.Width, page.Height));
        }

        WriteText(zip, "EPUB/nav.xhtml", NavigationXhtml(metadata, source.Pages.Count));
        WriteText(zip, "EPUB/package.opf", PackageOpf(metadata, source, direction));
    }

    private static void WriteText(
        ZipArchive zip,
        string path,
        string content,
        CompressionLevel compression = CompressionLevel.Optimal)
    {
        ZipArchiveEntry entry = zip.CreateEntry(path, compression);
        using Stream stream = entry.Open();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false));
        writer.Write(content);
    }

    private static string MediaExtension(string mediaType) => mediaType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => throw new InvalidDataException($"Unsupported page media type: {mediaType}")
    };

    private static string ContainerXml()
    {
        XNamespace container = "urn:oasis:names:tc:opendocument:xmlns:container";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(container + "container",
                new XAttribute("version", "1.0"),
                new XElement(container + "rootfiles",
                    new XElement(container + "rootfile",
                        new XAttribute("full-path", "EPUB/package.opf"),
                        new XAttribute("media-type", "application/oebps-package+xml")))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string AppleDisplayOptions()
    {
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement("display_options",
                new XElement("platform",
                    new XAttribute("name", "*"),
                    new XElement("option",
                        new XAttribute("name", "fixed-layout"), "true"),
                    new XElement("option",
                        new XAttribute("name", "open-to-spread"), "false"))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string PageXhtml(
        string number,
        string extension,
        int width,
        int height)
    {
        XNamespace html = "http://www.w3.org/1999/xhtml";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(html + "html",
                new XElement(html + "head",
                    new XElement(html + "title", $"Page {number}"),
                    new XElement(html + "meta",
                        new XAttribute("name", "viewport"),
                        new XAttribute("content", $"width={width},height={height}")),
                    new XElement(html + "style",
                        "html,body{margin:0;padding:0;width:100%;height:100%;overflow:hidden}" +
                        "img{display:block;width:100%;height:100%;object-fit:contain}")),
                new XElement(html + "body",
                    new XElement(html + "img",
                        new XAttribute("src", $"../images/page-{number}{extension}"),
                        new XAttribute("alt", $"Page {int.Parse(number)}")))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string NavigationXhtml(ComicMetadata metadata, int pageCount)
    {
        XNamespace html = "http://www.w3.org/1999/xhtml";
        XNamespace epub = "http://www.idpf.org/2007/ops";
        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(html + "html",
                new XAttribute(XNamespace.Xmlns + "epub", epub),
                new XElement(html + "head",
                    new XElement(html + "title", metadata.Title)),
                new XElement(html + "body",
                    new XElement(html + "nav",
                        new XAttribute(epub + "type", "toc"),
                        new XElement(html + "h1", metadata.Title),
                        new XElement(html + "ol",
                            Enumerable.Range(1, pageCount).Select(index =>
                                new XElement(html + "li",
                                    new XElement(html + "a",
                                        new XAttribute("href", $"pages/page-{index:D4}.xhtml"),
                                        $"Page {index}"))))))))
            .ToString(SaveOptions.DisableFormatting);
    }

    private static string PackageOpf(
        ComicMetadata metadata,
        ComicSource source,
        ReadingDirection direction)
    {
        XNamespace opf = "http://www.idpf.org/2007/opf";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        string progression = direction == ReadingDirection.RightToLeft ? "rtl" : "ltr";
        string identifier = $"urn:uuid:{Guid.NewGuid():D}";

        var manifest = new List<XElement>
        {
            new(opf + "item",
                new XAttribute("id", "nav"),
                new XAttribute("href", "nav.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml"),
                new XAttribute("properties", "nav"))
        };
        var spine = new List<XElement>();

        for (int index = 0; index < source.Pages.Count; index++)
        {
            ComicPage page = source.Pages[index];
            string number = (index + 1).ToString("D4");
            string extension = MediaExtension(page.MediaType);
            manifest.Add(new XElement(opf + "item",
                new XAttribute("id", $"image-{number}"),
                new XAttribute("href", $"images/page-{number}{extension}"),
                new XAttribute("media-type", page.MediaType),
                index == source.CoverIndex ? new XAttribute("properties", "cover-image") : null));
            manifest.Add(new XElement(opf + "item",
                new XAttribute("id", $"page-{number}"),
                new XAttribute("href", $"pages/page-{number}.xhtml"),
                new XAttribute("media-type", "application/xhtml+xml")));
            spine.Add(new XElement(opf + "itemref",
                new XAttribute("idref", $"page-{number}")));
        }

        return new XDocument(
            new XDeclaration("1.0", "utf-8", null),
            new XElement(opf + "package",
                new XAttribute("version", "3.0"),
                new XAttribute("unique-identifier", "pub-id"),
                new XElement(opf + "metadata",
                    new XElement(dc + "identifier",
                        new XAttribute("id", "pub-id"), identifier),
                    new XElement(dc + "title", metadata.Title),
                    new XElement(dc + "language", metadata.Language),
                    new XElement(opf + "meta",
                        new XAttribute("property", "belongs-to-collection"), metadata.Series),
                    new XElement(opf + "meta",
                        new XAttribute("property", "collection-type"), "series"),
                    new XElement(opf + "meta",
                        new XAttribute("property", "rendition:layout"), "pre-paginated"),
                    new XElement(opf + "meta",
                        new XAttribute("property", "rendition:orientation"), "auto"),
                    new XElement(opf + "meta",
                        new XAttribute("property", "rendition:spread"), "auto")),
                new XElement(opf + "manifest", manifest),
                new XElement(opf + "spine",
                    new XAttribute("page-progression-direction", progression),
                    spine)))
            .ToString(SaveOptions.DisableFormatting);
    }
}
```

The XML methods above declare EPUB 3, a unique identifier, language, title, series metadata, fixed-layout rendition values, cover, navigation, all page documents, all images, and the selected progression direction. `XElement` escapes metadata values.

- [ ] **Step 5: Add assertions for XML semantics**

Extend `FixedLayoutEpubBuilderTests` to read `package.opf` and assert:

```csharp
string package;
using (var reader = new StreamReader(zip.GetEntry("EPUB/package.opf")!.Open()))
    package = await reader.ReadToEndAsync();
Assert.Contains("pre-paginated", package);
Assert.Contains("page-progression-direction=\"ltr\"", package);
Assert.Contains("properties=\"cover-image\"", package);
Assert.Contains("properties=\"nav\"", package);
```

Add a second test using `ReadingDirection.RightToLeft` and assert `page-progression-direction="rtl"`.

- [ ] **Step 6: Run EPUB tests**

```powershell
dotnet test tests/ABC.Converter.Tests --filter FullyQualifiedName~FixedLayoutEpubBuilderTests
```

Expected: all fixed-layout EPUB tests pass.

- [ ] **Step 7: Commit**

```powershell
git add src/ABC.Converter.Core/Epub src/ABC.Converter.Epub tests/ABC.Converter.Tests/Epub
git commit -m "feat: build fixed-layout Apple Books EPUB files"
```

---

### Task 5: EPUB Validation and Atomic Output Organization

**Files:**
- Create: `src/ABC.Converter.Core/Epub/IEpubValidator.cs`
- Create: `src/ABC.Converter.Core/Files/IOutputPathPolicy.cs`
- Create: `src/ABC.Converter.Epub/EpubStructureValidator.cs`
- Create: `src/ABC.Converter.Infrastructure/Files/SeriesOutputPathPolicy.cs`
- Create: `src/ABC.Converter.Infrastructure/Conversion/ComicConversionService.cs`
- Create: `tests/ABC.Converter.Tests/Epub/EpubStructureValidatorTests.cs`
- Create: `tests/ABC.Converter.Tests/Files/SeriesOutputPathPolicyTests.cs`
- Create: `tests/ABC.Converter.Tests/Conversion/ComicConversionServiceTests.cs`

**Interfaces:**
- Consumes: `IComicArchiveReader`, `IEpubBuilder`, and core conversion models.
- Produces: `IEpubValidator.ValidateAsync`, `IOutputPathPolicy.GetOutputPath`, and `ComicConversionService.ConvertAsync`.

- [ ] **Step 1: Define validation and path contracts**

Create `src/ABC.Converter.Core/Epub/IEpubValidator.cs`:

```csharp
namespace ABC.Converter.Core.Epub;
public interface IEpubValidator
{
    Task ValidateAsync(string epubPath, CancellationToken cancellationToken);
}
```

Create `src/ABC.Converter.Core/Files/IOutputPathPolicy.cs`:

```csharp
using ABC.Converter.Core.Models;

namespace ABC.Converter.Core.Files;
public interface IOutputPathPolicy
{
    string GetOutputPath(string outputRoot, ComicMetadata metadata);
}
```

- [ ] **Step 2: Write failing path-policy tests**

Create `tests/ABC.Converter.Tests/Files/SeriesOutputPathPolicyTests.cs`:

```csharp
using ABC.Converter.Core.Models;
using ABC.Converter.Infrastructure.Files;

namespace ABC.Converter.Tests.Files;

public sealed class SeriesOutputPathPolicyTests
{
    [Fact]
    public void OrganizesBySeriesAndVolume()
    {
        var policy = new SeriesOutputPathPolicy();
        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Pilot", "Example: Series", "2", "1", 2024));
        Assert.Equal(
            Path.Combine(@"D:\Books", "Example Series", "Volume 1", "Example Series - 002 - Pilot.epub"),
            path);
    }

    [Fact]
    public void FallsBackToYearAndOmitsMissingIssue()
    {
        var policy = new SeriesOutputPathPolicy();
        string path = policy.GetOutputPath(
            @"D:\Books",
            new ComicMetadata("Annual", "Example Series", PublicationYear: 2024));
        Assert.Equal(
            Path.Combine(@"D:\Books", "Example Series", "2024", "Example Series - Annual.epub"),
            path);
    }
}
```

- [ ] **Step 3: Implement deterministic output paths**

Create `src/ABC.Converter.Infrastructure/Files/SeriesOutputPathPolicy.cs` with:

```csharp
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Files;

public sealed class SeriesOutputPathPolicy : IOutputPathPolicy
{
    public string GetOutputPath(string outputRoot, ComicMetadata metadata)
    {
        string series = Sanitize(metadata.Series);
        string? group = metadata.Volume is { Length: > 0 }
            ? $"Volume {Sanitize(metadata.Volume)}"
            : metadata.PublicationYear?.ToString("0000");

        var parts = new List<string> { series };
        if (int.TryParse(metadata.IssueNumber, out int issue)) parts.Add(issue.ToString("D3"));
        if (!string.IsNullOrWhiteSpace(metadata.Title)) parts.Add(Sanitize(metadata.Title));
        string fileName = string.Join(" - ", parts) + ".epub";

        return group is null
            ? Path.Combine(outputRoot, series, fileName)
            : Path.Combine(outputRoot, series, group, fileName);
    }

    private static string Sanitize(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string clean = new(value.Where(c => !invalid.Contains(c)).ToArray());
        clean = clean.Trim().TrimEnd('.');
        return string.IsNullOrWhiteSpace(clean) ? "Unknown" : clean;
    }
}
```

- [ ] **Step 4: Implement structural validation**

Create `src/ABC.Converter.Epub/EpubStructureValidator.cs`. It must open the ZIP, verify `mimetype` is the first entry and stored without compression, verify its exact content, parse `container.xml`, resolve the declared package path, parse the package, verify every manifest `href` exists, ensure one nav and one cover-image declaration, ensure spine references resolve, ensure every page XHTML parses, and throw `InvalidDataException` with the failed rule.

Write focused tests in `EpubStructureValidatorTests.cs` for a valid EPUB, missing package, missing manifest resource, malformed XHTML, and a compressed or misplaced mimetype.

- [ ] **Step 5: Implement the conversion coordinator**

Create `src/ABC.Converter.Infrastructure/Conversion/ComicConversionService.cs`:

```csharp
using ABC.Converter.Core.Archives;
using ABC.Converter.Core.Epub;
using ABC.Converter.Core.Files;
using ABC.Converter.Core.Models;

namespace ABC.Converter.Infrastructure.Conversion;

public sealed class ComicConversionService(
    IComicArchiveReader archiveReader,
    IEpubBuilder epubBuilder,
    IEpubValidator validator,
    IOutputPathPolicy outputPathPolicy)
{
    public async Task<ConversionResult> ConvertAsync(
        ConversionRequest request,
        CancellationToken cancellationToken)
    {
        string sourceFullPath = Path.GetFullPath(request.SourcePath);
        DateTime sourceWriteTime = File.GetLastWriteTimeUtc(sourceFullPath);
        long sourceLength = new FileInfo(sourceFullPath).Length;
        ComicSource source = await archiveReader.ReadAsync(sourceFullPath, cancellationToken);
        string outputPath = outputPathPolicy.GetOutputPath(request.OutputRoot, request.Metadata);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        string tempPath = Path.Combine(
            Path.GetDirectoryName(outputPath)!,
            $".{Path.GetFileName(outputPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await epubBuilder.BuildAsync(
                source, request.Metadata, request.Direction, tempPath, cancellationToken);
            await validator.ValidateAsync(tempPath, cancellationToken);

            var sourceInfo = new FileInfo(sourceFullPath);
            if (sourceInfo.Length != sourceLength ||
                sourceInfo.LastWriteTimeUtc != sourceWriteTime)
                throw new IOException("The source CBZ changed during conversion.");

            if (File.Exists(outputPath))
                throw new IOException($"Output already exists: {outputPath}");
            File.Move(tempPath, outputPath);
            return new ConversionResult(sourceFullPath, outputPath, source.Pages.Count);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }
}
```

- [ ] **Step 6: Write an end-to-end coordinator test**

In `ComicConversionServiceTests.cs`, create a two-page fixture, record its SHA-256 hash, convert it, assert the EPUB exists and validates, assert the source hash is unchanged, and assert no `.tmp` files remain. Add a validator stub that throws and assert no final EPUB exists.

- [ ] **Step 7: Run focused tests**

```powershell
dotnet test tests/ABC.Converter.Tests --filter "FullyQualifiedName~EpubStructureValidatorTests|FullyQualifiedName~SeriesOutputPathPolicyTests|FullyQualifiedName~ComicConversionServiceTests"
```

Expected: all validation, path, and coordinator tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src tests
git commit -m "feat: validate and atomically publish converted comics"
```

---

### Task 6: Queue-Focused WPF Shell

**Files:**
- Modify: `src/ABC.Converter.App/App.xaml`
- Modify: `src/ABC.Converter.App/App.xaml.cs`
- Modify: `src/ABC.Converter.App/MainWindow.xaml`
- Modify: `src/ABC.Converter.App/MainWindow.xaml.cs`
- Create: `src/ABC.Converter.App/ViewModels/MainWindowViewModel.cs`
- Create: `src/ABC.Converter.App/ViewModels/ConversionJobViewModel.cs`
- Create: `src/ABC.Converter.App/Commands/AsyncCommand.cs`
- Create: `src/ABC.Converter.App/Services/FolderPicker.cs`

**Interfaces:**
- Consumes: `ComicConversionService`, `ConversionRequest`, and `ComicMetadata`.
- Produces: An interactive WPF app that accepts CBZ files and folders and displays in-memory conversion progress.

- [ ] **Step 1: Add the queue view model**

Create `ConversionJobViewModel` with properties `SourcePath`, `Series`, `Status`, `Progress`, `ErrorMessage`, and `OutputPath`, all raising `INotifyPropertyChanged`.

Create `MainWindowViewModel` with:

```csharp
public ObservableCollection<ConversionJobViewModel> Jobs { get; } = [];
public string OutputRoot { get; set; }
public ICommand AddFilesCommand { get; }
public ICommand StartCommand { get; }
public ICommand CancelSelectedCommand { get; }
public Task AddPathsAsync(IEnumerable<string> paths);
public Task StartAsync(CancellationToken cancellationToken);
```

`AddPathsAsync` recursively enumerates `.cbz` files, uses canonical paths for duplicate suppression, and adds jobs without reading archive contents on the UI thread.

- [ ] **Step 2: Compose services at application startup**

In `App.xaml.cs`, construct one `CbzArchiveReader`, `FixedLayoutEpubBuilder`, `EpubStructureValidator`, `SeriesOutputPathPolicy`, and `ComicConversionService`. Pass the coordinator into `MainWindowViewModel`, assign it to `MainWindow.DataContext`, and show the window.

- [ ] **Step 3: Build the approved queue-focused layout**

Replace `MainWindow.xaml` with a responsive grid containing:

- App title with Settings and Help buttons.
- A bordered drag target and Add Comics button.
- Four status-count cards.
- A `DataGrid` with Comic, Series, Status, Progress, and Output columns.
- Output-root display and Browse button.
- Start, Pause, Cancel Selected, Retry Selected, and Retry All Failed buttons.
- A collapsible selected-job detail panel.

Set `AllowDrop="True"` and wire `DragOver` and `Drop` in `MainWindow.xaml.cs`. Accept file and folder drops, show the copy cursor only when at least one dropped path can contain CBZ input, then call `AddPathsAsync`.

- [ ] **Step 4: Keep conversion work off the UI thread**

`StartAsync` must process one queued job at a time in this plan, set status values in order, call `ComicConversionService.ConvertAsync`, and marshal property updates through the captured WPF dispatcher. It must catch `OperationCanceledException` as Cancelled and other exceptions as Failed with the exception message.

- [ ] **Step 5: Run the desktop smoke test**

```powershell
dotnet build ABC.Converter.sln
dotnet run --project src/ABC.Converter.App
```

Manual verification:

1. Drop a valid two-page CBZ.
2. Confirm one queue row appears.
3. Choose a temporary output folder.
4. Click Start.
5. Confirm the interface remains responsive.
6. Confirm the status becomes Completed.
7. Open the produced EPUB as a ZIP and confirm both pages exist.
8. Hash the CBZ before and after and confirm the hashes match.

- [ ] **Step 6: Commit**

```powershell
git add src/ABC.Converter.App
git commit -m "feat: add queue-focused WPF conversion shell"
```

---

### Task 7: Foundation Acceptance Suite and Documentation

**Files:**
- Create: `tests/ABC.Converter.Tests/Acceptance/FoundationAcceptanceTests.cs`
- Create: `tests/ABC.Converter.Tests/Fixtures/Comics/README.md`
- Create: `README.md`
- Create: `scripts/Run.ps1`
- Modify: `scripts/Test.ps1`

**Interfaces:**
- Consumes: The complete foundation conversion pipeline.
- Produces: Repeatable acceptance evidence and user-facing PowerShell commands.

- [ ] **Step 1: Add acceptance tests**

Create `FoundationAcceptanceTests.cs` with parameterized fixtures covering uppercase `.CBZ`, nested page folders, page2 before page10, PNG, JPEG, WebP, Unicode filenames, spaces, left-to-right, right-to-left, corrupt ZIP input, no supported pages, cancellation cleanup, and source SHA-256 preservation.

Each success case must build an EPUB, run `EpubStructureValidator`, open the package, and assert page count, progression direction, page dimensions, cover declaration, and source hash.

- [ ] **Step 2: Add a runnable PowerShell entry point**

Create `scripts/Run.ps1`:

```powershell
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
dotnet run --project "$PSScriptRoot\..\src\ABC.Converter.App\ABC.Converter.App.csproj"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

- [ ] **Step 3: Strengthen the test script**

Update `scripts/Test.ps1` to collect results:

```powershell
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Debug')
$ErrorActionPreference = 'Stop'
$results = Join-Path $PSScriptRoot '..\TestResults'
New-Item -ItemType Directory -Force -Path $results | Out-Null
dotnet test "$PSScriptRoot\..\ABC.Converter.sln" `
    --configuration $Configuration `
    --no-build `
    --logger "trx;LogFileName=abc-converter.trx" `
    --results-directory $results
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
```

- [ ] **Step 4: Document exact Windows commands**

Create `README.md` with prerequisites, `.\scripts\Setup.ps1`, `.\scripts\Build.ps1`, `.\scripts\Test.ps1`, and `.\scripts\Run.ps1`. Document the foundation slice's accepted inputs, output organization, reading-direction choice, source-file guarantee, and known scope boundary that queue persistence and ComicVine arrive in later plans.

- [ ] **Step 5: Run the full verification**

```powershell
.\scripts\Setup.ps1
.\scripts\Build.ps1 -Configuration Release
.\scripts\Test.ps1 -Configuration Release
```

Expected: zero warnings, zero build errors, and all tests pass.

- [ ] **Step 6: Commit**

```powershell
git add README.md scripts tests
git commit -m "test: qualify ABC Converter foundation MVP"
```

## Completion Gate

The foundation MVP is complete only when:

- The Release solution builds with zero warnings.
- All automated tests pass.
- Drag and drop accepts a valid CBZ and folder.
- A valid CBZ produces a structurally valid fixed-layout EPUB.
- Left-to-right and right-to-left output both pass.
- Page order and dimensions match the source.
- The source SHA-256 hash is unchanged.
- Failed and cancelled jobs leave no final EPUB or temporary files.
- The PowerShell scripts work from a clean Windows 10 22H2 or Windows 11 environment with the .NET 8 SDK.
