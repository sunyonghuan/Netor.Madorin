namespace Madorin.AI.Runtime.Providers.Abstractions;

public interface IRuntimeProviderAdapter
{
    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; }

    public ValueTask<IReadOnlyList<ProviderModel>> GetModelsAsync(
        CancellationToken cancellationToken = default);

    public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        CancellationToken cancellationToken = default);
}
