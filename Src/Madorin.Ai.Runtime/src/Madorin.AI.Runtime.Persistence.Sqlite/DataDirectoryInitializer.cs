using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Madorin.AI.Runtime.Persistence.Sqlite;

/// <summary>Creates the Runtime data layout and recovers SQLite lifecycle metadata.</summary>
public static class DataDirectoryInitializer
{
    private static readonly string[] Subdirectories =
    [
        "messages",
        "blobs",
        "checkpoints",
        "staging",
        "locks",
        "logs"
    ];

    /// <summary>Initializes the data directory and returns an open SQLite connection.</summary>
    public static async Task<SqliteConnection> InitializeAsync(
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ct.ThrowIfCancellationRequested();
        var fullPath = Path.GetFullPath(dataDirectory);
        Directory.CreateDirectory(fullPath);
        foreach (var subdirectory in Subdirectories)
        {
            Directory.CreateDirectory(Path.Combine(fullPath, subdirectory));
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(fullPath, "state.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            var quickCheck = await RunQuickCheckAsync(connection, ct).ConfigureAwait(false);
            if (!string.Equals(quickCheck, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SQLite quick_check failed for '{connection.DataSource}': {quickCheck}");
            }

            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            await repository.MarkInterruptedAsync(ct).ConfigureAwait(false);
            var meetingRepository = new SqliteMeetingRepository(connection);
            await meetingRepository.RecoverRunningInvocationsAsync(ct).ConfigureAwait(false);
            var wal = await CheckpointWalAsync(connection, ct).ConfigureAwait(false);
            var permanentRunningCount = await CountPermanentRunningAsync(connection, ct)
                .ConfigureAwait(false);
            if (permanentRunningCount != 0)
            {
                throw new InvalidDataException(
                    $"Startup recovery left {permanentRunningCount} permanently Running record(s).");
            }

            var recoverableToolIntentCount = await CountRecoverableToolIntentsAsync(connection, ct)
                .ConfigureAwait(false);
            await WriteRecoveryReportAsync(
                fullPath,
                quickCheck,
                wal,
                permanentRunningCount,
                recoverableToolIntentCount,
                ct).ConfigureAwait(false);
            return connection;
        }
        catch (SqliteException ex)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw new InvalidDataException(
                $"SQLite startup recovery failed for '{connection.DataSource}': {ex.Message}",
                ex);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<string> RunQuickCheckAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        return Convert.ToString(
                await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
                System.Globalization.CultureInfo.InvariantCulture)
            ?? "no result";
    }

    private static async Task<WalCheckpointResult> CheckpointWalAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(PASSIVE);";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (!await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            throw new InvalidDataException("SQLite WAL checkpoint returned no diagnostic row.");
        }

        return new WalCheckpointResult(
            reader.GetInt32(0),
            reader.GetInt32(1),
            reader.GetInt32(2));
    }

    private static async Task<int> CountPermanentRunningAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM runs
                 WHERE LOWER(status) IN ('accepted', 'preparing', 'running', 'waitingfortool', 'waitingforcredentials', 'persisting'))
              + (SELECT COUNT(*) FROM work_steps
                 WHERE LOWER(status) IN ('running', 'waiting_for_credentials'))
              + (SELECT COUNT(*) FROM work_sessions
                 WHERE LOWER(status) IN ('planning', 'executing', 'waiting_for_credentials'))
              + (SELECT COUNT(*) FROM work_step_attempts
                 WHERE LOWER(status) IN ('running', 'waiting_for_credentials'))
              + (SELECT COUNT(*) FROM work_background_jobs WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM meeting_invocations WHERE LOWER(status) = 'running');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<int> CountRecoverableToolIntentsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM tool_intents
            WHERE LOWER(status) IN ('sent', 'unknown');
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(ct).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task WriteRecoveryReportAsync(
        string dataDirectory,
        string quickCheck,
        WalCheckpointResult wal,
        int permanentRunningCount,
        int recoverableToolIntentCount,
        CancellationToken ct)
    {
        var reportPath = Path.Combine(dataDirectory, "logs", "startup-recovery.json");
        var temporaryPath = reportPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using (var writer = new Utf8JsonWriter(
                    stream,
                    new JsonWriterOptions { Indented = true }))
                {
                    writer.WriteStartObject();
                    writer.WriteString("completedAt", DateTimeOffset.UtcNow);
                    writer.WriteNumber("schemaVersion", SqliteSchema.CurrentVersion);
                    writer.WriteString("quickCheck", quickCheck);
                    writer.WriteStartObject("walCheckpoint");
                    writer.WriteNumber("busy", wal.Busy);
                    writer.WriteNumber("logFrames", wal.LogFrames);
                    writer.WriteNumber("checkpointedFrames", wal.CheckpointedFrames);
                    writer.WriteEndObject();
                    writer.WriteNumber("permanentRunningCount", permanentRunningCount);
                    writer.WriteNumber("recoverableToolIntentCount", recoverableToolIntentCount);
                    writer.WriteStartArray("retainedDirectories");
                    foreach (var subdirectory in Subdirectories)
                    {
                        var path = Path.Combine(dataDirectory, subdirectory);
                        writer.WriteStartObject();
                        writer.WriteString("name", subdirectory);
                        writer.WriteBoolean("exists", Directory.Exists(path));
                        writer.WriteNumber(
                            "entryCount",
                            Directory.EnumerateFileSystemEntries(path).Take(10_001).Count());
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    writer.WriteEndObject();
                    await writer.FlushAsync(ct).ConfigureAwait(false);
                }
            }

            File.Move(temporaryPath, reportPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private readonly record struct WalCheckpointResult(
        int Busy,
        int LogFrames,
        int CheckpointedFrames);
}
