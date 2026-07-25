using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Server;

/// <summary>Checks and repairs the Runtime data store through bounded maintenance operations.</summary>
public static class DatabaseMaintenanceService
{
    private static readonly string[] RebuildCapabilityDiagnostics =
    [
        "Run state cannot be reconstructed from JSONL alone.",
        "Tool Grants cannot be reconstructed from JSONL alone.",
        "Tool intents cannot be reconstructed from JSONL alone.",
        "Invocation snapshots cannot be reconstructed from JSONL alone."
    ];

    /// <summary>Checks SQLite and canonical JSONL integrity without modifying storage.</summary>
    public static async Task<DatabaseCheckResult> CheckAsync(
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        var issues = new List<DatabaseCheckIssue>();
        if (!Directory.Exists(fullDataDirectory))
        {
            issues.Add(Error(
                "data_directory_missing",
                $"Data directory '{fullDataDirectory}' was not found."));
            return CreateResult(fullDataDirectory, null, null, issues);
        }

        var messagesDirectory = Path.Combine(fullDataDirectory, "messages");
        if (!Directory.Exists(messagesDirectory))
        {
            issues.Add(Error(
                "messages_directory_missing",
                $"Messages directory '{messagesDirectory}' was not found."));
        }

        var sqlite = await InspectSqliteAsync(fullDataDirectory, issues, ct)
            .ConfigureAwait(false);
        var conversations = await ConversationIntegrityInspector.InspectAsync(
                fullDataDirectory,
                ct)
            .ConfigureAwait(false);
        CompareConversations(sqlite, conversations, issues);

        return CreateResult(
            fullDataDirectory,
            sqlite.SchemaVersion,
            sqlite.JournalMode,
            issues);
    }

    /// <summary>
    /// Repairs recognized JSONL tail damage and message-index drift under the workspace lease.
    /// </summary>
    public static async Task<DatabaseRepairResult> RepairAsync(
        string workspaceRoot,
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        WorkspaceWriteLock workspaceLock;
        try
        {
            workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspaceRoot,
                    "database-repair-" + Guid.NewGuid().ToString("N"),
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new DatabaseMaintenanceLockException(ex.Message, ex);
        }

        await using (workspaceLock.ConfigureAwait(false))
        {
            var before = await CheckAsync(dataDirectory, ct).ConfigureAwait(false);
            if (before.Healthy)
            {
                return new DatabaseRepairResult(
                    before.DataDirectory,
                    null,
                    [],
                    before,
                    before);
            }

            if (!before.CanRepair)
            {
                throw new InvalidDataException(
                    "Database repair is blocked by one or more non-repairable integrity issues.");
            }

            var sessionIds = before.Issues
                .Where(static issue => issue.Repairable && issue.SessionId is not null)
                .Select(static issue => issue.SessionId!)
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            string? backupPath = null;
            try
            {
                backupPath = await CreateRecoveryPointAsync(
                        before.DataDirectory,
                        sessionIds,
                        "repair-",
                        ct)
                    .ConfigureAwait(false);
                var store = new ConversationStore(before.DataDirectory);
                foreach (var sessionId in sessionIds)
                {
                    ct.ThrowIfCancellationRequested();
                    await store.RepairIfNeededAsync(sessionId, ct).ConfigureAwait(false);
                }

                var after = await CheckAsync(before.DataDirectory, ct).ConfigureAwait(false);
                if (!after.Healthy)
                {
                    throw new InvalidDataException(
                        "Database repair completed its writes but integrity issues remain.");
                }

                return new DatabaseRepairResult(
                    before.DataDirectory,
                    backupPath,
                    sessionIds,
                    before,
                    after);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or SqliteException)
            {
                throw new DatabaseRepairException(
                    backupPath is null
                        ? $"Database repair did not start because its recovery point failed: {ex.Message}"
                        : $"Database repair failed. Recovery point retained at '{backupPath}': {ex.Message}",
                    backupPath,
                    ex);
            }
        }
    }

    /// <summary>Scans canonical messages and creates a rebuild plan without modifying storage.</summary>
    public static async Task<DatabaseRebuildPlan> PlanRebuildAsync(
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Data directory '{fullDataDirectory}' was not found.");
        }

        var messagesDirectory = Path.Combine(fullDataDirectory, "messages");
        if (!Directory.Exists(messagesDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Messages directory '{messagesDirectory}' was not found.");
        }

        var inspections = await ConversationIntegrityInspector.InspectAsync(
                fullDataDirectory,
                ct)
            .ConfigureAwait(false);
        var recoverable = new List<string>();
        var unrecoverable = new List<string>(RebuildCapabilityDiagnostics);
        var sessionsRecoverable = 0;
        var messagesRecoverable = 0;
        foreach (var inspection in inspections)
        {
            ct.ThrowIfCancellationRequested();
            if (inspection.Status == ConversationIntegrityStatus.UnrecoverableDamage)
            {
                unrecoverable.Add(
                    $"Session '{inspection.SessionId}' cannot be rebuilt: {inspection.Diagnostic}");
                continue;
            }

            sessionsRecoverable++;
            messagesRecoverable += inspection.Records.Length;
            recoverable.Add(inspection.Status == ConversationIntegrityStatus.Valid
                ? $"Session '{inspection.SessionId}': {inspection.Records.Length} canonical message(s) can be restored."
                : $"Session '{inspection.SessionId}': {inspection.Records.Length} canonical message(s) can be restored; the damaged final record will remain unchanged and will not be indexed.");
        }

        return new DatabaseRebuildPlan(
            fullDataDirectory,
            inspections.Count,
            sessionsRecoverable,
            messagesRecoverable,
            [.. recoverable],
            [.. unrecoverable]);
    }

