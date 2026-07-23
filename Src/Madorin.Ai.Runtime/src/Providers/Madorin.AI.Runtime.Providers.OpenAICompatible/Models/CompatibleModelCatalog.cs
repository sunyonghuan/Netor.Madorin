using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.OpenAICompatible.Models;

/// <summary>
/// Model catalog for OpenAI-compatible endpoints (e.g. Azure OpenAI, DeepSeek, Qwen, local Ollama).
/// Unlike OpenAI and Anthropic, the model list is user-configured because
/// compatible endpoints vary by deployment.
/// </summary>
public static class CompatibleModelCatalog
{
    // ── Well-known compatible providers ──────────────────────────────────────

    /// <summary>DeepSeek R1 — strong reasoning, OpenAI-compatible API.</summary>
    public static readonly ProviderModel DeepSeekR1 = new(
        "deepseek-reasoner",
        "DeepSeek R1",
        ContextWindow: 64_000,
        MaxOutputTokens: 8_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: false, Reasoning: true));

    /// <summary>DeepSeek V3 — fast, cost-efficient chat model.</summary>
    public static readonly ProviderModel DeepSeekV3 = new(
        "deepseek-chat",
        "DeepSeek V3",
        ContextWindow: 64_000,
        MaxOutputTokens: 8_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true, Reasoning: false));

    /// <summary>Qwen-Max — Alibaba Cloud flagship model.</summary>
    public static readonly ProviderModel QwenMax = new(
        "qwen-max",
        "Qwen Max",
        ContextWindow: 32_000,
        MaxOutputTokens: 8_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true, Vision: true));

    /// <summary>GLM-4 — Zhipu AI's main model (compatible with OpenAI Chat API).</summary>
    public static readonly ProviderModel Glm4 = new(
        "glm-4",
        "GLM-4",
        ContextWindow: 128_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true, Vision: true));

    // ── Local / Ollama ────────────────────────────────────────────────────────

    /// <summary>
    /// Placeholder for any locally-run Ollama model.
    /// The actual model ID is set in config.json.
    /// </summary>
    public static readonly ProviderModel OllamaLocal = new(
        "ollama/local",
        "Ollama (local)",
        ContextWindow: null,     // depends on the specific model loaded
        MaxOutputTokens: null,
        Capabilities: new ProviderCapabilities(Streaming: true));

    /// <summary>
    /// Creates a user-defined model entry for an arbitrary compatible endpoint.
    /// </summary>
    public static ProviderModel Custom(
        string modelId,
        string displayName,
        int? contextWindow = null,
        int? maxOutputTokens = null) =>
        new(modelId, displayName, ContextWindow: contextWindow, MaxOutputTokens: maxOutputTokens,
            Capabilities: new ProviderCapabilities(Streaming: true, ToolCalling: true));

    /// <summary>Well-known compatible models as a discoverable list.</summary>
    public static readonly IReadOnlyList<ProviderModel> WellKnown =
    [
        DeepSeekR1, DeepSeekV3,
        QwenMax, Glm4,
        OllamaLocal,
    ];
}
