namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Persists Runtime events before they are sent to the host.</summary>
public interface IEventOutbox
{
    /// <summary>Atomically allocates a GSN and appends a pending event.</summary>
    public Task<long> AppendAsync(
        string runId,
        long runSequence,
        string messageType,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>Appends a Delta only when replay capacity remains; null means it was dropped before GSN allocation.</summary>
    public Task<long?> AppendDeltaAsync(
        string runId,
        long runSequence,
        string messageType,
        string payloadJson,
        CancellationToken ct = default);

    /// <summary>Marks a pending event as sent.</summary>
    public Task MarkSentAsync(long gsn, CancellationToken ct = default);

    /// <summary>Acknowledges all persisted events through the supplied GSN.</summary>
    public Task AcknowledgeAsync(long lastConfirmedGsn, CancellationToken ct = default);

    /// <summary>Loads pending and sent events that have not been acknowledged.</summary>
    public Task<IReadOnlyList<OutboxEntry>> LoadPendingAsync(CancellationToken ct = default);

    /// <summary>Gets the next sequence for an out-of-band event in one Run.</summary>
    public Task<long> GetNextRunSequenceAsync(
        string runId,
        CancellationToken ct = default);

    /// <summary>Gets the highest acknowledged GSN.</summary>
    public Task<long> GetAcknowledgedGsnAsync(CancellationToken ct = default);
}

/// <summary>Represents a persisted event waiting for acknowledgement.</summary>
public sealed record OutboxEntry(
    long Gsn,
    string RunId,
    long RunSequence,
    string MessageType,
    string PayloadJson);