    /// <summary>Replaces the database and rebuilds recoverable indexes under the workspace lease.</summary>
    public static async Task<DatabaseRebuildResult> RebuildAsync(
        string workspaceRoot,
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        WorkspaceWriteLock workspaceLock;
        try
        {
            workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspaceRoot,
                    "database-rebuild-" + Guid.NewGuid().ToString("N"),
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new DatabaseMaintenanceLockException(ex.Message, ex);
        }

        await using (workspaceLock.ConfigureAwait(false))
        {
            string? backupPath = null;
            try
            {
                var plan = await PlanRebuildAsync(dataDirectory, ct).ConfigureAwait(false);
                backupPath = await CreateRebuildRecoveryPointAsync(
                        plan.DataDirectory,
                        ct)
                    .ConfigureAwait(false);
                DeleteExistingDatabaseFiles(plan.DataDirectory);

                await using (var connection = await DataDirectoryInitializer.InitializeAsync(
                                 plan.DataDirectory,
                                 ct)
                             .ConfigureAwait(false))
                {
                }

                var store = new ConversationStore(plan.DataDirectory);
                var report = await store.RebuildIndexesFromRecoveryPointAsync(
                        backupPath,
                        ct)
                    .ConfigureAwait(false);
                return new DatabaseRebuildResult(
                    plan.DataDirectory,
                    report.SessionsScanned,
                    report.SessionsRebuilt,
                    report.MessagesUpserted,
                    backupPath,
                    report.RecoverableItems,
                    report.UnrecoverableDiagnostics);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or JsonException
                or SqliteException)
            {
                throw new DatabaseRebuildException(
                    backupPath is null
                        ? $"Database rebuild did not start because its recovery point failed: {ex.Message}"
                        : $"Database rebuild failed. Recovery point retained at '{backupPath}': {ex.Message}",
                    backupPath,
                    ex);
            }
        }
    }

    /// <summary>Compacts the Runtime database under the workspace lease.</summary>
    public static async Task<DatabaseVacuumResult> VacuumAsync(
        string workspaceRoot,
        string dataDirectory,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Data directory '{fullDataDirectory}' was not found.");
        }

