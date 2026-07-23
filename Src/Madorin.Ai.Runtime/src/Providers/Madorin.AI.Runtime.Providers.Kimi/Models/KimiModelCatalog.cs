using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.Kimi.Models;

/// <summary>
/// Kimi（Moonshot AI）官方模型目录。
/// 模型信息参考：https://platform.moonshot.cn/docs/pricing/chat
/// </summary>
public static class KimiModelCatalog
{
    // ── moonshot-v1 系列（按上下文长度分档）──────────────────────────────────
    public static readonly ProviderModel Moonshot8K = new(
        "moonshot-v1-8k",
        "Kimi moonshot-v1-8k",
        ContextWindow: 8_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true));

    public static readonly ProviderModel Moonshot32K = new(
        "moonshot-v1-32k",
        "Kimi moonshot-v1-32k",
        ContextWindow: 32_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true));

    public static readonly ProviderModel Moonshot128K = new(
        "moonshot-v1-128k",
        "Kimi moonshot-v1-128k",
        ContextWindow: 128_000,
        MaxOutputTokens: 4_096,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true));

    // ── kimi-latest（最新旗舰，含 thinking 能力）──────────────────────────────
    public static readonly ProviderModel KimiLatest = new(
        "kimi-latest",
        "Kimi Latest",
        ContextWindow: 128_000,
        MaxOutputTokens: 8_192,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true,
            Reasoning: true));

    /// <summary>所有可用模型，按上下文容量降序排列。</summary>
    public static readonly IReadOnlyList<ProviderModel> All =
    [
        KimiLatest,
        Moonshot128K,
        Moonshot32K,
        Moonshot8K,
    ];

    /// <summary>缺省模型（长上下文对话）。</summary>
    public static readonly ProviderModel Default = Moonshot32K;
}
