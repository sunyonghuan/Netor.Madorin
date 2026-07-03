namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// Chat 模式编排默认阈值。
/// 当前阶段先固化默认值，后续再接入 SystemSettingsService。
/// </summary>
public sealed class AgentOrchestratorOptions
{
    public int MaxRounds { get; init; } = 5;

    public int MaxSubTasks { get; init; } = 6;

    public TimeSpan PerStepTimeout { get; init; } = TimeSpan.FromMinutes(2);

    public int HandoffMaxChainLength { get; init; } = 3;
}
