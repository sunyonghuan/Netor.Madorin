namespace Madorin.AI.Runtime.Transport.Abstractions;

public interface IControlChannel : IAsyncDisposable
{
    public ValueTask<ReadOnlyMemory<byte>> SendAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default);
}
