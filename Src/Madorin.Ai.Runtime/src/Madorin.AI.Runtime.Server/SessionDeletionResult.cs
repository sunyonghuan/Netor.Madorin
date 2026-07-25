namespace Madorin.AI.Runtime.Server;

/// <summary>Reports a completed local Session deletion.</summary>
public sealed record SessionDeletionResult(
    string SessionId,
    bool IncludeBlobs,
    int DeletedBlobCount,
    string RecoveryPoint);
