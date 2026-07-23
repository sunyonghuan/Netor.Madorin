using Madorin.AI.Runtime.Providers.Abstractions;

namespace Madorin.AI.Runtime.Providers.DeepSeek.Models;

/// <summary>
/// DeepSeek 官方模型目录。
/// 模型信息参考：https://platform.deepseek.com/api-docs/pricing
/// </summary>
public static class DeepSeekModelCatalog
{
    // ── DeepSeek-R 系列（推理模型）────────────────────────────────────────────
    public static readonly ProviderModel DeepSeekR1 = new(
        "deepseek-reasoner",
        "DeepSeek-R1",
        ContextWindow: 64_000,
        MaxOutputTokens: 8_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: false,
            Reasoning: true));

    // ── DeepSeek-V 系列（对话模型）────────────────────────────────────────────
    public static readonly ProviderModel DeepSeekV3 = new(
        "deepseek-chat",
        "DeepSeek-V3",
        ContextWindow: 64_000,
        MaxOutputTokens: 8_000,
        Capabilities: new ProviderCapabilities(
            Streaming: true,
            ToolCalling: true,
            Reasoning: false));

    /// <summary>所有可用模型，按能力档位降序排列。</summary>
    public static readonly IReadOnlyList<ProviderModel> All =
    [
        DeepSeekR1,
        DeepSeekV3,
    ];

    /// <summary>缺省模型（性价比最高的对话模型）。</summary>
    public static readonly ProviderModel Default = DeepSeekV3;
}
