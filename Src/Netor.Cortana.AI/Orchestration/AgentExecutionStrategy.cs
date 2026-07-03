namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 定义 Chat 编排阶段预期采用的执行策略。
/// 当前仅作为扩展点契约，具体调度由后续阶段实现。
/// </summary>
public enum AgentExecutionStrategy
{
    Sequential = 0,
    Concurrent = 1,
}
