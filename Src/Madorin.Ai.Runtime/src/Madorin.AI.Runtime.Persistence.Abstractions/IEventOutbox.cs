using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Abstractions;

public interface IEventOutbox
{
    public ValueTask AppendAsync(
        RuntimeEventRecord runtimeEvent,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<RuntimeEventRecord> ReadAfterAsync(
        long globalSequence,
        CancellationToken cancellationToken = default);

    public ValueTask AcknowledgeAsync(
        long lastConfirmedGlobalSequence,
        CancellationToken cancellationToken = default);
}
