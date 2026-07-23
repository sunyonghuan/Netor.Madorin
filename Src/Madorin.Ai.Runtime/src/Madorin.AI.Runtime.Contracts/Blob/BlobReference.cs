namespace Madorin.AI.Runtime.Contracts;

public sealed record BlobReference(
    string BlobId,
    long Length,
    string Sha256,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt);
