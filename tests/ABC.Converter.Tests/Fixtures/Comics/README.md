# Foundation comic fixtures

The foundation acceptance suite creates its CBZ inputs at test time. It uses
deterministic, dimension-specific PNG, JPEG, and WebP images from
`FixtureArchive`; no copyrighted comic pages or binary fixture archives are
stored in the repository.

`FoundationAcceptanceTests` covers:

- uppercase `.CBZ` input;
- nested page folders and natural ordering (`page2` before `page10`);
- PNG, `.jpg`, `.jpeg`, and WebP pages;
- Unicode and spaces in archive and entry names;
- left-to-right and right-to-left page progression;
- corrupt ZIP input and an archive with no supported pages;
- cancellation cleanup; and
- byte-for-byte source preservation through SHA-256 checks.

Every successful fixture runs through `CbzArchiveReader`,
`FixedLayoutEpubBuilder`, `EpubStructureValidator`,
`SeriesOutputPathPolicy`, and `ComicConversionService`. The resulting EPUB is
then opened to verify its spine page count, progression direction, image
manifest and cover declaration, packaged image bytes, and each page viewport.
The success matrix uses distinct non-square source dimensions and compares
each known width and height with both the archive scan and its matching XHTML
viewport.
Each case runs in an isolated temporary directory, verifies that the source
archive is unchanged, and removes the directory afterward.

Add new compatibility cases as generated fixtures unless exact external
bytes are essential to reproduce a format defect. If a binary fixture is
needed, keep it minimal, document its provenance and license here, and add a
source hash so accidental replacement is visible.
