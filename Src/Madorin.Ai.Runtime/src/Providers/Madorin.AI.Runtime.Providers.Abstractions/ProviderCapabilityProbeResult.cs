using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Providers.Abstractions;

public sealed record ProviderCapabilityProbeResult(
    ProviderCapabilities Capabilities,
    DateTimeOffset ProbedAt,
    string ConfigurationVersion,
    string Source,
    string[] MissingCapabilities,
    ProviderExtensionData[]? Extensions = null);

public static class ProviderCapabilityProbe
{
    public static ProviderCapabilityProbeResult FromDeclared(
        ProviderCapabilities capabilities,
        string configurationVersion,
        DateTimeOffset? probedAt = null) =>
        new(
            capabilities,
            probedAt ?? DateTimeOffset.UtcNow,
            configurationVersion,
            "declared",
            GetMissingCapabilities(capabilities));

    public static string[] GetMissingCapabilities(ProviderCapabilities capabilities)
    {
        var missing = new List<string>();
        AddIfMissing(missing, "streaming", capabilities.Streaming);
        AddIfMissing(missing, "tool_calling", capabilities.ToolCalling);
        AddIfMissing(missing, "vision", capabilities.Vision);
        AddIfMissing(missing, "audio", capabilities.Audio);
        AddIfMissing(missing, "structured_output", capabilities.StructuredOutput);
        AddIfMissing(missing, "reasoning", capabilities.Reasoning);
        AddIfMissing(missing, "prompt_caching", capabilities.PromptCaching);
        AddIfMissing(missing, "files", capabilities.Files);
        AddIfMissing(missing, "computer_use", capabilities.ComputerUse);
        AddIfMissing(missing, "embeddings", capabilities.Embeddings);
        AddIfMissing(missing, "usage", capabilities.Usage);
        AddIfMissing(missing, "remote_cancellation", capabilities.RemoteCancellation);
        return [.. missing];
    }

    private static void AddIfMissing(List<string> missing, string name, bool isSupported)
    {
        if (!isSupported)
        {
            missing.Add(name);
        }
    }
}
