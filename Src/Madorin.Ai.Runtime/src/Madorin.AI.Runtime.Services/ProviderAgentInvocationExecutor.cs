using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Orchestration.Abstractions;
using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Services;

/// <summary>
/// Default <see cref="IAgentInvocationExecutor"/> that delegates streaming to a resolved <see cref="IRuntimeProviderAdapter"/>.
/// </summary>
public sealed class ProviderAgentInvocationExecutor : IAgentInvocationExecutor
{
    private readonly Func<string, IRuntimeProviderAdapter> _providerFactory;

    public ProviderAgentInvocationExecutor(Func<string, IRuntimeProviderAdapter> providerFactory)
    {
        _providerFactory = providerFactory ?? throw new ArgumentNullException(nameof(providerFactory));
    }

    public async IAsyncEnumerable<RuntimeProviderEvent> ExecuteAsync(
        AgentInvocationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var adapter = _providerFactory(request.ProviderId)
            ?? throw new InvalidOperationException($"Provider adapter not registered for Provider ID '{request.ProviderId}'.");

        var providerRequest = new RuntimeProviderRequest(
            request.InvocationId,
            request.AgentId,
            request.ProviderId,
            request.ModelId,
            request.Messages,
            request.AvailableTools,
            Temperature: null,
            MaxTokens: null,
            StructuredOutput: request.StructuredOutput,
            InternalRequestId: null,
            AttemptNumber: 0,
            IsIdempotent: request.IsIdempotent,
            HasIrreversibleToolSideEffects: request.HasIrreversibleToolSideEffects,
            CancellationToken: ct);

        await foreach (var evt in adapter.CompleteStreamingAsync(providerRequest, ct).ConfigureAwait(false))
        {
            yield return evt;
        }
    }
}
