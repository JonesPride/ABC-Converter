# ABC Converter

Apple Books Comic Converter converts CBZ comics into fixed-layout EPUB 3 files
for Apple Books. This repository contains the foundation Windows desktop
slice: add CBZ files or folders, choose an output location, run the queue, and
inspect each job's result.

## Requirements

- Windows 10 version 22H2 or Windows 11 on an x64 PC
- .NET 8 SDK for x64
- Windows PowerShell 5.1 or PowerShell 7

The developer workflow does not require Visual Studio, Bash, WSL, or
`winget`. Confirm the SDK before setup:

```powershell
dotnet --version
```

The command must report an installed .NET 8 SDK.

## Build, test, and run

Open PowerShell in the repository root and run:

```powershell
Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
.\scripts\Setup.ps1
.\scripts\Build.ps1 -Configuration Release
.\scripts\Test.ps1 -Configuration Release
.\scripts\Run.ps1
```

The process-scoped execution policy ends when you close that PowerShell
session. An organization policy may prevent this override; contact your
administrator if PowerShell still blocks the scripts.

`Setup.ps1` checks for .NET 8 and restores NuGet packages. `Build.ps1` builds
the solution with warnings treated as errors. `Test.ps1` uses the existing
build, so run `Build.ps1` first. Test results are written to
`TestResults\abc-converter.trx`.

The build and test scripts use `Debug` when you omit `-Configuration`.
`Run.ps1` starts the WPF application from
`src\ABC.Converter.App\ABC.Converter.App.csproj`.

## Convert comics

1. Drag CBZ files or folders onto the window, or use **Add Comics** and
   **Add Folder**.
2. Check the output location. The default is
   `Documents\ABC Converter`.
3. Select **Start**.

Folder input uses recursive scanning. The scanner accepts files whose
extension is `.cbz`, without case sensitivity. It ignores CBR, PDF, plain ZIP,
and loose image folders. A CBZ may contain:

- JPG or JPEG pages
- PNG pages
- WebP pages

The reader ignores unrelated archive entries. It fails a corrupt archive or a
CBZ with no supported page images. Page paths use natural ordering, so
`page2.jpg` comes before `page10.jpg`, including pages in nested folders.
Unicode and spaces are supported in file and archive-entry names.

## EPUB output

The converter creates one fixed-layout EPUB 3 publication per CBZ. Each EPUB
contains Apple Books display options, one XHTML document per page, the source
page dimensions, and a declared cover. The converter validates the package
before publishing the final file.

The output path follows this pattern:

```text
<Output Root>\<Series>\<Volume N or YYYY>\<Series> - <Issue> - <Title>.epub
```

The converter omits the volume/year folder, issue, or title segment when the
metadata has no value for it. It removes Windows-reserved characters from
path components and uses a stable suffix when the preferred output name
already exists.

The foundation desktop app converts left to right by default. The conversion
engine supports and tests both left-to-right and right-to-left EPUB output.
A later interface milestone will expose the per-comic reading-direction
choice.

## Source-file guarantee

ABC Converter treats each source CBZ as read-only. It does not edit, rename,
move, or delete source files. Before publication, the converter checks the
source length and modification time and rejects the job if either changed
during conversion. Acceptance tests compare the source SHA-256 before and
after conversion.

A failed or cancelled conversion does not publish a new final EPUB and removes
its temporary build file.

## Foundation scope

This slice keeps the queue in memory and processes jobs in sequence. Closing
the app discards unfinished queue state. Queue persistence, concurrent
workers, pause/resume, recovery, and durable logs belong to the next plan.

ComicInfo.xml metadata, ComicVine matching, result caching, and metadata
review also belong to later plans. The current converter derives its basic
title and series values from the source filename.

## License

ABC Converter's original code is licensed under the [MIT License](LICENSE).
Third-party dependencies, including SkiaSharp and its native Skia libraries, retain their own
licenses and usage requirements; the project's MIT license does not replace them.
