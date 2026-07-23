using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public interface IToolResultBlobStore
{
    public Task<BlobReference> StoreAsync(
        string runId,
        ReadOnlyMemory<byte> content,
        string contentType,
        string accessScope,
        DateTimeOffset expiresAt,
        CancellationToken ct = default);

    public Task ValidateReferenceAsync(
        BlobReference reference,
        CancellationToken ct = default);
}
