using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.OpenAI.Models;

/// <summary>
/// Official model catalog for the OpenAI Responses API.
/// Context windows and capabilities sourced from https://platform.openai.com/docs/models
/// </summary>
public static class OpenAIModelCatalog
{
    // ── GPT-5 family ─────────────────────────────────────────────────────────
    public static readonly ProviderModel Gpt5 = new(
        "gpt-5",
        "GPT-5",
        ContextWindow: 128_000,
        MaxOutputTokens: 16_384,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    public static readonly ProviderModel Gpt5Mini = new(
        "gpt-5-mini",
        "GPT-5 mini",
        ContextWindow: 128_000,
        MaxOutputTokens: 16_384,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    // ── o-series (reasoning) ─────────────────────────────────────────────────
    public static readonly ProviderModel O3 = new(
        "o3",
        "o3",
        ContextWindow: 200_000,
        MaxOutputTokens: 100_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: true));

    public static readonly ProviderModel O3Mini = new(
        "o3-mini",
        "o3-mini",
        ContextWindow: 200_000,
        MaxOutputTokens: 100_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: false, Reasoning: true));

    public static readonly ProviderModel O4Mini = new(
        "o4-mini",
        "o4-mini",
        ContextWindow: 200_000,
        MaxOutputTokens: 100_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: true));

    // ── GPT-4 family ─────────────────────────────────────────────────────────
    public static readonly ProviderModel Gpt4O = new(
        "gpt-4o",
        "GPT-4o",
        ContextWindow: 128_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    public static readonly ProviderModel Gpt4OMini = new(
        "gpt-4o-mini",
        "GPT-4o mini",
        ContextWindow: 128_000,
        MaxOutputTokens: 16_384,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    /// <summary>All models ordered by capability tier (highest first).</summary>
    public static readonly IReadOnlyList<ProviderModel> All =
    [
        O3, O4Mini, O3Mini,
        Gpt5, Gpt5Mini,
        Gpt4O, Gpt4OMini,
    ];

    /// <summary>Default model used when none is specified.</summary>
    public static readonly ProviderModel Default = Gpt4OMini;
}
