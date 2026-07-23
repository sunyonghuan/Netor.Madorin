using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public interface IRuntimeProviderAdapter
{
    public string ProviderId { get; }

    public ProviderCapabilities Capabilities { get; }

    public IReadOnlyList<ProviderModel> Models { get; }

    public ProviderConfigurationSchema ConfigurationSchema =>
        ProviderConfigurationSchema.ForProvider(ProviderId);

    public ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ProviderTokenEstimator.Estimate(request));
    }

    public ValueTask<ProviderCapabilityProbeResult> ProbeCapabilitiesAsync(
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            ProviderCapabilityProbe.FromDeclared(Capabilities, ConfigurationSchema.Version));
    }

    public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
        RuntimeProviderRequest request,
        CancellationToken ct = default);

    public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default);
}

/// <summary>Optional capability for adapters that can replace credentials for a pending request.</summary>
public interface IRuntimeProviderCredentialUpdater
{
    public Task UpdateCredentialsAsync(
        CredentialsUpdateParameters parameters,
        CancellationToken ct = default);
}
