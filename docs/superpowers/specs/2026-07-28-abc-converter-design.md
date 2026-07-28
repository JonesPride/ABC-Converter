# Apple Books Comic Converter Design

## 1. Product Definition

Apple Books Comic Converter, shortened to ABC Converter, is a Windows desktop application for converting large collections of CBZ comics into fixed-layout EPUB 3 files optimized for Apple Books.

Version 1 is intended for personal use on one Windows PC. It supports Windows 10 version 22H2 and Windows 11 on 64-bit systems.

### Version 1 goals

- Accept individual CBZ files and folders containing CBZ files.
- Support drag and drop and file or folder pickers.
- Reliably queue at least 10,000 comics.
- Resume unfinished work after application closure, interruption, or restart.
- Read embedded ComicInfo.xml metadata.
- Match comics against ComicVine.
- Automatically accept high-confidence ComicVine matches.
- Route uncertain matches to a review screen.
- Create validated, fixed-layout EPUB 3 files for Apple Books.
- Extract and assign a cover.
- Save output into organized series folders.
- Leave every source CBZ unchanged.
- Provide clear progress, logs, retry controls, installation, and updates.

### Explicitly outside version 1

- CBR, PDF, ZIP without a CBZ extension, and loose image-folder input.
- macOS, Linux, ARM64, and 32-bit Windows.
- Cloud conversion, account synchronization, and multi-user use.
- Editing, deleting, renaming, copying, or moving source CBZ files.
- A public app-store release or paid code-signing certificate.

## 2. Technical Direction

ABC Converter will use C#, .NET 8, and WPF. Every developer workflow will have a PowerShell command or script, including setup, restore, build, test, packaging, and release.

The application will be a modular desktop app with background conversion workers in the same process. Module boundaries must allow the conversion worker to move into a separate process in a future version without changing the queue or user interface contracts.

### Solution modules

- `ABC.Converter.App`: WPF windows, drag and drop, commands, settings, progress, logs, and review screens.
- `ABC.Converter.Core`: queue rules, job state, metadata models, matching policy, naming rules, validation contracts, and interfaces.
- `ABC.Converter.Infrastructure`: CBZ access, filesystem operations, SQLite persistence, ComicVine HTTP access, protected secrets, update checks, and logging.
- `ABC.Converter.Epub`: fixed-layout EPUB construction and EPUB-specific validation.
- `ABC.Converter.Tests`: unit, integration, compatibility, recovery, and performance tests.

Dependencies point inward. App, Infrastructure, and Epub may depend on Core. Core must not depend on WPF, SQLite, HTTP, archive implementations, or EPUB packaging.

## 3. Main User Experience

The approved main window uses a queue-focused layout.

### Main window

- Header with application name, Settings, and Help.
- Large drag-and-drop target with an Add Comics button.
- Batch counters for Queued, Completed, Needs Review, and Failed.
- Queue grid showing comic, detected series, status, progress, and current stage.
- Overall batch progress.
- Output location.
- Start, Pause, Cancel Selected, Retry Selected, and Retry All Failed actions.
- Search and status filters.
- Expandable details and logs for the selected job.

### Supporting screens

- Metadata Review for uncertain ComicVine results and manual metadata editing.
- Settings for output location, ComicVine API key, worker count, update preferences, filename pattern, and default reading direction.
- History for completed jobs and reconversion.
- Update dialog showing the available version and release notes.

The interface remains responsive while scanning, matching, extracting, building, validating, or writing files. Every long operation supports progress reporting and cancellation where the underlying operation can stop safely.

## 4. End-to-End Conversion Flow

1. The user drops CBZ files or folders or chooses them with a picker.
2. The scanner recursively finds files whose extension is `.cbz`, case-insensitively.
3. The scanner rejects duplicate queue entries using canonical source path plus a file fingerprint.
4. Each accepted file becomes a persisted queue job.
5. The metadata pipeline reads ComicInfo.xml when present and parses useful values from the filename.
6. ComicVine lookup searches with available series, issue, volume, year, and title values.
7. A high-confidence match continues automatically.
8. An uncertain or conflicting match enters Needs Review without blocking unrelated jobs.
9. The converter opens the CBZ, verifies its entries, and produces a naturally sorted page list.
10. The first eligible comic page becomes the cover unless ComicInfo.xml explicitly identifies a different cover page.
11. The EPUB builder creates the complete publication in a job-specific temporary directory.
12. The validator checks the package before the output is published.
13. The publisher writes the finished EPUB atomically into its organized series folder.
14. The job becomes Completed and its history is saved.
15. Temporary files are removed after success, cancellation, or handled failure.

