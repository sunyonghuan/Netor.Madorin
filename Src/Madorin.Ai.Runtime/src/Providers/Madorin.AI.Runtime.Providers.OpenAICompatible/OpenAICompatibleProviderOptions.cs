using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.OpenAICompatible;

/// <summary>Configures one OpenAI-compatible Provider profile.</summary>
public sealed record OpenAICompatibleProviderOptions(
    string ProviderId,
    string BaseUrl,
    string ApiKey,
    string AuthenticationHeader = "Authorization",
    string? AuthenticationScheme = "Bearer",
    ProviderCapabilities? Capabilities = null,
    IReadOnlyList<ProviderModel>? Models = null,
    string ConfigurationVersion = "1",
    string? ProbePath = "models",
    int MaximumProbeResponseBytes = 65_536);
