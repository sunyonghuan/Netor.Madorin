using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public interface IModeOrchestrator
{
    public RuntimeMode Mode { get; }

    public IAsyncEnumerable<RuntimeEventEnvelope> RunAsync(
        string runId,
        string sessionId,
        NewSessionRunRequest request,
        string runtimeInstanceId,
        CancellationToken ct = default);
}