## 5. Input and Page Handling

### Accepted input

- Files with a `.cbz` extension.
- Folders scanned recursively for `.cbz` files.
- Images inside the CBZ with JPG, JPEG, PNG, or WebP extensions.
- ComicInfo.xml when present at the archive root.

macOS metadata, thumbnails, hidden files, directory entries, and unrelated archive entries are ignored.

### Page order

Pages use a deterministic natural sort that treats numeric runs as numbers. For example, `page2.jpg` precedes `page10.jpg`. When paths contain folders, sorting compares normalized relative paths segment by segment.

The converter preserves each image's original pixels and aspect ratio. It does not recompress images in version 1 unless a WebP compatibility conversion is required by the selected Apple Books compatibility profile. Any required conversion preserves the original dimensions and uses a quality setting that avoids visible comic-art degradation.

An archive with no supported page images fails during scanning with a clear error.

## 6. Metadata and ComicVine

### Metadata priority

1. User edits made in Metadata Review.
2. Valid ComicInfo.xml fields.
3. Accepted ComicVine fields.
4. Filename parsing.
5. Safe fallback values.

Higher-priority values are not silently overwritten by lower-priority sources. The review screen shows the source of each value.

### Stored publication metadata

- Title
- Series
- Issue number
- Volume
- Publication year and release date
- Publisher and imprint
- Writers, artists, colorists, letterers, editors, and other credited creators
- Description or summary
- Genres and tags
- Language
- ComicVine issue identifier and source URL
- Reading direction

### Matching policy

Matching uses normalized series name, issue number, volume, publication year, and title. A match may be accepted automatically only when it exceeds the configured high-confidence threshold and has no material conflict with an existing ComicInfo.xml issue number or series.

Ambiguous results, conflicting issue numbers, missing required identifiers, request failures that exhaust retries, and low-confidence results enter Needs Review. The user can choose a result, edit fields, skip online metadata, or retry the search.

### API key and network behavior

The ComicVine API key is encrypted for the current Windows user through Windows-protected user storage. It never appears in an EPUB, exported log, crash report, command-line argument, or plaintext settings file.

ComicVine responses are cached in SQLite. Requests use rate limiting, bounded retries with backoff for temporary failures, and cancellation. Authentication and permanent request errors do not retry indefinitely.

## 7. Fixed-Layout EPUB 3 Output

Each output is an EPUB 3 publication with:

- The required uncompressed `mimetype` file as the first ZIP entry.
- A valid container document.
- A package document with manifest, spine, metadata, and fixed-layout declarations.
- One XHTML document per comic page.
- One image resource per page.
- A navigation document.
- A cover image and cover metadata.
- Apple Books display options.
- A unique publication identifier.
- Declared language and page progression direction.

Every page XHTML document defines a viewport matching the page image dimensions and renders one full-page image without margins. The publication uses pre-paginated rendition metadata.

Left-to-right is the default. A comic can be changed to right-to-left before conversion or reconverted with a different direction.

### Output organization

The default output root is selected by the user. Output is organized as:

`<Output Root>\<Sanitized Series Name>\<Volume or Publication Year>\<Sanitized Series Name> - <Padded Issue Number> - <Sanitized Title>.epub`

The optional second-level folder uses `Volume <Number>` when a volume is known, otherwise the four-digit publication year when known. It is omitted when neither value is available. If issue number or title is unavailable, the filename omits that segment rather than inserting placeholder text. Windows-reserved characters and names are sanitized deterministically. Collisions add a stable differentiator instead of overwriting an unrelated EPUB.

Source CBZ files remain in their original locations and are never modified.

### Safe publication

The builder writes into a unique temporary work directory. Validation must pass before publication. The final move into the output directory is atomic when the filesystem supports it. Existing output is replaced only through an explicit Reconvert action for the same recorded job. A failed build never leaves a partial file with the final `.epub` name.

## 8. Queue, Concurrency, and Recovery

SQLite stores jobs, state transitions, metadata decisions, output records, settings, cached ComicVine results, and history.

### Job states

- Queued
- Scanning
- Fetching Metadata
- Needs Review
- Extracting
- Building EPUB
- Validating
- Publishing
- Completed
- Cancelled
- Failed

