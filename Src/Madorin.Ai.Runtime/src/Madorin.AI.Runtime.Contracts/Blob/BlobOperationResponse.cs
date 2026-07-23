namespace Madorin.AI.Runtime.Contracts;

public sealed record BlobOperationResponse(
    bool Success,
    string? Error,
    BlobReference? Reference,
    long Offset,
    bool EndOfBlob);
