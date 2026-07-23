namespace Madorin.AI.Runtime.Contracts;

public sealed record BlobCompleteRequest(
    string UploadId,
    long Length,
    string Sha256);