Every transition is persisted. On startup, jobs left in an active state are returned to Queued after temporary work is inspected and cleaned. Completed jobs remain completed when their recorded output still exists.

The default worker count is calculated from available CPU and memory, with a manual setting. Workers process separate comics concurrently. Work for one comic is sequential where ordering or archive safety requires it.

Pause prevents new jobs from starting and allows active jobs to finish safely. Cancel Selected requests cancellation, removes that job's temporary output, and records Cancelled. Application shutdown stops scheduling, requests cancellation, persists state, and exits after a bounded cleanup period.

Failed jobs record the stage, user-facing message, technical detail, time, and retryability. Retrying restarts at the earliest safe stage and does not rerun completed jobs.

## 9. Logging and Diagnostics

The queue shows concise user-facing status. The detail panel provides timestamped structured logs for the selected job.

Logs support:

- Filtering by severity and stage.
- Copying selected entries.
- Exporting a diagnostic log.
- Automatic retention limits.
- Redaction of the ComicVine API key and other sensitive values.

The application log records startup, database migration, update checks, worker lifecycle, and unexpected failures. Logs do not contain comic page bytes.

## 10. Installation and Updates

ABC Converter publishes as a self-contained Windows x64 application. The target PC does not need a separate .NET installation.

The installer creates Start menu and optional desktop shortcuts. Uninstall preserves settings, queue history, and cached metadata by default, with an explicit option to remove application data.

GitHub Releases hosts full installers, update packages, version metadata, checksums, and release notes. On launch, the application may check for updates according to user settings. It presents the available version and release notes, then waits for user approval before download and installation.

An update failure leaves the installed version usable. Update packages are verified before installation. Version 1 supports one stable update channel.

Because version 1 is for personal use, code signing is not required. The build and release design must allow signing to be added later without changing application architecture.

## 11. Error Handling

Errors are isolated to the affected job whenever possible. One corrupt comic, failed lookup, invalid image, full disk, permission failure, or validation failure does not stop unrelated jobs.

User-facing errors state:

- What failed.
- Which comic was affected.
- The stage where it failed.
- Whether retry can help.
- A practical next action.

Global errors such as an unavailable database or unwritable application-data directory block startup with a recovery message rather than risking data loss.

## 12. Testing and Acceptance Criteria

### Automated tests

- Unit tests for natural sorting, filename parsing, sanitization, matching confidence, metadata precedence, state transitions, and reading direction.
- Integration tests for CBZ reading, ComicInfo.xml parsing, SQLite persistence, ComicVine caching and retry behavior, EPUB building, validation, and atomic publication.
- Recovery tests for interrupted active jobs, cancellation, queue restoration, missing completed output, and retry.
- Compatibility fixtures for JPG, JPEG, PNG, WebP, mixed image formats, nested archive paths, Unicode names, spaces, long paths, and corrupt archives.
- Golden-file checks for package metadata, manifest, spine, navigation, Apple Books display options, cover declarations, and page dimensions.
- A queue performance test with at least 10,000 jobs.
- Installer, update, rollback, and uninstall tests on Windows 10 version 22H2 and current Windows 11.

### Version 1 acceptance criteria

- A valid CBZ can be added by drag and drop and converted without blocking the interface.
- A folder containing CBZ files can be scanned recursively.
- A 10,000-item queue remains responsive and persists across restart.
- High-confidence ComicVine matches continue automatically.
- Uncertain matches appear in Metadata Review without blocking unrelated work.
- A supported comic produces a structurally valid fixed-layout EPUB that opens in Apple Books with the correct page order, cover, aspect ratio, metadata, and reading direction.
- Originals remain byte-for-byte unchanged.
- Interrupted work resumes without duplicate completed output.
- Failed jobs can be understood and retried from the interface.
- Installation, update, failed-update recovery, and uninstall behave as specified.

## 13. PowerShell Developer Experience

The repository provides PowerShell scripts for:

- Installing or checking prerequisites.
- Restoring packages.
- Building Debug and Release configurations.
- Running unit, integration, and compatibility tests.
- Publishing a self-contained Windows x64 build.
- Producing the installer and update packages.
- Creating a GitHub Release payload.

Scripts fail with nonzero exit codes and clear messages. They do not require Bash, WSL, winget, or manual Visual Studio interaction.
