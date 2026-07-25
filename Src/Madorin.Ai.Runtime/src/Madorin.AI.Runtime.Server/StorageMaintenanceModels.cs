namespace Madorin.AI.Runtime.Server;

/// <summary>Describes one Blob storage integrity problem.</summary>
public sealed record StorageCheckIssue(
    string Code,
    string Severity,
    string Message,
    string? BlobId = null);

/// <summary>Summarizes a read-only Blob storage inspection.</summary>
public sealed record StorageCheckResult(
    string DataDirectory,
    string BlobDirectory,
    bool VerifyHashes,
    bool ReferenceScanComplete,
    int ReferenceCount,
    int BlobFileCount,
    int OrphanCount,
    StorageCheckIssue[] Issues)
{
    public bool Healthy => Issues.Length == 0;
}

/// <summary>Describes the safe candidates for one Blob garbage-collection pass.</summary>
public sealed record StorageGcPlan(
    string DataDirectory,
    string BlobDirectory,
    int OlderThanDays,
    DateTimeOffset CutoffUtc,
    bool ReferenceScanComplete,
    int ReferenceCount,
    int BlobFileCount,
    int ProtectedByAgeCount,
    long CandidateBytes,
    string[] CandidateBlobIds,
    StorageCheckIssue[] BlockingIssues)
{
    public bool CanCollect => ReferenceScanComplete && BlockingIssues.Length == 0;
}

/// <summary>Reports Blob files moved out of the active store into a recovery point.</summary>
public sealed record StorageGcResult(
    string DataDirectory,
    string BlobDirectory,
    int OlderThanDays,
    DateTimeOffset CutoffUtc,
    int ProtectedByAgeCount,
    long CollectedBytes,
    string[] CollectedBlobIds,
    string? RecoveryPointPath);

/// <summary>Indicates that Blob GC could not acquire the workspace write lease.</summary>
public sealed class StorageMaintenanceLockException : InvalidOperationException
{
    public StorageMaintenanceLockException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Indicates that Blob GC cannot prove which files are unreferenced.</summary>
public sealed class StorageGcBlockedException : InvalidOperationException
{
    public StorageGcBlockedException(StorageGcPlan plan)
        : base("Blob garbage collection is blocked because the reference scan is incomplete.")
    {
        Plan = plan;
    }

    public StorageGcPlan Plan { get; }
}

/// <summary>Indicates that Blob GC failed after planning or recovery-point creation.</summary>
public sealed class StorageGcException : IOException
{
    public StorageGcException(
        string message,
        string? recoveryPointPath,
        IReadOnlyList<string> collectedBlobIds,
        Exception innerException)
        : base(message, innerException)
    {
        RecoveryPointPath = recoveryPointPath;
        CollectedBlobIds = [.. collectedBlobIds];
    }

    public string? RecoveryPointPath { get; }

    public string[] CollectedBlobIds { get; }
}
