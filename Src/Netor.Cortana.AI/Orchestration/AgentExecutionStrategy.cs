namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 子任务执行策略：是顺序串行还是并发执行。
/// 阶段 2A 默认 Sequential；阶段 3A+ HandoffChat 始终 Sequential；
/// 阶段 3B+ Workflow 模式由 TaskExecutionEngine 决定，不走此枚举。
/// </summary>
public enum AgentExecutionStrategy
{
    /// <summary>顺序串行：一个子任务跑完再跑下一个。当前阶段默认值。</summary>
    Sequential = 0,

    /// <summary>并发执行：多个子任务并行；阶段 5+ 评估，注意 [05] §风险 9（并发文件冲突）。</summary>
    Concurrent = 1,
}
