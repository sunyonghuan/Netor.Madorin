using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Transport.Abstractions;

public interface IEventChannel : IAsyncDisposable
{
    public IAsyncEnumerable<RuntimeEventEnvelope> ReadAllAsync(
        long eventReplayCursor,
        CancellationToken cancellationToken = default);

    public ValueTask AcknowledgeAsync(
        long lastConfirmedGlobalSequence,
        CancellationToken cancellationToken = default);
}
