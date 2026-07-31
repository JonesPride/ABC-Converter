# SQLite Queue and Restart Recovery Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Persist the ABC Converter queue and metadata decisions in SQLite so unfinished work survives application shutdown, crashes, and Windows restarts without duplicating completed output.

**Architecture:** Core defines provider-neutral queue records, states, recovery rules, and storage interfaces. Infrastructure implements a versioned SQLite database and low-cost source identity fingerprinting. The WPF ViewModel writes every meaningful transition through the queue store and restores the queue before the main window is shown.

**Tech Stack:** C# 12, .NET 8, WPF, `Microsoft.Data.Sqlite` 8.0.20 with the non-vulnerable `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 security floor, xUnit, PowerShell, Windows GitHub Actions.

## Global Constraints

- Support Windows 10 version 22H2 and Windows 11 on x64.
- `ABC.Converter.Core` must not depend on WPF, SQLite, HTTP, archive implementations, or EPUB packaging.
- Original CBZ files remain untouched.
- The queue must remain usable with at least 10,000 records.
- SQLite stores queue jobs, state transitions, approved metadata, pending review data, output records, errors, retry data, and the output-root setting.
- Every durable job transition is committed before the next conversion stage begins.
- Jobs left in `Scanning`, `FetchingMetadata`, `Extracting`, `BuildingEpub`, `Validating`, or `Publishing` are reset to `Queued` on startup.
- `Completed` jobs remain completed only while their recorded EPUB exists; a missing recorded EPUB becomes retryable `Failed`.
- `NeedsReview`, `Cancelled`, and `Failed` are restored without silently requeueing them.
- A duplicate is the same case-insensitive canonical source path plus the same `v1:{length}:{lastWriteTimeUtcTicks}` fingerprint. A changed file at the same path is a new job.
- ComicVine API keys are never written to SQLite, configuration files, queue records, logs, or exceptions.
- Database initialization failures block startup with a recovery message instead of opening an in-memory queue.
- This plan does not add concurrent workers, pause, structured durable logs, ComicVine response caching, installer changes, or update delivery.

---

### Task 1: Durable Queue Contracts and Recovery Policy

**Files:**
- Create: `src/ABC.Converter.Core/Queue/QueueJobState.cs`
- Create: `src/ABC.Converter.Core/Queue/QueueMetadataCandidate.cs`
- Create: `src/ABC.Converter.Core/Queue/QueueMetadataReview.cs`
- Create: `src/ABC.Converter.Core/Queue/QueueJobRecord.cs`
- Create: `src/ABC.Converter.Core/Queue/IQueueStore.cs`
- Create: `src/ABC.Converter.Core/Queue/QueueRecoveryPolicy.cs`
- Test: `tests/ABC.Converter.Tests/Queue/QueueRecoveryPolicyTests.cs`

**Interfaces:**
- Consumes: `ABC.Converter.Core.Models.ComicMetadata`.
- Produces: the exact types below for Tasks 2–4.

```csharp
namespace ABC.Converter.Core.Queue;

public enum QueueJobState
{
    Queued,
    Scanning,
    FetchingMetadata,
    NeedsReview,
    Extracting,
    BuildingEpub,
    Validating,
    Publishing,
    Completed,
    Cancelled,
    Failed,
}

public sealed record QueueMetadataCandidate(
    string Provider,
    long ExternalId,
    string Name,
    string? IssueNumber,
    string Series,
    int? StartYear);

public sealed record QueueMetadataReview(
    ComicMetadata OfflineMetadata,
    IReadOnlyList<QueueMetadataCandidate> Candidates);

public sealed record QueueJobRecord(
    Guid Id,
    string SourcePath,
    string SourceFingerprint,
    string OutputRoot,
    ComicMetadata RequestedMetadata,
    QueueJobState State,
    int Progress,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    ComicMetadata? ApprovedMetadata = null,
    QueueMetadataReview? MetadataReview = null,
    string? OutputPath = null,
    string? ErrorStage = null,
    string? UserError = null,
    string? TechnicalError = null,
    bool Retryable = false,
    int RetryCount = 0);

public interface IQueueStore : IAsyncDisposable
{
    Task InitializeAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<QueueJobRecord>> LoadJobsAsync(CancellationToken cancellationToken);
    Task<string?> LoadSettingAsync(string key, CancellationToken cancellationToken);
    Task SaveSettingAsync(string key, string value, CancellationToken cancellationToken);
    Task<IReadOnlyList<Guid>> AddJobsAsync(
        IReadOnlyList<QueueJobRecord> jobs,
        CancellationToken cancellationToken);
    Task SaveJobAsync(QueueJobRecord job, CancellationToken cancellationToken);
}
```

