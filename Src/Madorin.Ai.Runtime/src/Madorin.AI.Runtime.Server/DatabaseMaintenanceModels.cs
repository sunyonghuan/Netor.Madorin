namespace Madorin.AI.Runtime.Server;

/// <summary>Describes one issue found by a read-only Runtime database check.</summary>
public sealed record DatabaseCheckIssue(
    string Code,
    string Severity,
    string Message,
    bool Repairable,
    string? SessionId = null);

/// <summary>Contains the result of a read-only Runtime database check.</summary>
public sealed record DatabaseCheckResult(
    string DataDirectory,
    int? SchemaVersion,
    int SupportedSchemaVersion,
    string? JournalMode,
    DatabaseCheckIssue[] Issues)
{
    /// <summary>Gets whether no integrity issues were found.</summary>
    public bool Healthy => Issues.Length == 0;

    /// <summary>Gets the number of issues that the repair command recognizes.</summary>
    public int RepairableIssueCount => Issues.Count(static issue => issue.Repairable);

    /// <summary>Gets whether every reported issue can be repaired by the repair command.</summary>
    public bool CanRepair => Issues.Length > 0 && Issues.All(static issue => issue.Repairable);
}

/// <summary>Contains the result of one confirmed Runtime database repair.</summary>
public sealed record DatabaseRepairResult(
    string DataDirectory,
    string? BackupPath,
    string[] RepairedSessionIds,
    DatabaseCheckResult Before,
    DatabaseCheckResult After);

/// <summary>Describes the read-only result of scanning canonical messages for a rebuild.</summary>
public sealed record DatabaseRebuildPlan(
    string DataDirectory,
    int SessionsScanned,
    int SessionsRecoverable,
    int MessagesRecoverable,
    string[] RecoverableItems,
    string[] UnrecoverableDiagnostics)
{
    /// <summary>Gets the number of canonical Session files that cannot be rebuilt.</summary>
    public int SessionsUnrecoverable => SessionsScanned - SessionsRecoverable;
}

/// <summary>Contains the result of rebuilding a fresh database from canonical messages.</summary>
public sealed record DatabaseRebuildResult(
    string DataDirectory,
    int SessionsScanned,
    int SessionsRebuilt,
    int MessagesUpserted,
    string BackupPath,
    string[] RecoverableItems,
    string[] UnrecoverableDiagnostics)
{
    /// <summary>Gets whether every scanned Session was rebuilt.</summary>
    public bool Complete => SessionsScanned == SessionsRebuilt;
}

/// <summary>Contains the size change produced by compacting the Runtime database.</summary>
public sealed record DatabaseVacuumResult(
    string DataDirectory,
    string DatabasePath,
    long BytesBefore,
    long BytesAfter)
{
    /// <summary>Gets the number of bytes removed from the database file.</summary>
    public long BytesReclaimed => Math.Max(0, BytesBefore - BytesAfter);
}

/// <summary>Describes a read-only forward database migration plan.</summary>
public sealed record DatabaseMigrationPlan(
    string DataDirectory,
    string DatabasePath,
    int FromVersion,
    int TargetVersion,
    int[] PendingVersions)
{
    /// <summary>Gets whether the plan contains at least one migration.</summary>
    public bool RequiresMigration => PendingVersions.Length > 0;
}

/// <summary>Contains the result of a forward database migration.</summary>
public sealed record DatabaseMigrationResult(
    string DataDirectory,
    string DatabasePath,
    int FromVersion,
    int TargetVersion,
    int[] AppliedVersions,
    string? BackupPath);

/// <summary>Contains the paths and message count produced by an online database backup.</summary>
public sealed record DatabaseBackupResult(
    string DataDirectory,
    string DatabasePath,
    string? MessagesArchivePath,
    int MessageFileCount);

/// <summary>Reports an online backup failure after incomplete artifacts were cleaned up.</summary>
public sealed class DatabaseBackupException : Exception
{
    /// <summary>Initializes a database backup exception.</summary>
    public DatabaseBackupException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports a rebuild failure and the retained recovery point, when available.</summary>
public sealed class DatabaseRebuildException : Exception
{
    /// <summary>Initializes a rebuild exception and records its recovery point.</summary>
    public DatabaseRebuildException(
        string message,
        string? backupPath,
        Exception innerException)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    /// <summary>Gets the retained recovery point created before database replacement.</summary>
    public string? BackupPath { get; }
}

/// <summary>Reports a database compaction failure.</summary>
public sealed class DatabaseVacuumException : Exception
{
    /// <summary>Initializes a database compaction exception.</summary>
    public DatabaseVacuumException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports a migration failure and the retained recovery point, when available.</summary>
public sealed class DatabaseMigrationException : Exception
{
    /// <summary>Initializes a migration exception and records its recovery point.</summary>
    public DatabaseMigrationException(
        string message,
        string? backupPath,
        Exception innerException)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    /// <summary>Gets the retained recovery point created before migration.</summary>
    public string? BackupPath { get; }
}

/// <summary>Reports failure to acquire the workspace lease required by a repair.</summary>
public sealed class DatabaseMaintenanceLockException : Exception
{
    /// <summary>Initializes a workspace maintenance lock exception.</summary>
    public DatabaseMaintenanceLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reports a repair failure after a recovery point may have been created.</summary>
public sealed class DatabaseRepairException : Exception
{
    /// <summary>Initializes a repair exception and records its recovery point.</summary>
    public DatabaseRepairException(
        string message,
        string? backupPath,
        Exception innerException)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }

    /// <summary>Gets the retained recovery point, when backup creation completed.</summary>
    public string? BackupPath { get; }
}
