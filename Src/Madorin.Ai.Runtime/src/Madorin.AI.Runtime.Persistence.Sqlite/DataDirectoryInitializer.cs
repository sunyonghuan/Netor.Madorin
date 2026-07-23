using Microsoft.Data.Sqlite;

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
            await SqliteSchema.EnsureCreatedAsync(connection, ct).ConfigureAwait(false);
            var repository = new SqliteSessionRepository(connection);
            await repository.MarkInterruptedAsync(ct).ConfigureAwait(false);
            var meetingRepository = new SqliteMeetingRepository(connection);
            await meetingRepository.RecoverRunningInvocationsAsync(ct).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
