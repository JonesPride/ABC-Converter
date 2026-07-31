using System.Globalization;
using System.Text.Json;
using ABC.Converter.Core.Models;
using ABC.Converter.Core.Queue;
using Microsoft.Data.Sqlite;

namespace ABC.Converter.Infrastructure.Persistence;

public sealed class SqliteQueueStore(string databasePath) : IQueueStore
{
    private const int CurrentSchemaVersion = 1;
    private const string SourcePathCollation = "ABC_ORDINAL_IGNORE_CASE";

    private const string SchemaVersionOneSql = """
        CREATE TABLE IF NOT EXISTS queue_jobs (
            id TEXT PRIMARY KEY NOT NULL,
            source_path TEXT NOT NULL COLLATE ABC_ORDINAL_IGNORE_CASE,
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
        """;

    private const string SelectJobsSql = """
        SELECT
            id,
            source_path,
            source_fingerprint,
            output_root,
            requested_title,
            requested_series,
            requested_issue,
            requested_volume,
            requested_year,
            requested_language,
            state,
            progress,
            created_utc,
            updated_utc,
            approved_metadata_json,
            metadata_review_json,
            output_path,
            error_stage,
            user_error,
            technical_error,
            retryable,
            retry_count
        FROM queue_jobs
        ORDER BY created_utc, id;
        """;

    private const string InsertJobSql = """
        INSERT INTO queue_jobs (
            id,
            source_path,
            source_fingerprint,
            output_root,
            requested_title,
            requested_series,
            requested_issue,
            requested_volume,
            requested_year,
            requested_language,
            state,
            progress,
            created_utc,
            updated_utc,
            approved_metadata_json,
            metadata_review_json,
            output_path,
            error_stage,
            user_error,
            technical_error,
            retryable,
            retry_count)
        VALUES (
            $id,
            $source_path,
            $source_fingerprint,
            $output_root,
            $requested_title,
            $requested_series,
            $requested_issue,
            $requested_volume,
            $requested_year,
            $requested_language,
            $state,
            $progress,
            $created_utc,
            $updated_utc,
            $approved_metadata_json,
            $metadata_review_json,
            $output_path,
            $error_stage,
            $user_error,
            $technical_error,
            $retryable,
            $retry_count)
        ON CONFLICT(source_path, source_fingerprint) DO NOTHING
        RETURNING id;
        """;

    private const string UpdateJobSql = """
        UPDATE queue_jobs
        SET
            source_path = $source_path,
            source_fingerprint = $source_fingerprint,
            output_root = $output_root,
            requested_title = $requested_title,
            requested_series = $requested_series,
            requested_issue = $requested_issue,
            requested_volume = $requested_volume,
            requested_year = $requested_year,
            requested_language = $requested_language,
            state = $state,
            progress = $progress,
            created_utc = $created_utc,
            updated_utc = $updated_utc,
            approved_metadata_json = $approved_metadata_json,
            metadata_review_json = $metadata_review_json,
            output_path = $output_path,
            error_stage = $error_stage,
            user_error = $user_error,
            technical_error = $technical_error,
            retryable = $retryable,
            retry_count = $retry_count
        WHERE id = $id;
        """;

    private static readonly JsonSerializerOptions JsonOptions = new();

    private readonly string normalizedDatabasePath = NormalizeDatabasePath(databasePath);

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        long version = await ReadSchemaVersionAsync(connection, cancellationToken)
            .ConfigureAwait(false);

        if (version > CurrentSchemaVersion)
        {
            throw new InvalidOperationException(
                $"Queue database '{normalizedDatabasePath}' uses unsupported future schema " +
                $"version {version}; this application supports version {CurrentSchemaVersion}.");
        }

        await EnableWalAsync(connection, cancellationToken).ConfigureAwait(false);
        if (version == CurrentSchemaVersion)
        {
            return;
        }

        using SqliteTransaction transaction = connection.BeginTransaction();
        try
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = SchemaVersionOneSql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task<IReadOnlyList<QueueJobRecord>> LoadJobsAsync(
        CancellationToken cancellationToken)
    {
        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = SelectJobsSql;
        await using SqliteDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        var jobs = new List<QueueJobRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            jobs.Add(ReadJob(reader));
        }

