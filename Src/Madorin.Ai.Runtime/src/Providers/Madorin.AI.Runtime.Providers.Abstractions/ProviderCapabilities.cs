namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record ProviderCapabilities(
    bool Streaming = false,
    bool ToolCalling = false,
    bool Vision = false,
    bool Audio = false,
    bool StructuredOutput = false,
    bool Reasoning = false,
    bool PromptCaching = false,
    bool Files = false,
    bool ComputerUse = false,
    bool Embeddings = false,
    bool Usage = false,
    bool RemoteCancellation = false);