- [ ] **Step 1: Write failing recovery-policy tests**

Add focused tests proving: every active state resets to `Queued` with progress zero and no transient error/output; `NeedsReview` and its candidates survive unchanged; completed jobs with an existing EPUB remain completed; completed jobs with a missing EPUB become retryable failed jobs with stage `Recovery`; failed/cancelled/queued jobs remain in their state; timestamps use an injected UTC time.

```csharp
[Theory]
[InlineData(QueueJobState.Scanning)]
[InlineData(QueueJobState.FetchingMetadata)]
[InlineData(QueueJobState.Extracting)]
[InlineData(QueueJobState.BuildingEpub)]
[InlineData(QueueJobState.Validating)]
[InlineData(QueueJobState.Publishing)]
public void ActiveJobsReturnToQueued(QueueJobState state)
{
    QueueJobRecord recovered = QueueRecoveryPolicy.Recover(
        CreateJob(state) with { Progress = 75, OutputPath = "partial.tmp" },
        _ => false,
        new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero));

    Assert.Equal(QueueJobState.Queued, recovered.State);
    Assert.Equal(0, recovered.Progress);
    Assert.Null(recovered.OutputPath);
    Assert.Null(recovered.UserError);
}
```

- [ ] **Step 2: Run Task 1 RED on Windows**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: build fails because `ABC.Converter.Core.Queue` types do not exist.

- [ ] **Step 3: Implement the exact queue types and recovery policy**

`QueueRecoveryPolicy.Recover` must be pure and use this signature:

```csharp
public static QueueJobRecord Recover(
    QueueJobRecord job,
    Func<string, bool> outputExists,
    DateTimeOffset utcNow)
```

Validate null arguments. Active states return a copy with `State = Queued`, `Progress = 0`, transient output/error fields cleared, `Retryable = false`, and `UpdatedUtc = utcNow`. Missing completed output returns `Failed`, clears `OutputPath`, sets `ErrorStage = "Recovery"`, `UserError = "The completed EPUB could not be found."`, `TechnicalError` to the missing path, and `Retryable = true`. All other states return the original record unchanged.

- [ ] **Step 4: Verify Task 1 GREEN**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: full suite passes with no warnings.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src/ABC.Converter.Core/Queue tests/ABC.Converter.Tests/Queue
git commit -m "add durable queue recovery contracts"
```

---

### Task 2: Versioned SQLite Queue Store and Source Fingerprints

**Files:**
- Modify: `src/ABC.Converter.Infrastructure/ABC.Converter.Infrastructure.csproj`
- Create: `src/ABC.Converter.Infrastructure/Persistence/ISourceFingerprintProvider.cs`
- Create: `src/ABC.Converter.Infrastructure/Persistence/SourceFingerprintProvider.cs`
- Create: `src/ABC.Converter.Infrastructure/Persistence/SqliteQueueStore.cs`
- Test: `tests/ABC.Converter.Tests/Persistence/SourceFingerprintProviderTests.cs`
- Test: `tests/ABC.Converter.Tests/Persistence/SqliteQueueStoreTests.cs`

**Interfaces:**
- Consumes: Task 1 `IQueueStore` and queue records.
- Produces:

```csharp
public interface ISourceFingerprintProvider
{
    Task<string> GetFingerprintAsync(string sourcePath, CancellationToken cancellationToken);
}

