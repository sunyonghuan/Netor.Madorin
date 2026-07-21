using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Orchestration.Abstractions;

public interface IAgentInvocationExecutor
{
    public IAsyncEnumerable<RuntimeProviderEvent> ExecuteAsync(
        AgentInvocationRequest request,
        CancellationToken cancellationToken = default);
}
