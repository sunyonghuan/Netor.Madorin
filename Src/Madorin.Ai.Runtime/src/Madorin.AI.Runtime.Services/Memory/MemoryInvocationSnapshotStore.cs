using System.Collections.Concurrent;

namespace Madorin.AI.Runtime.Services.Memory;

public sealed class MemoryInvocationSnapshotStore
{
    private readonly ConcurrentDictionary<string, MemoryContextSnapshot> _snapshots =
        new(StringComparer.Ordinal);

    public void Capture(string invocationId, AgentContextSnapshot context)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        ArgumentNullException.ThrowIfNull(context);
        if (!_snapshots.TryAdd(invocationId, context.Memory))
        {
            throw new InvalidOperationException(
                $"Memory was already captured for Invocation '{invocationId}'.");
        }
    }

    public bool TryGet(string invocationId, out MemoryContextSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        return _snapshots.TryGetValue(invocationId, out snapshot!);
    }

    public bool Release(string invocationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(invocationId);
        return _snapshots.TryRemove(invocationId, out _);
    }
}