public sealed class SqliteQueueStore(string databasePath) : IQueueStore
```

- [ ] **Step 1: Add failing SQLite and fingerprint tests**

Tests must use a unique temporary on-disk database, never a mocked connection. Prove initialization is idempotent; schema version 1 is set; all job fields round-trip; review candidates round-trip; `AddJobsAsync` is atomic; duplicate path/fingerprint pairs are ignored case-insensitively; the same path with a changed fingerprint is accepted; save updates one job; settings round-trip; 10,000 jobs insert and load in deterministic `CreatedUtc, Id` order; a database with a future `PRAGMA user_version` is rejected without modification; fingerprints are canonical `v1:length:lastWriteTicks` and change when length or UTC write time changes.

```csharp
[Fact]
public async Task RoundTripsACompleteQueueJob()
{
    await using var database = new TemporaryQueueDatabase();
    await database.Store.InitializeAsync(CancellationToken.None);
    QueueJobRecord expected = QueueJobFactory.CompleteRecord();

    IReadOnlyList<Guid> inserted = await database.Store.AddJobsAsync(
        [expected],
        CancellationToken.None);
    QueueJobRecord actual = Assert.Single(
        await database.Store.LoadJobsAsync(CancellationToken.None));

    Assert.Equal([expected.Id], inserted);
    Assert.Equivalent(expected, actual, strict: true);
}
```

- [ ] **Step 2: Run Task 2 RED on Windows**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: build fails because the persistence types do not exist.

- [ ] **Step 3: Add the SQLite dependency**

Add to `ABC.Converter.Infrastructure.csproj`:

```xml
<PackageReference Include="Microsoft.Data.Sqlite" Version="8.0.20" />
<PackageReference Include="SQLitePCLRaw.bundle_e_sqlite3" Version="2.1.12" />
```

- [ ] **Step 4: Implement schema version 1 in one migration transaction**

Use one connection per operation with `Mode=ReadWriteCreate;Cache=Shared`, `PRAGMA busy_timeout=5000`, foreign keys on, and WAL journal mode. Store enum values as stable text. Store metadata fields in columns and review candidates as JSON generated with `System.Text.Json`. Use parameterized SQL only.

```sql
CREATE TABLE IF NOT EXISTS queue_jobs (
    id TEXT PRIMARY KEY NOT NULL,
    source_path TEXT NOT NULL COLLATE NOCASE,
    source_fingerprint TEXT NOT NULL,
    output_root TEXT NOT NULL,
    requested_title TEXT NOT NULL,
    requested_series TEXT NOT NULL,
    requested_issue TEXT NULL,
    requested_volume TEXT NULL,
    requested_year INTEGER NULL,
    requested_language TEXT NOT NULL,
    state TEXT NOT NULL,
    progress INTEGER NOT NULL CHECK (progress BETWEEN 0 AND 100),
    created_utc TEXT NOT NULL,
    updated_utc TEXT NOT NULL,
    approved_metadata_json TEXT NULL,
    metadata_review_json TEXT NULL,
    output_path TEXT NULL,
    error_stage TEXT NULL,
    user_error TEXT NULL,
    technical_error TEXT NULL,
    retryable INTEGER NOT NULL CHECK (retryable IN (0, 1)),
    retry_count INTEGER NOT NULL CHECK (retry_count >= 0),
    UNIQUE(source_path, source_fingerprint)
);
CREATE INDEX IF NOT EXISTS ix_queue_jobs_state ON queue_jobs(state);
CREATE INDEX IF NOT EXISTS ix_queue_jobs_created ON queue_jobs(created_utc, id);
CREATE TABLE IF NOT EXISTS app_settings (
    key TEXT PRIMARY KEY NOT NULL,
    value TEXT NOT NULL
);
PRAGMA user_version = 1;
```

`AddJobsAsync` and migrations use explicit transactions. `AddJobsAsync` returns the IDs actually inserted, in input order, and omits duplicate path/fingerprint records. `SaveJobAsync` updates by ID and throws `InvalidOperationException` when no record exists. Loading rejects unknown state text, invalid JSON, or invalid required columns with a database-specific `InvalidDataException`; it must not silently discard a row.

- [ ] **Step 5: Implement low-cost source fingerprints**

Canonicalize with `Path.GetFullPath`, read length and `LastWriteTimeUtc.Ticks` from one refreshed `FileInfo`, honor cancellation before and after filesystem access, and return exactly `v1:{length}:{ticks}` using invariant formatting.

- [ ] **Step 6: Verify Task 2 GREEN**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: full suite passes; the 10,000-record test completes within 10 seconds on `windows-latest`.

- [ ] **Step 7: Commit Task 2**

```powershell
git add src/ABC.Converter.Infrastructure tests/ABC.Converter.Tests/Persistence
git commit -m "persist queue records in SQLite"
```

---

### Task 3: WPF Queue Persistence and Startup Recovery

**Files:**
- Modify: `src/ABC.Converter.App/ViewModels/ConversionJobViewModel.cs`
- Modify: `src/ABC.Converter.App/ViewModels/MainWindowViewModel.cs`
- Modify: `src/ABC.Converter.App/App.xaml.cs`
- Modify: `src/ABC.Converter.App/MainWindow.xaml.cs`
- Create: `src/ABC.Converter.App/Services/ApplicationDataPaths.cs`
- Create: `src/ABC.Converter.App/Services/SafeExceptionDetails.cs`
- Test: `tests/ABC.Converter.App.Tests/ViewModels/QueuePersistenceViewModelTests.cs`
- Test: `tests/ABC.Converter.App.Tests/TestSupport/InMemoryQueueStore.cs`

**Interfaces:**
- Consumes: Task 1 queue contracts and Task 2 store/fingerprint provider.
- Produces:

```csharp
public Task InitializeAsync(CancellationToken cancellationToken);
public Task PrepareForShutdownAsync(TimeSpan timeout);
```

- [ ] **Step 1: Write failing ViewModel persistence tests**

Use an in-memory fake implementing the real `IQueueStore`, not mocks. Prove: initialization restores deterministic order; recovery changes are saved before initialization completes; adding a batch computes identities and persists before UI insertion; duplicate path/fingerprint jobs are suppressed across restarts; changed source fingerprint creates a new job; approved metadata, pending review candidates, output root, status, progress, output, and errors survive a new ViewModel instance; every conversion state write happens before the next stage; retry increments `RetryCount` and preserves approved metadata; queued cancellation persists; graceful shutdown cancels the active job and leaves it resumable.

```csharp
[Fact]
public async Task NewViewModelRestoresApprovedMetadataAndQueuedState()
{
    var store = new InMemoryQueueStore();
    MainWindowViewModel first = CreateViewModel(store);
    await first.InitializeAsync(CancellationToken.None);
    await first.AddPathsAsync([comicPath]);
    first.SelectedJob = Assert.Single(first.Jobs);
    first.SelectedJob.ApprovedMetadata = approved;
    await first.PersistSelectedJobForTestAsync();

    MainWindowViewModel reopened = CreateViewModel(store);
    await reopened.InitializeAsync(CancellationToken.None);

    ConversionJobViewModel restored = Assert.Single(reopened.Jobs);
    Assert.Equal(approved, restored.ApprovedMetadata);
    Assert.Equal("Queued", restored.Status);
}
```

Do not add a production `PersistSelectedJobForTestAsync`; drive persistence through the public approve command in the real test. The illustrative name above is explicitly forbidden in production.

- [ ] **Step 2: Run Task 3 RED on Windows**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: build fails because the ViewModel constructor and initialization contract do not exist.

- [ ] **Step 3: Add durable identity to `ConversionJobViewModel`**

Add immutable-at-runtime `Id`, `SourceFingerprint`, `CreatedUtc`, `UpdatedUtc`, `RetryCount`, `ErrorStage`, `TechnicalError`, and `Retryable`. Add internal mapping methods `FromRecord` and `ToRecord(outputRoot, utcNow)`; keep the existing display properties and status strings for XAML compatibility. Map every status through exhaustive functions to `QueueJobState` and reject unknown values.

- [ ] **Step 4: Integrate `IQueueStore` and fingerprints into `MainWindowViewModel`**

Extend the constructor with optional `IQueueStore? queueStore`, `ISourceFingerprintProvider? fingerprintProvider`, and `Func<DateTimeOffset>? utcNow`. When no store is supplied, use an internal no-op store only for existing isolated tests; production composition must always supply SQLite.

`InitializeAsync` initializes the database, loads `OutputRoot`, loads jobs, applies `QueueRecoveryPolicy`, saves changed recovery records, rebuilds `_knownSourcePaths` as path/fingerprint identities, subscribes handlers, and raises queue state once. It must be safe to call only once and throw on a second call.

`AddPathsAsync` discovers files off the UI thread, computes fingerprints with bounded CPU-neutral sequential I/O, builds records, calls one `AddJobsAsync`, then inserts only records whose IDs were returned by the store.

All paths that set scanning, needs-review, completed, cancelled, failed, approved, or retry state must await `SaveJobAsync` after the in-memory mutation and before processing continues. Failure records use the current state as `ErrorStage`, user-facing `exception.Message`, and `SafeExceptionDetails.Format(exception)` as technical detail, with `Retryable = true` except cancellation. The safe formatter stores only the top-level exception type, message, and stack trace; it never traverses inner exceptions, `Data`, request objects, or URLs, and it redacts case-insensitive `api_key=` query values before persistence.

Retry clears transient errors/output/review, increments `RetryCount`, preserves `ApprovedMetadata`, and saves `Queued`. It must never clear a user-approved decision.

- [ ] **Step 5: Compose the production database before showing the window**

`ApplicationDataPaths.QueueDatabasePath` returns `%LOCALAPPDATA%\ABC Converter\abc-converter.db` and creates only the parent directory. `App.OnStartup` may be `async void` because it is the WPF override, but it must wrap initialization in `try/catch`; on failure show one recovery message, dispose constructed resources, call `Shutdown(1)`, and never show the main window.

- [ ] **Step 6: Add bounded graceful shutdown**

Track the current processing task. `PrepareForShutdownAsync(TimeSpan.FromSeconds(5))` stops new starts, cancels the active linked token, awaits processing until the timeout, and ensures the last state has been saved. `MainWindow.Closing` cancels the first close request, awaits this method, then closes once using a recursion guard.

- [ ] **Step 7: Verify Task 3 GREEN**

Run: `pwsh -File .\scripts\Test.ps1 -Configuration Release`

Expected: full suite passes with no warnings.

- [ ] **Step 8: Commit Task 3**

```powershell
git add src/ABC.Converter.App tests/ABC.Converter.App.Tests
git commit -m "restore the persistent queue at startup"
```

---

### Task 4: Restart Acceptance, Performance Qualification, and Documentation

**Files:**
- Create: `tests/ABC.Converter.Tests/Acceptance/QueueRecoveryAcceptanceTests.cs`
- Modify: `README.md`
- Modify: `docs/phase-2.md`

**Interfaces:**
- Consumes: completed queue store, recovery policy, and WPF integration.
- Produces: merge evidence for the persistence/recovery milestone.

- [ ] **Step 1: Write end-to-end restart tests against a real SQLite file**

Prove these flows without mocks: queued batch survives store disposal/reopen; an interrupted active job reopens queued and converts exactly once; approved metadata survives and prevents ComicVine re-resolution; completed output is not converted again; missing completed output becomes retryable failed; failed error fields and retry count survive; pending review candidates survive; changed CBZ fingerprint at the same path can be added; unchanged duplicate cannot; original CBZ SHA-256 is unchanged through recovery and conversion.

- [ ] **Step 2: Run the new acceptance tests before any acceptance-specific production change**

Run: `dotnet test .\tests\ABC.Converter.Tests\ABC.Converter.Tests.csproj -c Release --filter FullyQualifiedName~QueueRecoveryAcceptanceTests`

Expected: PASS if Tasks 1–3 fully satisfy the contract. If a test fails because behavior is missing, preserve that failing run as RED evidence and make the smallest production correction before rerunning.

- [ ] **Step 3: Run the 10,000-job performance gate**

Run: `dotnet test .\tests\ABC.Converter.Tests\ABC.Converter.Tests.csproj -c Release --filter FullyQualifiedName~LoadsTenThousandJobs`

Expected: PASS within the 10-second test timeout on `windows-latest`, with all 10,000 records returned in deterministic order.

- [ ] **Step 4: Update documentation without overstating scope**

README must state the exact database location, restored states, duplicate identity rule, missing-output behavior, graceful-shutdown timeout, and PowerShell commands. Remove the statement that closing the app discards the queue. `docs/phase-2.md` marks milestone 4 complete and names concurrent workers, pause, durable logs, and ComicVine SQLite caching as remaining work.

- [ ] **Step 5: Run final Release qualification**

```powershell
pwsh -File .\scripts\Setup.ps1
pwsh -File .\scripts\Build.ps1 -Configuration Release
pwsh -File .\scripts\Test.ps1 -Configuration Release
```

Expected: restore, vulnerability audit, Release build, and every test pass with no warnings.

- [ ] **Step 6: Commit Task 4**

```powershell
git add tests/ABC.Converter.Tests/Acceptance README.md docs/phase-2.md .github/workflows/windows-ci.yml
git commit -m "qualify queue restart recovery"
```

---

## Completion Criteria

- A clean launch creates or migrates schema version 1 in the per-user application-data directory.
- A queue containing 10,000 comics persists and restores in deterministic order.
- Every meaningful state change, approved metadata decision, progress value, output record, retry count, and error record survives restart.
- Active work interrupted by shutdown or a crash restarts as queued and does not overwrite or duplicate completed output.
- Missing completed output is visible and retryable rather than silently treated as complete.
- Duplicate suppression survives restart and accepts a changed file at the same canonical path.
- Startup refuses to continue if the database is unavailable, corrupt, or newer than the supported schema.
- Windows Release CI passes dependency audit, build, unit tests, integration tests, recovery tests, and the 10,000-job gate.