        return jobs;
    }

    public async Task<string?> LoadSettingAsync(
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM app_settings WHERE key = $key;";
        command.Parameters.Add("$key", SqliteType.Text).Value = key;
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        return result as string ?? throw InvalidColumn("app_settings.value");
    }

    public async Task SaveSettingAsync(
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO app_settings(key, value)
            VALUES ($key, $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value;
            """;
        command.Parameters.Add("$key", SqliteType.Text).Value = key;
        command.Parameters.Add("$value", SqliteType.Text).Value = value;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Guid>> AddJobsAsync(
        IReadOnlyList<QueueJobRecord> jobs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(jobs);
        if (jobs.Count == 0)
        {
            return [];
        }

        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteTransaction transaction = connection.BeginTransaction();
        using SqliteCommand command = CreateJobCommand(connection, transaction, InsertJobSql);
        var inserted = new List<Guid>(jobs.Count);
        try
        {
            foreach (QueueJobRecord job in jobs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                BindJob(command, job);
                object? result = await command
                    .ExecuteScalarAsync(cancellationToken)
                    .ConfigureAwait(false);
                if (result is not null)
                {
                    string insertedId = result as string ?? throw InvalidColumn("queue_jobs.id");
                    if (!Guid.TryParseExact(insertedId, "D", out Guid parsedId))
                    {
                        throw InvalidColumn("queue_jobs.id");
                    }

                    inserted.Add(parsedId);
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
            return inserted;
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public async Task SaveJobAsync(
        QueueJobRecord job,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await using SqliteConnection connection =
            await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        using SqliteCommand command = CreateJobCommand(connection, transaction: null, UpdateJobSql);
        BindJob(command, job);
        int changed = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        if (changed == 0)
        {
            throw new InvalidOperationException(
                $"Queue job '{job.Id:D}' does not exist in database '{normalizedDatabasePath}'.");
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static string NormalizeDatabasePath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.GetFullPath(path);
    }

    private SqliteConnection CreateConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = normalizedDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
        };
        return new SqliteConnection(builder.ConnectionString);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SqliteConnection connection = CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            connection.CreateCollation(
                SourcePathCollation,
                static (left, right) => StringComparer.OrdinalIgnoreCase.Compare(left, right));
            using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout = 5000;
                PRAGMA foreign_keys = ON;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<long> ReadSchemaVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long version
            ? version
            : throw new InvalidDataException("The SQLite user_version value is invalid.");
    }

    private static async Task EnableWalAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
    }

    private static SqliteCommand CreateJobCommand(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string commandText)
    {
        SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        command.Parameters.Add("$id", SqliteType.Text);
        command.Parameters.Add("$source_path", SqliteType.Text);
        command.Parameters.Add("$source_fingerprint", SqliteType.Text);
        command.Parameters.Add("$output_root", SqliteType.Text);
        command.Parameters.Add("$requested_title", SqliteType.Text);
        command.Parameters.Add("$requested_series", SqliteType.Text);
        command.Parameters.Add("$requested_issue", SqliteType.Text);
        command.Parameters.Add("$requested_volume", SqliteType.Text);
        command.Parameters.Add("$requested_year", SqliteType.Integer);
        command.Parameters.Add("$requested_language", SqliteType.Text);
        command.Parameters.Add("$state", SqliteType.Text);
        command.Parameters.Add("$progress", SqliteType.Integer);
        command.Parameters.Add("$created_utc", SqliteType.Text);
        command.Parameters.Add("$updated_utc", SqliteType.Text);
        command.Parameters.Add("$approved_metadata_json", SqliteType.Text);
        command.Parameters.Add("$metadata_review_json", SqliteType.Text);
        command.Parameters.Add("$output_path", SqliteType.Text);
        command.Parameters.Add("$error_stage", SqliteType.Text);
        command.Parameters.Add("$user_error", SqliteType.Text);
        command.Parameters.Add("$technical_error", SqliteType.Text);
        command.Parameters.Add("$retryable", SqliteType.Integer);
        command.Parameters.Add("$retry_count", SqliteType.Integer);
        return command;
    }

    private static void BindJob(SqliteCommand command, QueueJobRecord job)
    {
        command.Parameters["$id"].Value = job.Id.ToString("D");
        command.Parameters["$source_path"].Value = job.SourcePath;
        command.Parameters["$source_fingerprint"].Value = job.SourceFingerprint;
        command.Parameters["$output_root"].Value = job.OutputRoot;
        command.Parameters["$requested_title"].Value = job.RequestedMetadata.Title;
        command.Parameters["$requested_series"].Value = job.RequestedMetadata.Series;
        command.Parameters["$requested_issue"].Value = DbValue(job.RequestedMetadata.IssueNumber);
        command.Parameters["$requested_volume"].Value = DbValue(job.RequestedMetadata.Volume);
        command.Parameters["$requested_year"].Value = DbValue(job.RequestedMetadata.PublicationYear);
        command.Parameters["$requested_language"].Value = job.RequestedMetadata.Language;
        command.Parameters["$state"].Value = job.State.ToString();
        command.Parameters["$progress"].Value = job.Progress;
        command.Parameters["$created_utc"].Value = FormatTimestamp(job.CreatedUtc);
        command.Parameters["$updated_utc"].Value = FormatTimestamp(job.UpdatedUtc);
        command.Parameters["$approved_metadata_json"].Value = DbValue(
            Serialize(job.ApprovedMetadata));
        command.Parameters["$metadata_review_json"].Value = DbValue(
            Serialize(job.MetadataReview));
        command.Parameters["$output_path"].Value = DbValue(job.OutputPath);
        command.Parameters["$error_stage"].Value = DbValue(job.ErrorStage);
        command.Parameters["$user_error"].Value = DbValue(job.UserError);
        command.Parameters["$technical_error"].Value = DbValue(job.TechnicalError);
        command.Parameters["$retryable"].Value = job.Retryable ? 1 : 0;
        command.Parameters["$retry_count"].Value = job.RetryCount;
    }

    private QueueJobRecord ReadJob(SqliteDataReader reader)
    {
        string idText = ReadRequiredString(reader, 0, "queue_jobs.id");
        if (!Guid.TryParseExact(idText, "D", out Guid id))
        {
            throw InvalidColumn("queue_jobs.id");
        }

        string stateText = ReadRequiredString(reader, 10, "queue_jobs.state");
        if (!Enum.TryParse(stateText, ignoreCase: false, out QueueJobState state) ||
            !string.Equals(
                Enum.GetName(typeof(QueueJobState), state),
                stateText,
                StringComparison.Ordinal))
        {
            throw InvalidColumn("queue_jobs.state");
        }

        int progress = ReadInt32(reader, 11, "queue_jobs.progress");
        if (progress is < 0 or > 100)
        {
            throw InvalidColumn("queue_jobs.progress");
        }

        int retryableValue = ReadInt32(reader, 20, "queue_jobs.retryable");
        if (retryableValue is not 0 and not 1)
        {
            throw InvalidColumn("queue_jobs.retryable");
        }

        int retryCount = ReadInt32(reader, 21, "queue_jobs.retry_count");
        if (retryCount < 0)
        {
            throw InvalidColumn("queue_jobs.retry_count");
        }

        var requestedMetadata = new ComicMetadata(
            ReadRequiredString(reader, 4, "queue_jobs.requested_title"),
            ReadRequiredString(reader, 5, "queue_jobs.requested_series"),
            ReadOptionalString(reader, 6, "queue_jobs.requested_issue"),
            ReadOptionalString(reader, 7, "queue_jobs.requested_volume"),
            ReadNullableInt32(reader, 8, "queue_jobs.requested_year"),
            ReadRequiredString(reader, 9, "queue_jobs.requested_language"));

        return new QueueJobRecord(
            id,
            ReadRequiredString(reader, 1, "queue_jobs.source_path"),
            ReadRequiredString(reader, 2, "queue_jobs.source_fingerprint"),
            ReadRequiredString(reader, 3, "queue_jobs.output_root"),
            requestedMetadata,
            state,
            progress,
            ReadTimestamp(reader, 12, "queue_jobs.created_utc"),
            ReadTimestamp(reader, 13, "queue_jobs.updated_utc"),
            DeserializeMetadata(
                ReadOptionalString(reader, 14, "queue_jobs.approved_metadata_json"),
                "queue_jobs.approved_metadata_json"),
            DeserializeReview(
                ReadOptionalString(reader, 15, "queue_jobs.metadata_review_json"),
                "queue_jobs.metadata_review_json"),
            ReadOptionalString(reader, 16, "queue_jobs.output_path"),
            ReadOptionalString(reader, 17, "queue_jobs.error_stage"),
            ReadOptionalString(reader, 18, "queue_jobs.user_error"),
            ReadOptionalString(reader, 19, "queue_jobs.technical_error"),
            retryableValue == 1,
            retryCount);
    }

    private string ReadRequiredString(
        SqliteDataReader reader,
        int ordinal,
        string columnName)
    {
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not string value)
        {
            throw InvalidColumn(columnName);
        }

        return value;
    }

    private string? ReadOptionalString(
        SqliteDataReader reader,
        int ordinal,
        string columnName) => reader.IsDBNull(ordinal)
        ? null
        : ReadRequiredString(reader, ordinal, columnName);

    private int ReadInt32(
        SqliteDataReader reader,
        int ordinal,
        string columnName)
    {
        if (reader.IsDBNull(ordinal) || reader.GetValue(ordinal) is not long value ||
            value is < int.MinValue or > int.MaxValue)
        {
            throw InvalidColumn(columnName);
        }

        return (int)value;
    }

    private int? ReadNullableInt32(
        SqliteDataReader reader,
        int ordinal,
        string columnName) => reader.IsDBNull(ordinal)
        ? null
        : ReadInt32(reader, ordinal, columnName);

    private DateTimeOffset ReadTimestamp(
        SqliteDataReader reader,
        int ordinal,
        string columnName)
    {
        string value = ReadRequiredString(reader, ordinal, columnName);
        if (!DateTimeOffset.TryParseExact(
                value,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset timestamp) ||
            !string.Equals(value, FormatTimestamp(timestamp), StringComparison.Ordinal))
        {
            throw InvalidColumn(columnName);
        }

        return timestamp.ToUniversalTime();
    }

    private ComicMetadata? DeserializeMetadata(string? json, string columnName)
    {
        ComicMetadata? metadata = Deserialize<ComicMetadata>(json, columnName);
        if (metadata is not null)
        {
            ValidateMetadata(metadata, columnName);
        }

        return metadata;
    }

    private QueueMetadataReview? DeserializeReview(string? json, string columnName)
    {
        QueueMetadataReview? review = Deserialize<QueueMetadataReview>(json, columnName);
        if (review is null)
        {
            return null;
        }

        if (review.OfflineMetadata is null || review.Candidates is null)
        {
            throw InvalidColumn(columnName);
        }

        ValidateMetadata(review.OfflineMetadata, columnName);
        foreach (QueueMetadataCandidate? candidate in review.Candidates)
        {
            if (candidate is null ||
                candidate.Provider is null ||
                candidate.Name is null ||
                candidate.Series is null)
            {
                throw InvalidColumn(columnName);
            }
        }

        return review;
    }

    private void ValidateMetadata(ComicMetadata metadata, string columnName)
    {
        if (metadata.Title is null ||
            metadata.Series is null ||
            metadata.Language is null)
        {
            throw InvalidColumn(columnName);
        }
    }

    private T? Deserialize<T>(string? json, string columnName)
        where T : class
    {
        if (json is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions) ??
                throw InvalidColumn(columnName);
        }
        catch (JsonException exception)
        {
            throw InvalidColumn(columnName, exception);
        }
        catch (NotSupportedException exception)
        {
            throw InvalidColumn(columnName, exception);
        }
    }

    private static string? Serialize<T>(T? value)
        where T : class => value is null
        ? null
        : JsonSerializer.Serialize(value, JsonOptions);

    private static string FormatTimestamp(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private InvalidDataException InvalidColumn(
        string columnName,
        Exception? innerException = null)
    {
        string message =
            $"Queue database '{normalizedDatabasePath}' contains invalid data in column " +
            $"'{columnName}'.";
        return innerException is null
            ? new InvalidDataException(message)
            : new InvalidDataException(message, innerException);
    }
}
