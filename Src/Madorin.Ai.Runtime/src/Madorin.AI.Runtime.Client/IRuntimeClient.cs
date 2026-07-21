using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Client;

public interface IRuntimeClient : IAsyncDisposable
{
    public ValueTask ConnectAsync(CancellationToken cancellationToken = default);

    public IAsyncEnumerable<RuntimeEventEnvelope> ReadEventsAsync(
        long eventReplayCursor,
        CancellationToken cancellationToken = default);

    public ValueTask CancelRunAsync(
        string runId,
        CancellationToken cancellationToken = default);
}
