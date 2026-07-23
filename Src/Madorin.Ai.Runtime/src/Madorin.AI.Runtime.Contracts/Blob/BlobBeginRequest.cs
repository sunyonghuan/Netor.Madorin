namespace Madorin.AI.Runtime.Contracts;

public sealed record BlobBeginRequest(
    string UploadId,
    string RunId,
    string ContentType,
    string AccessScope,
    DateTimeOffset ExpiresAt,
    long? Length = null);
