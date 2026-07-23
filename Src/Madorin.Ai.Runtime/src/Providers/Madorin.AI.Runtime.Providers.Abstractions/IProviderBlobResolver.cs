using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

/// <summary>Resolves an authorized Runtime Blob into bytes accepted by a Provider SDK.</summary>
public interface IProviderBlobResolver
{
    public ValueTask<ResolvedProviderBlob> ResolveAsync(
        BlobReference blob,
        CancellationToken ct = default);
}

public sealed record ResolvedProviderBlob(
    ReadOnlyMemory<byte> Data,
    string ContentType,
    string? FileName = null);
