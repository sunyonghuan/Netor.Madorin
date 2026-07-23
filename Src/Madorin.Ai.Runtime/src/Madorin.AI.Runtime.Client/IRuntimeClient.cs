using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Client;

public interface IRuntimeClient : IAsyncDisposable
{
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    public ValueTask<string> StartNewSessionRunAsync(
        NewSessionRunRequest request,
        CancellationToken cancellationToken = default);

    public ValueTask<string> StartExistingSessionRunAsync(
        ExistingSessionRunRequest request,
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        CancellationToken cancellationToken = default);

    public ValueTask AcknowledgeEventsAsync(
        long lastConfirmedGsn,
        CancellationToken cancellationToken = default);

    public ValueTask<bool> CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    public ValueTask<RunQueryResult> QueryRunAsync(
        string runId,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionGetResult> GetSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionResumeResult> ResumeSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionMessagesListResult> ListSessionMessagesAsync(
        SessionMessagesListParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<NextTurnSelection> UpdateSessionSelectionAsync(
        SessionSelectionUpdateParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask<SessionRehydrateResult> RehydrateSessionAsync(
        SessionRehydrateParameters parameters,
        CancellationToken cancellationToken = default);

    public ValueTask UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken cancellationToken = default);
}
