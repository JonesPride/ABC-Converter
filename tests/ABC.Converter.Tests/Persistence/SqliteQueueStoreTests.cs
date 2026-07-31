using ABC.Converter.Core.Queue;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ABC.Converter.Tests.Persistence;

public sealed class SqliteQueueStoreTests
{
    [Fact]
    public async Task InitializationIsIdempotent()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord expected = QueueJobFactory.CompleteRecord();
        await database.Store.AddJobsAsync([expected], CancellationToken.None);

        await database.Store.InitializeAsync(CancellationToken.None);

        QueueJobRecord actual = Assert.Single(
            await database.Store.LoadJobsAsync(CancellationToken.None));
        Assert.Equivalent(expected, actual, strict: true);
    }

    [Fact]
    public async Task InitializationSetsSchemaVersionOne()
    {
        await using var database = new TemporaryQueueDatabase();

        await database.Store.InitializeAsync(CancellationToken.None);

        long version = await database.ExecuteScalarAsync<long>("PRAGMA user_version;");
        Assert.Equal(1L, version);
    }

    [Fact]
    public async Task InitializationEnablesWriteAheadLogging()
    {
        await using var database = new TemporaryQueueDatabase();

        await database.Store.InitializeAsync(CancellationToken.None);

        string journalMode = await database.ExecuteScalarAsync<string>("PRAGMA journal_mode;");
        Assert.Equal("wal", journalMode, ignoreCase: true);
    }

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

    [Fact]
    public async Task RoundTripsEveryMetadataReviewCandidateField()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord expected = QueueJobFactory.CompleteRecord() with
        {
            State = QueueJobState.NeedsReview,
            ApprovedMetadata = null,
            OutputPath = null,
            ErrorStage = null,
            UserError = null,
            TechnicalError = null,
            Retryable = false,
            RetryCount = 0,
        };

        await database.Store.AddJobsAsync([expected], CancellationToken.None);
        QueueJobRecord actual = Assert.Single(
            await database.Store.LoadJobsAsync(CancellationToken.None));

        Assert.Equivalent(expected.MetadataReview, actual.MetadataReview, strict: true);
    }

    [Fact]
    public async Task StoresQueueStateAsStableText()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord expected = QueueJobFactory.CompleteRecord() with
        {
            State = QueueJobState.FetchingMetadata,
        };

        await database.Store.AddJobsAsync([expected], CancellationToken.None);

        Assert.Equal(
            "FetchingMetadata",
            await database.ExecuteScalarAsync<string>("SELECT state FROM queue_jobs;"));
    }

    [Fact]
    public async Task AddJobsIsAtomicWhenARecordConflictsById()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        Guid sharedId = Guid.Parse("2bce987b-bc5b-423c-b6da-19542241a267");
        QueueJobRecord first = QueueJobFactory.BasicRecord(
            sharedId,
            @"C:\Comics\first.cbz",
            "v1:1:100");
        QueueJobRecord conflicting = QueueJobFactory.BasicRecord(
            sharedId,
            @"C:\Comics\second.cbz",
            "v1:2:200");

        await Assert.ThrowsAsync<SqliteException>(
            () => database.Store.AddJobsAsync([first, conflicting], CancellationToken.None));

        Assert.Empty(await database.Store.LoadJobsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task IgnoresDuplicatePathFingerprintPairsCaseInsensitively()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord first = QueueJobFactory.BasicRecord(
            Guid.Parse("38d8b887-e5a8-4878-b79b-90938e67b287"),
            @"C:\Comics\Batman 001.CBZ",
            "v1:100:200");
        QueueJobRecord duplicate = QueueJobFactory.BasicRecord(
            Guid.Parse("aee0b910-7380-4f52-8c56-776865961ac9"),
            @"c:\comics\batman 001.cbz",
            "v1:100:200");
        QueueJobRecord distinct = QueueJobFactory.BasicRecord(
            Guid.Parse("270452f2-dbd2-456e-a634-d78946c634b2"),
            @"C:\Comics\Batman 002.cbz",
            "v1:200:300");

        IReadOnlyList<Guid> inserted = await database.Store.AddJobsAsync(
            [first, duplicate, distinct],
            CancellationToken.None);

        Assert.Equal([first.Id, distinct.Id], inserted);
        IReadOnlyList<QueueJobRecord> loaded =
            await database.Store.LoadJobsAsync(CancellationToken.None);
        Assert.Equal(2, loaded.Count);
        Assert.Contains(loaded, job => job.Id == first.Id);
        Assert.Contains(loaded, job => job.Id == distinct.Id);
    }

    [Fact]
    public async Task IgnoresUnicodePathDuplicatesUsingOrdinalIgnoreCase()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord first = QueueJobFactory.BasicRecord(
            Guid.Parse("15e933ee-6595-4620-9517-ab902f311b80"),
            @"C:\Comics\Édition.cbz",
            "v1:300:400");
        QueueJobRecord duplicate = QueueJobFactory.BasicRecord(
            Guid.Parse("cc7a17e6-d14c-463c-af09-b6e6a590086e"),
            @"c:\comics\édition.cbz",
            "v1:300:400");
        Assert.Equal(
            0,
            StringComparer.OrdinalIgnoreCase.Compare(first.SourcePath, duplicate.SourcePath));

        IReadOnlyList<Guid> inserted = await database.Store.AddJobsAsync(
            [first, duplicate],
            CancellationToken.None);

        Assert.Equal([first.Id], inserted);
        QueueJobRecord loaded = Assert.Single(
            await database.Store.LoadJobsAsync(CancellationToken.None));
        Assert.Equal(first.Id, loaded.Id);
    }

    [Fact]
    public async Task AcceptsTheSamePathWhenItsFingerprintChanges()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord original = QueueJobFactory.BasicRecord(
            Guid.Parse("822977e0-1f09-4929-896e-cb47d78cc8d9"),
            @"C:\Comics\Batman 001.cbz",
            "v1:100:200");
        QueueJobRecord changed = QueueJobFactory.BasicRecord(
            Guid.Parse("15a74cb7-c05f-42bb-ae09-f62c3fc65f80"),
            @"c:\comics\batman 001.CBZ",
            "v1:101:300");

        IReadOnlyList<Guid> inserted = await database.Store.AddJobsAsync(
            [original, changed],
            CancellationToken.None);

        Assert.Equal([original.Id, changed.Id], inserted);
        Assert.Equal(2, (await database.Store.LoadJobsAsync(CancellationToken.None)).Count);
    }

    [Fact]
    public async Task SaveJobUpdatesOnlyTheMatchingJob()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord original = QueueJobFactory.BasicRecord(
            Guid.Parse("703878bf-b78e-4728-8860-b9c9589bfe74"),
            @"C:\Comics\original.cbz",
            "v1:100:200");
        QueueJobRecord untouched = QueueJobFactory.BasicRecord(
            Guid.Parse("3ebaa380-eaf3-464c-b6a7-ad8412b2a36d"),
            @"C:\Comics\untouched.cbz",
            "v1:200:300");
        QueueJobRecord updated = QueueJobFactory.CompleteRecord(original.Id);
        await database.Store.AddJobsAsync([original, untouched], CancellationToken.None);

        await database.Store.SaveJobAsync(updated, CancellationToken.None);

        IReadOnlyList<QueueJobRecord> loaded =
            await database.Store.LoadJobsAsync(CancellationToken.None);
        Assert.Equivalent(updated, Assert.Single(loaded, job => job.Id == original.Id), strict: true);
        Assert.Equivalent(untouched, Assert.Single(loaded, job => job.Id == untouched.Id), strict: true);
    }

    [Fact]
    public async Task SaveJobThrowsWhenTheJobDoesNotExist()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.SaveJobAsync(
                QueueJobFactory.CompleteRecord(),
                CancellationToken.None));
    }

    [Fact]
    public async Task SettingsRoundTripAndCanBeUpdated()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);

        Assert.Null(await database.Store.LoadSettingAsync("output-root", CancellationToken.None));
        await database.Store.SaveSettingAsync(
            "output-root",
            @"D:\Bibliothèque\初版",
            CancellationToken.None);
        Assert.Equal(
            @"D:\Bibliothèque\初版",
            await database.Store.LoadSettingAsync("output-root", CancellationToken.None));

        await database.Store.SaveSettingAsync(
            "output-root",
            @"E:\Updated",
            CancellationToken.None);
        Assert.Equal(
            @"E:\Updated",
            await database.Store.LoadSettingAsync("output-root", CancellationToken.None));
    }

    [Fact]
    public async Task LoadsMixedOffsetCreatedUtcValuesInChronologicalOrder()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        QueueJobRecord earlier = QueueJobFactory.BasicRecord(
            Guid.Parse("00000000-0000-0000-0000-000000000101"),
            @"C:\Comics\earlier.cbz",
            "v1:101:101",
            new DateTimeOffset(2026, 7, 31, 10, 0, 0, TimeSpan.FromHours(2)));
        QueueJobRecord later = QueueJobFactory.BasicRecord(
            Guid.Parse("00000000-0000-0000-0000-000000000102"),
            @"C:\Comics\later.cbz",
            "v1:102:102",
            new DateTimeOffset(2026, 7, 31, 9, 0, 0, TimeSpan.Zero));
        Assert.True(earlier.CreatedUtc < later.CreatedUtc);
        await database.Store.AddJobsAsync([later, earlier], CancellationToken.None);

        IReadOnlyList<QueueJobRecord> loaded =
            await database.Store.LoadJobsAsync(CancellationToken.None);

        Assert.Equal([earlier.Id, later.Id], loaded.Select(job => job.Id));
    }

    [Fact(Timeout = 10_000)]
    public async Task InsertsAndLoadsTenThousandJobsInCreatedUtcThenIdOrder()
    {
        const int jobCount = 10_000;
        await using var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        DateTimeOffset baseline = new(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);
        QueueJobRecord[] jobs = Enumerable.Range(1, jobCount)
            .Select(index => QueueJobFactory.BasicRecord(
                DeterministicGuid(index),
                $@"C:\Comics\issue-{index:D5}.cbz",
                $"v1:{index}:{index * 10L}",
                baseline.AddSeconds(index % 17)))
            .Reverse()
            .ToArray();

        IReadOnlyList<Guid> inserted = await database.Store.AddJobsAsync(
            jobs,
            CancellationToken.None);
        IReadOnlyList<QueueJobRecord> loaded =
            await database.Store.LoadJobsAsync(CancellationToken.None);
        Guid[] expectedOrder = jobs
            .OrderBy(job => job.CreatedUtc)
            .ThenBy(job => job.Id)
            .Select(job => job.Id)
            .ToArray();

        Assert.Equal(jobCount, inserted.Count);
        Assert.True(
            jobs.Select(job => job.Id).SequenceEqual(inserted),
            "Inserted IDs were not returned in input order.");
        Assert.Equal(jobCount, loaded.Count);
        Assert.True(
            expectedOrder.SequenceEqual(loaded.Select(job => job.Id)),
            "Jobs were not loaded in CreatedUtc, Id order.");
    }

    [Fact]
    public async Task RejectsAFutureSchemaWithoutModifyingIt()
    {
        await using var database = new TemporaryQueueDatabase();
        await database.ExecuteNonQueryAsync(
            "CREATE TABLE future_marker (value TEXT NOT NULL);");
        await database.ExecuteNonQueryAsync(
            "INSERT INTO future_marker(value) VALUES ('preserve-me');");
        await database.ExecuteNonQueryAsync("PRAGMA user_version = 2;");

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => database.Store.InitializeAsync(CancellationToken.None));

        Assert.Equal(2L, await database.ExecuteScalarAsync<long>("PRAGMA user_version;"));
        Assert.Equal(
            0L,
            await database.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM sqlite_master WHERE name = 'queue_jobs';"));
        Assert.Equal(
            "preserve-me",
            await database.ExecuteScalarAsync<string>("SELECT value FROM future_marker;"));
    }

    [Fact]
    public async Task LoadingRejectsUnknownStateText()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET state = 'FutureState';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsInvalidMetadataJson()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET metadata_review_json = '{not-json';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsCreatedUtcWithoutCanonicalOffset()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET created_utc = '2026-07-31T10:15:30';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsEmptyApprovedMetadataJsonObject()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET approved_metadata_json = '{}';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsEmptyMetadataReviewJsonObject()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET metadata_review_json = '{}';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsIncompleteNestedMetadataReviewGraph()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET metadata_review_json = " +
            "'{\"OfflineMetadata\":{},\"Candidates\":[{}]}';");

        await AssertInvalidDatabaseAsync(database);
    }

    [Fact]
    public async Task LoadingRejectsAnInvalidRequiredColumn()
    {
        await using var database = await CreateDatabaseWithOneJobAsync();
        await database.ExecuteNonQueryAsync(
            "UPDATE queue_jobs SET id = 'not-a-guid';");

        await AssertInvalidDatabaseAsync(database);
    }

    private static Guid DeterministicGuid(int value) =>
        Guid.Parse($"00000000-0000-0000-0000-{value:X12}");

    private static async Task<TemporaryQueueDatabase> CreateDatabaseWithOneJobAsync()
    {
        var database = new TemporaryQueueDatabase();
        await database.Store.InitializeAsync(CancellationToken.None);
        await database.Store.AddJobsAsync(
            [QueueJobFactory.CompleteRecord()],
            CancellationToken.None);
        return database;
    }

    private static async Task AssertInvalidDatabaseAsync(TemporaryQueueDatabase database)
    {
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => database.Store.LoadJobsAsync(CancellationToken.None));
        Assert.Contains(database.DatabasePath, error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
