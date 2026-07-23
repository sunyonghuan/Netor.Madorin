namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record ProviderModel(
    string Id,
    string DisplayName,
    string? ProviderModelId = null,
    int? ContextWindow = null,
    int? MaxOutputTokens = null,
    ProviderCapabilities? Capabilities = null,
    bool IsDeprecated = false);
