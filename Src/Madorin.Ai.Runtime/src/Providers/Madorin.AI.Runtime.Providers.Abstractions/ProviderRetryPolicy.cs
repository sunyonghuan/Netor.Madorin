namespace Madorin.AI.Runtime.Providers.Abstractions;

public static class ProviderRetryPolicy
{
    public static bool CanRetry(RuntimeProviderRequest request, bool hasObservedOutput)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request.IsIdempotent
            && !request.HasIrreversibleToolSideEffects
            && !hasObservedOutput;
    }
}
