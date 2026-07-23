using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Anthropic.Models;

/// <summary>
/// Official model catalog for the Anthropic Messages API.
/// Context windows sourced from https://docs.anthropic.com/en/docs/about-claude/models
/// </summary>
public static class AnthropicModelCatalog
{
    // ── Claude 4 family ───────────────────────────────────────────────────────
    public static readonly ProviderModel Claude4Opus = new(
        "claude-opus-4-5",
        "Claude Opus 4.5",
        ProviderModelId: "claude-opus-4-5-20260101",
        ContextWindow: 200_000,
        MaxOutputTokens: 32_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: true));

    public static readonly ProviderModel Claude4Sonnet = new(
        "claude-sonnet-4-6",
        "Claude Sonnet 4.6",
        ProviderModelId: "claude-sonnet-4-6-20260101",
        ContextWindow: 200_000,
        MaxOutputTokens: 16_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: true));

    public static readonly ProviderModel Claude4Haiku = new(
        "claude-haiku-4-5",
        "Claude Haiku 4.5",
        ProviderModelId: "claude-haiku-4-5-20251001",
        ContextWindow: 200_000,
        MaxOutputTokens: 8_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    // ── Claude 3 family (legacy) ──────────────────────────────────────────────
    public static readonly ProviderModel Claude3Opus = new(
        "claude-3-opus-20240229",
        "Claude 3 Opus",
        ContextWindow: 200_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false),
        IsDeprecated: false);

    public static readonly ProviderModel Claude35Sonnet = new(
        "claude-3-5-sonnet-20241022",
        "Claude 3.5 Sonnet",
        ContextWindow: 200_000,
        MaxOutputTokens: 8_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: true, Reasoning: false));

    public static readonly ProviderModel Claude35Haiku = new(
        "claude-3-5-haiku-20241022",
        "Claude 3.5 Haiku",
        ContextWindow: 200_000,
        MaxOutputTokens: 8_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true, ToolCalling: true,
            Vision: false, Reasoning: false));

    /// <summary>All models ordered by capability tier (highest first).</summary>
    public static readonly IReadOnlyList<ProviderModel> All =
    [
        Claude4Opus, Claude4Sonnet, Claude4Haiku,
        Claude35Sonnet, Claude35Haiku,
        Claude3Opus,
    ];

    /// <summary>Default model used when none is specified.</summary>
    public static readonly ProviderModel Default = Claude35Haiku;
}
