namespace Madorin.AI.Runtime.Persistence.Abstractions;

public sealed record ToolResultBlobState(
    string BlobId,
    long Length,
    string Sha256,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt);
