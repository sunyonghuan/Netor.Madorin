namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record ProviderCapabilities(
    bool SupportsStreaming,
    bool SupportsTools,
    bool SupportsReasoning);
