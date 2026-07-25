namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Separates one Session's tool-result Blob references from references owned elsewhere.</summary>
public sealed record SessionBlobReferences(
    string[] TargetBlobIds,
    string[] ProtectedBlobIds);
