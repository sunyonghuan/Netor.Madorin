using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Persistence.Abstractions;

/// <summary>Persists Session and Run lifecycle metadata.</summary>
public interface ISessionRepository
{
    /// <summary>Creates a Session or returns the Session bound to the idempotency key.</summary>
    public Task<string> CreateSessionAsync(
        RuntimeMode mode,
        string idempotencyKey,
        TimeSpan keyRetention,
        CancellationToken ct = default);

    /// <summary>Creates a Run or returns the Run bound to the idempotency key.</summary>
    public Task<string> CreateRunAsync(
        string sessionId,
        string runIdempotencyKey,
        TimeSpan keyRetention,
        CancellationToken ct = default);

    /// <summary>Gets a Run's current persisted status.</summary>
    public Task<RunStatus> GetRunStatusAsync(string runId, CancellationToken ct = default);

    /// <summary>Conditionally applies a valid Run status transition.</summary>
    public Task TransitionRunStatusAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus targetStatus,
        CancellationToken ct = default);

    /// <summary>Atomically writes a terminal state and its final text snapshot.</summary>
    public Task TransitionRunToTerminalAsync(
        string runId,
        RunStatus expectedStatus,
        RunStatus terminalStatus,
        string? terminalText,
        CancellationToken ct = default);

    /// <summary>Gets the persisted status and terminal text for a Run.</summary>
    public Task<RunSnapshot?> GetRunSnapshotAsync(
        string runId,
        CancellationToken ct = default);

    /// <summary>Marks all non-terminal Runs as interrupted during startup recovery.</summary>
    public Task MarkInterruptedAsync(CancellationToken ct = default);

    /// <summary>Lists Sessions using an updated-at and Session ID keyset cursor.</summary>
    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(
        string? cursor,
        int pageSize,
        CancellationToken ct = default);

    /// <summary>Gets Session metadata, its latest Run, Selection snapshot, and recovery watermarks.</summary>
    public Task<PersistedSessionSnapshot?> GetSessionSnapshotAsync(
        string sessionId,
        CancellationToken ct = default);

    /// <summary>Conditionally saves a prompt-free Selection and its Agent hash snapshots.</summary>
    public Task<bool> TrySaveSessionSelectionAsync(
        string sessionId,
        int? expectedSelectionVersion,
        int selectionVersion,
        string selectionJson,
        IReadOnlyList<AgentSnapshot> agentSnapshots,
        CancellationToken ct = default);

    /// <summary>Lists sessions using a structured query with optional filters.</summary>
    public Task<IReadOnlyList<SessionDescriptor>> ListSessionsAsync(
        SessionListQuery query,
        CancellationToken ct = default);

    /// <summary>Sets a Session's title only when the current title is null or empty.</summary>
    public Task<bool> TrySetInitialSessionTitleAsync(
        string sessionId,
        string title,
        CancellationToken ct = default);

    /// <summary>Sets a Session status and returns false when the Session does not exist.</summary>
    public Task<bool> TrySetSessionStatusAsync(
        string sessionId,
        SessionStatus status,
        CancellationToken ct = default);

    /// <summary>Deletes a Session and all Runtime metadata owned by it in one transaction.</summary>
    public Task<bool> TryDeleteSessionAsync(
        string sessionId,
        CancellationToken ct = default);

    /// <summary>Gets tool-result Blob ids owned by one Session and protected by all other owners.</summary>
    public Task<SessionBlobReferences> GetToolResultBlobReferencesAsync(
        string sessionId,
        CancellationToken ct = default);

    /// <summary>Lists lightweight message metadata by Session sequence cursor.</summary>
    public Task<IReadOnlyList<MessageIndexEntry>> ListMessageIndexAsync(
        string sessionId,
        long? cursor,
        int pageSize,
        CancellationToken ct = default);
}
