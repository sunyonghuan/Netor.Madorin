namespace Madorin.AI.Runtime.Persistence.Files;

/// <summary>Controls bounded JSONL writes, record size, and durability fault injection.</summary>
public sealed record ConversationStoreOptions
{
    public int MaxPendingWritesPerSession { get; init; } = 128;

    public int MaxJsonLineBytes { get; init; } = 16 * 1024 * 1024;

    public Func<ConversationStoreFailurePoint, CancellationToken, ValueTask>? FailureInjector { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(MaxPendingWritesPerSession);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxJsonLineBytes, 1024);
    }
}
