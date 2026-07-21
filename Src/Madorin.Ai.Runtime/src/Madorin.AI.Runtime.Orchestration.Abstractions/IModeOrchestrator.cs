using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public interface IModeOrchestrator
{
    public RuntimeMode Mode { get; }

    public IAsyncEnumerable<RuntimeProviderEvent> ExecuteAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken = default);
}
