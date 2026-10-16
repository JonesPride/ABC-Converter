using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using ABC.Converter.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace ABC.Converter.Tests.Persistence;

internal sealed class TemporaryQueueDatabase : IAsyncDisposable
{
    private readonly string directoryPath;

    public TemporaryQueueDatabase()
    {
        directoryPath = Path.Combine(
            Path.GetTempPath(),
            $"abc-converter-queue-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directoryPath);
        DatabasePath = Path.Combine(directoryPath, "queue.db");
        Store = new SqliteQueueStore(DatabasePath);
    }

    public string DatabasePath { get; }

    public IQueueStore Store { get; }

    public async Task ExecuteNonQueryAsync(string commandText)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    public async Task<T> ExecuteScalarAsync<T>(string commandText)
    {
        await using SqliteConnection connection = CreateConnection();
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = commandText;
        object? result = await command.ExecuteScalarAsync();
        return Assert.IsType<T>(result);
    }

    public async ValueTask DisposeAsync()
    {
        await Store.DisposeAsync();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteConnection(builder.ConnectionString);
    }
}

internal static class QueueJobFactory
{
    private static readonly DateTimeOffset CreatedUtc =
        new(2026, 7, 31, 10, 15, 30, TimeSpan.Zero);

    public static QueueJobRecord CompleteRecord(Guid? id = null) => new(
        id ?? Guid.Parse("7292c920-cf2f-47f3-aa26-fc6d99b4185b"),
        @"C:\Comics\Detective Comics 001.cbz",
        "v1:8192:638895888000000000",
        @"D:\Epub Library",
        new ComicMetadata(
            "The Case of the Chemical Syndicate",
            "Detective Comics",
            "1",
            "1",
            1937,
            "en-US"),
        QueueJobState.Failed,
        73,
        CreatedUtc,
        CreatedUtc.AddMinutes(9),
        new ComicMetadata(
            "The Case of the Chemical Syndicate (Approved)",
            "Detective Comics",
            "001",
            "1",
            1937,
            "en"),
        new QueueMetadataReview(
            new ComicMetadata(
                "Detective Comics 001",
                "Detective Comics",
                "1",
                PublicationYear: 1937,
                Language: "en"),
            [
                new QueueMetadataCandidate(
                    "Comic Vine",
                    145612,
                    "The Case of the Chemical Syndicate",
                    "1",
                    "Detective Comics",
                    1937),
                new QueueMetadataCandidate(
                    "Secondary Provider",
                    90210,
                    "Detective Comics",
                    null,
                    "Detective Comics",
                    null),
            ]),
        @"D:\Epub Library\Detective Comics\Volume 1\Detective Comics - 001.epub",
        "Validating",
        "The generated EPUB did not pass validation.",
        "nav.xhtml is missing the landmarks navigation entry.",
        true,
        3);

    public static QueueJobRecord BasicRecord(
        Guid id,
        string sourcePath,
        string fingerprint,
        DateTimeOffset? createdUtc = null) => new(
        id,
        sourcePath,
        fingerprint,
        @"D:\Epub Library",
        new ComicMetadata("Issue", "Series"),
        QueueJobState.Queued,
        0,
        createdUtc ?? CreatedUtc,
        createdUtc ?? CreatedUtc);
}
