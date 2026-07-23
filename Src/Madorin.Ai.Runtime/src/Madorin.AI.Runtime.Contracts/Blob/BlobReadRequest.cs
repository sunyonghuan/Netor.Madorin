namespace Madorin.AI.Runtime.Contracts;

public sealed record BlobReadRequest(
    string BlobId,
    long Offset,
    int MaxBytes);