        var databasePath = Path.Combine(fullDataDirectory, "state.db");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                $"State database '{databasePath}' was not found.",
                databasePath);
        }

        WorkspaceWriteLock workspaceLock;
        try
        {
            workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspaceRoot,
                    "database-vacuum-" + Guid.NewGuid().ToString("N"),
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new DatabaseMaintenanceLockException(ex.Message, ex);
        }

        await using (workspaceLock.ConfigureAwait(false))
        {
            try
            {
                if (!File.Exists(databasePath))
                {
                    throw new FileNotFoundException(
                        $"State database '{databasePath}' disappeared before compaction.",
                        databasePath);
                }

                var bytesBefore = new FileInfo(databasePath).Length;
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString();
                await using (var connection = new SqliteConnection(connectionString))
                {
                    await connection.OpenAsync(ct).ConfigureAwait(false);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "VACUUM;";
                    await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                var bytesAfter = new FileInfo(databasePath).Length;
                return new DatabaseVacuumResult(
                    fullDataDirectory,
                    databasePath,
                    bytesBefore,
                    bytesAfter);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or SqliteException)
            {
                throw new DatabaseVacuumException(
                    $"Database vacuum failed: {ex.Message}",
                    ex);
            }
        }
    }

    /// <summary>Creates a read-only plan for a forward database migration.</summary>
    public static async Task<DatabaseMigrationPlan> PlanMigrationAsync(
        string dataDirectory,
        int? targetVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        var resolvedTargetVersion = targetVersion ?? SqliteSchema.CurrentVersion;
        if (resolvedTargetVersion < 0 || resolvedTargetVersion > SqliteSchema.CurrentVersion)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetVersion),
                targetVersion,
                $"Target schema version must be between 0 and {SqliteSchema.CurrentVersion}.");
        }

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Data directory '{fullDataDirectory}' was not found.");
        }

        var databasePath = Path.Combine(fullDataDirectory, "state.db");
        if (!File.Exists(databasePath))
        {
            throw new FileNotFoundException(
                $"State database '{databasePath}' was not found.",
                databasePath);
        }

        try
        {
            var inspectionIssues = new List<DatabaseCheckIssue>();
            await using var inspectionConnection = await OpenInspectionConnectionAsync(
                    databasePath,
                    File.Exists(databasePath + "-wal"),
                    inspectionIssues,
                    ct)
                .ConfigureAwait(false);
            if (inspectionConnection is null)
            {
                throw new InvalidDataException(
                    inspectionIssues.Count == 0
                        ? "Database changed while the migration plan was being created."
                        : string.Join(" ", inspectionIssues.Select(static issue => issue.Message)));
            }

            var fromVersion = await SqliteSchema.ReadCurrentVersionAsync(
                    inspectionConnection.Connection,
                    ct)
                .ConfigureAwait(false);
            if (fromVersion > SqliteSchema.CurrentVersion)
            {
                throw new InvalidDataException(
                    $"The database schema version {fromVersion} is newer than supported version {SqliteSchema.CurrentVersion}.");
            }

            if (resolvedTargetVersion < fromVersion)
            {
                throw new ArgumentException(
                    $"Schema downgrade from version {fromVersion} to {resolvedTargetVersion} is not supported.",
                    nameof(targetVersion));
            }

            return new DatabaseMigrationPlan(
                fullDataDirectory,
                databasePath,
                fromVersion,
                resolvedTargetVersion,
                [.. Enumerable.Range(fromVersion + 1, resolvedTargetVersion - fromVersion)]);
        }
        catch (SqliteException ex)
        {
            throw new InvalidDataException(
                $"Database migration planning failed for '{databasePath}': {ex.Message}",
                ex);
        }
    }

    /// <summary>Migrates the Runtime database forward under the workspace lease.</summary>
    public static async Task<DatabaseMigrationResult> MigrateAsync(
        string workspaceRoot,
        string dataDirectory,
        int? targetVersion = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        _ = await PlanMigrationAsync(dataDirectory, targetVersion, ct).ConfigureAwait(false);

        WorkspaceWriteLock workspaceLock;
        try
        {
            workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                    workspaceRoot,
                    "database-migrate-" + Guid.NewGuid().ToString("N"),
                    ct: ct)
                .ConfigureAwait(false);
        }
        catch (InvalidOperationException ex)
        {
            throw new DatabaseMaintenanceLockException(ex.Message, ex);
        }

        await using (workspaceLock.ConfigureAwait(false))
        {
            var plan = await PlanMigrationAsync(dataDirectory, targetVersion, ct)
                .ConfigureAwait(false);
            if (!plan.RequiresMigration)
            {
                return new DatabaseMigrationResult(
                    plan.DataDirectory,
                    plan.DatabasePath,
                    plan.FromVersion,
                    plan.TargetVersion,
                    [],
                    null);
            }

            string? backupPath = null;
            try
            {
                backupPath = await CreateRecoveryPointAsync(
                        plan.DataDirectory,
                        [],
                        "migrate-",
                        ct)
                    .ConfigureAwait(false);
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = plan.DatabasePath,
                    Mode = SqliteOpenMode.ReadWrite,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false,
                    DefaultTimeout = 5
                }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(ct).ConfigureAwait(false);
                await SqliteSchema.MigrateAsync(
                        connection,
                        plan.FromVersion,
                        plan.TargetVersion,
                        ct)
                    .ConfigureAwait(false);
                var migratedVersion = await SqliteSchema.ReadCurrentVersionAsync(connection, ct)
                    .ConfigureAwait(false);
                if (migratedVersion != plan.TargetVersion)
                {
                    throw new InvalidDataException(
                        $"Database migration ended at schema version {migratedVersion}, expected {plan.TargetVersion}.");
                }

                return new DatabaseMigrationResult(
                    plan.DataDirectory,
                    plan.DatabasePath,
                    plan.FromVersion,
                    plan.TargetVersion,
                    [.. plan.PendingVersions],
                    backupPath);
            }
            catch (Exception ex) when (ex is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or InvalidOperationException
                or SqliteException)
            {
                throw new DatabaseMigrationException(
                    backupPath is null
                        ? $"Database migration did not start because its recovery point failed: {ex.Message}"
                        : $"Database migration failed. Recovery point retained at '{backupPath}': {ex.Message}",
                    backupPath,
                    ex);
            }
        }
    }

    /// <summary>
    /// Creates a consistent SQLite online backup and optionally archives canonical messages.
    /// </summary>
    public static async Task<DatabaseBackupResult> BackupAsync(
        string workspaceRoot,
        string dataDirectory,
        string? outputPath = null,
        bool includeMessages = false,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (outputPath is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        }

        var fullDataDirectory = Path.GetFullPath(dataDirectory);
        if (!Directory.Exists(fullDataDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Data directory '{fullDataDirectory}' was not found.");
        }

        var sourceDatabasePath = Path.Combine(fullDataDirectory, "state.db");
        if (!File.Exists(sourceDatabasePath))
        {
            throw new FileNotFoundException(
                $"State database '{sourceDatabasePath}' was not found.",
                sourceDatabasePath);
        }

        var databaseBackupPath = ResolveDatabaseBackupPath(fullDataDirectory, outputPath);
        var messagesArchivePath = includeMessages
            ? Path.ChangeExtension(databaseBackupPath, ".messages.zip")
            : null;
        EnsureBackupOutputsAvailable(databaseBackupPath, messagesArchivePath);

        WorkspaceWriteLock? workspaceLock = null;
        if (includeMessages)
        {
            try
            {
                workspaceLock = await WorkspaceWriteLock.AcquireAsync(
                        workspaceRoot,
                        "database-backup-" + Guid.NewGuid().ToString("N"),
                        ct: ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidOperationException ex)
            {
                throw new DatabaseMaintenanceLockException(ex.Message, ex);
            }
        }

        try
        {
            EnsureBackupOutputsAvailable(databaseBackupPath, messagesArchivePath);
            return await CreateBackupAsync(
                    fullDataDirectory,
                    sourceDatabasePath,
                    databaseBackupPath,
                    messagesArchivePath,
                    ct)
                .ConfigureAwait(false);
        }
        finally
        {
            if (workspaceLock is not null)
            {
                await workspaceLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task<DatabaseBackupResult> CreateBackupAsync(
        string dataDirectory,
        string sourceDatabasePath,
        string databaseBackupPath,
        string? messagesArchivePath,
        CancellationToken ct)
    {
        var messageFiles = messagesArchivePath is null
            ? []
            : EnumerateMessageFiles(dataDirectory);
        var databaseDirectory = Path.GetDirectoryName(databaseBackupPath)
            ?? throw new InvalidDataException("Database backup output has no parent directory.");
        Directory.CreateDirectory(databaseDirectory);
        var databaseTemporaryPath = CreateTemporaryFile(databaseBackupPath);
        string? archiveTemporaryPath = null;
        var databasePublished = false;
        var archivePublished = false;
        try
        {
            await BackupSqliteAsync(
                    sourceDatabasePath,
                    databaseTemporaryPath,
                    ct)
                .ConfigureAwait(false);
            await FlushFileToDiskAsync(databaseTemporaryPath, ct).ConfigureAwait(false);

            var messageFileCount = 0;
            if (messagesArchivePath is not null)
            {
                var archiveDirectory = Path.GetDirectoryName(messagesArchivePath)
                    ?? throw new InvalidDataException(
                        "Messages archive output has no parent directory.");
                Directory.CreateDirectory(archiveDirectory);
                archiveTemporaryPath = CreateTemporaryFile(messagesArchivePath);
                messageFileCount = await CreateMessagesArchiveAsync(
                        messageFiles,
                        archiveTemporaryPath,
                        ct)
                    .ConfigureAwait(false);
            }

            EnsureBackupOutputsAvailable(databaseBackupPath, messagesArchivePath);
            File.Move(databaseTemporaryPath, databaseBackupPath);
            databasePublished = true;
            if (messagesArchivePath is not null && archiveTemporaryPath is not null)
            {
                File.Move(archiveTemporaryPath, messagesArchivePath);
                archivePublished = true;
            }

            DeleteSqliteSidecars(databaseTemporaryPath);
            return new DatabaseBackupResult(
                dataDirectory,
                databaseBackupPath,
                messagesArchivePath,
                messageFileCount);
        }
        catch (Exception ex)
        {
            var cleanupException = TryCleanupBackupArtifacts(
                databaseTemporaryPath,
                archiveTemporaryPath,
                databasePublished ? databaseBackupPath : null,
                archivePublished ? messagesArchivePath : null);
            if (ex is OperationCanceledException && cleanupException is null)
            {
                throw;
            }

            var innerException = cleanupException is null
                ? ex
                : new AggregateException(ex, cleanupException);
            throw new DatabaseBackupException(
                cleanupException is null
                    ? $"Database backup failed: {ex.Message}"
                    : $"Database backup failed and incomplete artifacts could not be fully removed: {ex.Message}",
                innerException);
        }
    }

    private static async Task BackupSqliteAsync(
        string sourceDatabasePath,
        string destinationDatabasePath,
        CancellationToken ct)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourceDatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationDatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();

        await using var source = new SqliteConnection(sourceConnectionString);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await source.OpenAsync(ct).ConfigureAwait(false);
        await destination.OpenAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        source.BackupDatabase(destination);
        ct.ThrowIfCancellationRequested();
    }

    private static async Task<int> CreateMessagesArchiveAsync(
        IReadOnlyList<MessageArchiveFile> messageFiles,
        string archivePath,
        CancellationToken ct)
    {
        await using var archiveStream = new FileStream(archivePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        });
        archiveStream.SetLength(0);
        using (var archive = new ZipArchive(
                   archiveStream,
                   ZipArchiveMode.Create,
                   leaveOpen: true))
        {
            foreach (var messageFile in messageFiles)
            {
                ct.ThrowIfCancellationRequested();
                var entry = archive.CreateEntry(
                    messageFile.EntryName,
                    CompressionLevel.NoCompression);
                await using var source = new FileStream(messageFile.SourcePath, new FileStreamOptions
                {
                    Mode = FileMode.Open,
                    Access = FileAccess.Read,
                    Share = FileShare.Read,
                    BufferSize = 64 * 1024,
                    Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                });
                await using var destination = entry.Open();
                await source.CopyToAsync(destination, 64 * 1024, ct).ConfigureAwait(false);
            }
        }

        await archiveStream.FlushAsync(ct).ConfigureAwait(false);
        archiveStream.Flush(flushToDisk: true);
        return messageFiles.Count;
    }

    private static MessageArchiveFile[] EnumerateMessageFiles(string dataDirectory)
    {
        var messagesDirectory = Path.Combine(dataDirectory, "messages");
        if (!Directory.Exists(messagesDirectory))
        {
            throw new DirectoryNotFoundException(
                $"Messages directory '{messagesDirectory}' was not found.");
        }

        return Directory.EnumerateFiles(
                messagesDirectory,
                "*",
                SearchOption.AllDirectories)
            .Select(path => new MessageArchiveFile(
                path,
                Path.GetRelativePath(messagesDirectory, path).Replace('\\', '/')))
            .OrderBy(static file => file.EntryName, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task FlushFileToDiskAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.ReadWrite,
            Share = FileShare.Read,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        });
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static string ResolveDatabaseBackupPath(
        string dataDirectory,
        string? outputPath)
    {
        if (outputPath is not null)
        {
            return Path.GetFullPath(outputPath);
        }

        return Path.Combine(
            dataDirectory,
            "backups",
            "database-"
            + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
            + "-"
            + Guid.NewGuid().ToString("N")
            + ".db");
    }

    private static void EnsureBackupOutputsAvailable(
        string databaseBackupPath,
        string? messagesArchivePath)
    {
        EnsureBackupOutputAvailable(databaseBackupPath);
        if (messagesArchivePath is not null)
        {
            EnsureBackupOutputAvailable(messagesArchivePath);
        }
    }

    private static void EnsureBackupOutputAvailable(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new IOException($"Backup output '{path}' already exists.");
        }
    }

    private static string CreateTemporaryFile(string outputPath)
    {
        var directory = Path.GetDirectoryName(outputPath)
            ?? throw new InvalidDataException("Backup output has no parent directory.");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var temporaryPath = Path.Combine(
                directory,
                "." + Path.GetFileName(outputPath) + "." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                return temporaryPath;
            }
            catch (IOException) when (File.Exists(temporaryPath))
            {
                continue;
            }
        }

        throw new IOException($"Could not reserve a temporary file for '{outputPath}'.");
    }

    private static Exception? TryCleanupBackupArtifacts(params string?[] paths)
    {
        List<Exception>? failures = null;
        foreach (var path in paths.Where(static path => path is not null))
        {
            try
            {
                File.Delete(path!);
                DeleteSqliteSidecars(path!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures ??= [];
                failures.Add(ex);
            }
        }

        return failures switch
        {
            null => null,
            [var failure] => failure,
            _ => new AggregateException(failures)
        };
    }

    private static void DeleteSqliteSidecars(string databasePath)
    {
        File.Delete(databasePath + "-wal");
        File.Delete(databasePath + "-shm");
        File.Delete(databasePath + "-journal");
    }

    private static async Task<SqliteInspection> InspectSqliteAsync(
        string dataDirectory,
        List<DatabaseCheckIssue> issues,
        CancellationToken ct)
    {
        var databasePath = Path.Combine(dataDirectory, "state.db");
        if (!File.Exists(databasePath))
        {
            issues.Add(Error(
                "state_database_missing",
                $"State database '{databasePath}' was not found."));
            return SqliteInspection.Empty;
        }

        var walPath = databasePath + "-wal";
        var hasWal = File.Exists(walPath);
        await using var inspectionConnection = await OpenInspectionConnectionAsync(
                databasePath,
                hasWal,
                issues,
                ct)
            .ConfigureAwait(false);
        if (inspectionConnection is null)
        {
            return SqliteInspection.Empty;
        }

        var connection = inspectionConnection.Connection;
        try
        {
            await InspectPragmaAsync(
                    connection,
                    "PRAGMA quick_check;",
                    "sqlite_quick_check_failed",
                    issues,
                    ct)
                .ConfigureAwait(false);
            await InspectPragmaAsync(
                    connection,
                    "PRAGMA integrity_check;",
                    "sqlite_integrity_check_failed",
                    issues,
                    ct)
                .ConfigureAwait(false);
            await InspectForeignKeysAsync(connection, issues, ct).ConfigureAwait(false);

            var journalMode = await ReadJournalModeAsync(databasePath, ct).ConfigureAwait(false);
            if (!string.Equals(journalMode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(Error(
                    "journal_mode_not_wal",
                    $"SQLite journal mode is '{journalMode ?? "unknown"}', expected 'wal'."));
            }

            int? schemaVersion = null;
            try
            {
                schemaVersion = await SqliteSchema.ValidateReadOnlyAsync(connection, ct)
                    .ConfigureAwait(false);
            }
            catch (InvalidDataException ex)
            {
                var code = ex.Message.Contains("Missing:", StringComparison.Ordinal)
                    ? "schema_incomplete"
                    : "schema_version_incompatible";
                issues.Add(Error(code, ex.Message));
            }
            catch (SqliteException ex)
            {
                issues.Add(Error("schema_incomplete", ex.Message));
            }

            var sessions = await SchemaObjectExistsAsync(connection, "table", "sessions", ct)
                    .ConfigureAwait(false)
                ? await ReadSessionsAsync(connection, ct).ConfigureAwait(false)
                : new HashSet<string>(StringComparer.Ordinal);
            var indexes = await SchemaObjectExistsAsync(connection, "table", "message_index", ct)
                    .ConfigureAwait(false)
                ? await ReadMessageIndexesAsync(connection, ct).ConfigureAwait(false)
                : new Dictionary<string, DatabaseIndexRecord[]>(StringComparer.Ordinal);
            return new SqliteInspection(schemaVersion, journalMode, sessions, indexes);
        }
        catch (SqliteException ex)
        {
            issues.Add(Error("sqlite_open_or_read_failed", ex.Message));
            return SqliteInspection.Empty;
        }
    }

    private static async Task<DatabaseInspectionConnection?> OpenInspectionConnectionAsync(
        string databasePath,
        bool hasWal,
        List<DatabaseCheckIssue> issues,
        CancellationToken ct)
    {
        if (!hasWal)
        {
            var connection = CreateReadOnlyConnection(
                new Uri(databasePath).AbsoluteUri + "?immutable=1");
            await connection.OpenAsync(ct).ConfigureAwait(false);
            return new DatabaseInspectionConnection(connection, temporaryDirectory: null);
        }

        var temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "madorin-db-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            var snapshotDatabasePath = Path.Combine(temporaryDirectory, "state.db");
            var snapshotWalPath = snapshotDatabasePath + "-wal";
            var snapshotCreated = false;
            for (var attempt = 0; attempt < 3; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                var before = CaptureDatabaseFiles(databasePath);
                if (before.Wal is null)
                {
                    break;
                }

                try
                {
                    await CopySharedFileAsync(databasePath, snapshotDatabasePath, ct)
                        .ConfigureAwait(false);
                    await CopySharedFileAsync(databasePath + "-wal", snapshotWalPath, ct)
                        .ConfigureAwait(false);
                }
                catch (IOException) when (!File.Exists(databasePath)
                    || !File.Exists(databasePath + "-wal"))
                {
                    File.Delete(snapshotDatabasePath);
                    File.Delete(snapshotWalPath);
                    continue;
                }

                var after = CaptureDatabaseFiles(databasePath);
                if (before == after)
                {
                    snapshotCreated = true;
                    break;
                }

                File.Delete(snapshotDatabasePath);
                File.Delete(snapshotWalPath);
            }

            if (!snapshotCreated)
            {
                issues.Add(Error(
                    "database_changed_during_check",
                    "SQLite database files changed while the read-only inspection snapshot was being created."));
                Directory.Delete(temporaryDirectory, recursive: true);
                return null;
            }

            var snapshotConnection = CreateReadOnlyConnection(snapshotDatabasePath);
            await snapshotConnection.OpenAsync(ct).ConfigureAwait(false);
            return new DatabaseInspectionConnection(snapshotConnection, temporaryDirectory);
        }
        catch
        {
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }

            throw;
        }
    }

    private static async Task CopySharedFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using var source = new FileStream(
            sourcePath,
            new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        await using var destination = new FileStream(
            destinationPath,
            new FileStreamOptions
            {
                Mode = FileMode.Create,
                Access = FileAccess.Write,
                Share = FileShare.None,
                BufferSize = 64 * 1024,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan
            });
        await source.CopyToAsync(destination, 64 * 1024, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static SqliteConnection CreateReadOnlyConnection(string dataSource)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dataSource,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString();
        return new SqliteConnection(connectionString);
    }

    private static DatabaseFileSnapshot CaptureDatabaseFiles(string databasePath) =>
        new(
            CaptureFile(databasePath),
            CaptureFile(databasePath + "-wal"));

    private static DatabaseFileIdentity? CaptureFile(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        var info = new FileInfo(path);
        return new DatabaseFileIdentity(info.Length, info.LastWriteTimeUtc);
    }

    private static async Task InspectPragmaAsync(
        SqliteConnection connection,
        string sql,
        string issueCode,
        List<DatabaseCheckIssue> issues,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var diagnostics = new List<string>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var value = reader.GetString(0);
            if (!string.Equals(value, "ok", StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(value);
            }
        }

        if (diagnostics.Count > 0)
        {
            issues.Add(Error(issueCode, string.Join("; ", diagnostics)));
        }
    }

    private static async Task InspectForeignKeysAsync(
        SqliteConnection connection,
        List<DatabaseCheckIssue> issues,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_key_check;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var count = 0;
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            count++;
        }

        if (count > 0)
        {
            issues.Add(Error(
                "foreign_key_violation",
                $"SQLite reported {count} foreign-key violation(s)."));
        }
    }

    private static async Task<HashSet<string>> ReadSessionsAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT session_id FROM sessions ORDER BY session_id;";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var sessions = new HashSet<string>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            sessions.Add(reader.GetString(0));
        }

        return sessions;
    }

    private static async Task<Dictionary<string, DatabaseIndexRecord[]>> ReadMessageIndexesAsync(
        SqliteConnection connection,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT session_id, message_id, sequence, file_offset, record_length
            FROM message_index
            ORDER BY session_id, sequence, message_id;
            """;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        var records = new Dictionary<string, List<DatabaseIndexRecord>>(StringComparer.Ordinal);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var sessionId = reader.GetString(0);
            if (!records.TryGetValue(sessionId, out var sessionRecords))
            {
                sessionRecords = [];
                records.Add(sessionId, sessionRecords);
            }

            sessionRecords.Add(new DatabaseIndexRecord(
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetInt64(3),
                reader.GetInt64(4)));
        }

        return records.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static void CompareConversations(
        SqliteInspection sqlite,
        IReadOnlyList<ConversationFileIntegrityResult> conversations,
        List<DatabaseCheckIssue> issues)
    {
        var filesBySession = conversations.ToDictionary(
            static file => file.SessionId,
            StringComparer.Ordinal);
        foreach (var file in conversations)
        {
            if (sqlite.CanCompareRelationships && !sqlite.SessionIds.Contains(file.SessionId))
            {
                issues.Add(Error(
                    "message_file_without_session",
                    $"Conversation file '{file.FilePath}' has no matching SQLite Session.",
                    sessionId: file.SessionId));
            }

            switch (file.Status)
            {
                case ConversationIntegrityStatus.RecoverableTailDamage:
                    issues.Add(Error(
                        "jsonl_tail_damage",
                        $"Session '{file.SessionId}' has a damaged final JSONL record: {file.Diagnostic}",
                        repairable: sqlite.CanCompareRelationships
                            && sqlite.SessionIds.Contains(file.SessionId),
                        sessionId: file.SessionId));
                    break;
                case ConversationIntegrityStatus.UnrecoverableDamage:
                    issues.Add(Error(
                        "jsonl_middle_damage",
                        $"Session '{file.SessionId}' contains unrecoverable JSONL damage: {file.Diagnostic}",
                        sessionId: file.SessionId));
                    break;
            }

            if (!sqlite.CanCompareRelationships
                || !sqlite.SessionIds.Contains(file.SessionId)
                || file.Status == ConversationIntegrityStatus.UnrecoverableDamage)
            {
                continue;
            }

            var expected = file.Records.Select(static record => new DatabaseIndexRecord(
                    record.MessageId,
                    record.Sequence,
                    record.FileOffset,
                    record.RecordLength))
                .ToArray();
            var actual = sqlite.Indexes.GetValueOrDefault(file.SessionId) ?? [];
            if (!expected.SequenceEqual(actual))
            {
                issues.Add(Error(
                    "message_index_mismatch",
                    $"Session '{file.SessionId}' message index does not match canonical JSONL.",
                    repairable: true,
                    sessionId: file.SessionId));
            }
        }

        if (!sqlite.CanCompareRelationships)
        {
            return;
        }

        foreach (var sessionId in sqlite.SessionIds.Order(StringComparer.Ordinal))
        {
            if (!filesBySession.ContainsKey(sessionId))
            {
                issues.Add(Error(
                    "session_without_message_file",
                    $"SQLite Session '{sessionId}' has no canonical JSONL file.",
                    sessionId: sessionId));
            }
        }
    }

    private static async Task<string> CreateRebuildRecoveryPointAsync(
        string dataDirectory,
        CancellationToken ct)
    {
        var backupRoot = Path.Combine(dataDirectory, "backups");
        var createdBackupRoot = !Directory.Exists(backupRoot);
        string? backupPath = null;
        try
        {
            Directory.CreateDirectory(backupRoot);
            backupPath = Path.Combine(
                backupRoot,
                "rebuild-"
                + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupPath);
            foreach (var fileName in new[] { "state.db", "state.db-wal", "state.db-shm" })
            {
                var sourcePath = Path.Combine(dataDirectory, fileName);
                if (File.Exists(sourcePath))
                {
                    await CopyFileAsync(
                            sourcePath,
                            Path.Combine(backupPath, fileName),
                            ct)
                        .ConfigureAwait(false);
                }
            }

            return backupPath;
        }
        catch
        {
            if (backupPath is not null && Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }

            if (createdBackupRoot
                && Directory.Exists(backupRoot)
                && !Directory.EnumerateFileSystemEntries(backupRoot).Any())
            {
                Directory.Delete(backupRoot);
            }

            throw;
        }
    }

    private static void DeleteExistingDatabaseFiles(string dataDirectory)
    {
        foreach (var fileName in new[] { "state.db", "state.db-wal", "state.db-shm" })
        {
            File.Delete(Path.Combine(dataDirectory, fileName));
        }
    }

    private static async Task<string> CreateRecoveryPointAsync(
        string dataDirectory,
        IReadOnlyList<string> sessionIds,
        string backupPrefix,
        CancellationToken ct)
    {
        var backupRoot = Path.Combine(dataDirectory, "backups");
        var createdBackupRoot = !Directory.Exists(backupRoot);
        string? backupPath = null;
        try
        {
            Directory.CreateDirectory(backupRoot);
            backupPath = Path.Combine(
                backupRoot,
                backupPrefix
                + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)
                + "-"
                + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(backupPath);

            foreach (var fileName in new[] { "state.db", "state.db-wal", "state.db-shm" })
            {
                var source = Path.Combine(dataDirectory, fileName);
                if (File.Exists(source))
                {
                    await CopyFileAsync(source, Path.Combine(backupPath, fileName), ct)
                        .ConfigureAwait(false);
                }
            }

            var backupMessages = Path.Combine(backupPath, "messages");
            Directory.CreateDirectory(backupMessages);
            foreach (var sessionId in sessionIds)
            {
                var source = Path.Combine(dataDirectory, "messages", sessionId + ".jsonl");
                if (!File.Exists(source))
                {
                    throw new FileNotFoundException(
                        $"Canonical JSONL for Session '{sessionId}' disappeared before backup.",
                        source);
                }

                await CopyFileAsync(
                        source,
                        Path.Combine(backupMessages, sessionId + ".jsonl"),
                        ct)
                    .ConfigureAwait(false);
            }

            return backupPath;
        }
        catch
        {
            if (backupPath is not null && Directory.Exists(backupPath))
            {
                Directory.Delete(backupPath, recursive: true);
            }

            if (createdBackupRoot
                && Directory.Exists(backupRoot)
                && !Directory.EnumerateFileSystemEntries(backupRoot).Any())
            {
                Directory.Delete(backupRoot);
            }

            throw;
        }
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken ct)
    {
        await using var source = new FileStream(sourcePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        await using var destination = new FileStream(destinationPath, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            BufferSize = 64 * 1024,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough
        });
        await source.CopyToAsync(destination, 64 * 1024, ct).ConfigureAwait(false);
        await destination.FlushAsync(ct).ConfigureAwait(false);
        destination.Flush(flushToDisk: true);
    }

    private static async Task<string?> ReadJournalModeAsync(
        string databasePath,
        CancellationToken ct)
    {
        var header = new byte[20];
        await using var stream = new FileStream(databasePath, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            BufferSize = 4096,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });
        var read = await stream.ReadAtLeastAsync(
                header,
                header.Length,
                throwOnEndOfStream: false,
                cancellationToken: ct)
            .ConfigureAwait(false);
        if (read < header.Length)
        {
            return null;
        }

        return header[18] == 2 && header[19] == 2 ? "wal" : "delete";
    }

    private static async Task<bool> SchemaObjectExistsAsync(
        SqliteConnection connection,
        string type,
        string name,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = $type AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$type", type);
        command.Parameters.AddWithValue("$name", name);
        return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is not null;
    }

    private static DatabaseCheckResult CreateResult(
        string dataDirectory,
        int? schemaVersion,
        string? journalMode,
        List<DatabaseCheckIssue> issues) =>
        new(
            dataDirectory,
            schemaVersion,
            SqliteSchema.CurrentVersion,
            journalMode,
            [.. issues.OrderBy(static issue => issue.Code, StringComparer.Ordinal)
                .ThenBy(static issue => issue.SessionId, StringComparer.Ordinal)]);

    private static DatabaseCheckIssue Error(
        string code,
        string message,
        bool repairable = false,
        string? sessionId = null) =>
        new(code, "error", message, repairable, sessionId);

    private sealed record SqliteInspection(
        int? SchemaVersion,
        string? JournalMode,
        HashSet<string> SessionIds,
        Dictionary<string, DatabaseIndexRecord[]> Indexes,
        bool CanCompareRelationships = true)
    {
        public static SqliteInspection Empty { get; } = new(
            null,
            null,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, DatabaseIndexRecord[]>(StringComparer.Ordinal),
            CanCompareRelationships: false);
    }

    private sealed class DatabaseInspectionConnection(
        SqliteConnection connection,
        string? temporaryDirectory) : IAsyncDisposable
    {
        public SqliteConnection Connection { get; } = connection;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (temporaryDirectory is not null && Directory.Exists(temporaryDirectory))
                {
                    Directory.Delete(temporaryDirectory, recursive: true);
                }
            }
        }
    }

    private sealed record DatabaseFileIdentity(long Length, DateTime LastWriteTimeUtc);

    private sealed record DatabaseFileSnapshot(
        DatabaseFileIdentity? Database,
        DatabaseFileIdentity? Wal);

    private sealed record DatabaseIndexRecord(
        string MessageId,
        long Sequence,
        long FileOffset,
        long RecordLength);

    private sealed record MessageArchiveFile(string SourcePath, string EntryName);
}
