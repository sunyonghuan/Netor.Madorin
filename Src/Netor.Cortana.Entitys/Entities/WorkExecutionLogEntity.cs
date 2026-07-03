namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作模式任务的执行日志类型枚举（持久化为 TEXT 列）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §3.2。
/// </summary>
public static class WorkExecutionLogTypes
{
    public const string StepStart = "StepStart";
    public const string StepComplete = "StepComplete";
    public const string ToolCall = "ToolCall";
    public const string ToolResult = "ToolResult";
    public const string Thinking = "Thinking";
    public const string Acceptance = "Acceptance";
    public const string Error = "Error";
    public const string UserInterrupt = "UserInterrupt";

    // 16 文档：长任务可靠性
    public const string SelfEvaluation = "SelfEvaluation";
    public const string RetryAttempt = "RetryAttempt";
    public const string DeadlockDetected = "DeadlockDetected";

    // 11 文档：并行 / 嵌套
    public const string ParallelBlockStart = "ParallelBlockStart";
    public const string ParallelBlockEnd = "ParallelBlockEnd";
    public const string NestedWorkflowStart = "NestedWorkflowStart";
    public const string NestedWorkflowEnd = "NestedWorkflowEnd";

    // 子智能体背景执行
    public const string SubAgentDelta = "SubAgentDelta";
    public const string SubAgentJobStart = "SubAgentJobStart";
    public const string SubAgentJobEnd = "SubAgentJobEnd";
}

/// <summary>
/// 工作模式任务执行日志（WorkExecutionLogs 表）。
/// 每条记录代表一次工具调用 / 验收 / 错误 / 自评等执行明细。
/// </summary>
public sealed class WorkExecutionLogEntity
{
    /// <summary>自增主键。</summary>
    public long Id { get; set; }

    /// <summary>关联的 WorkTasks.Id。</summary>
    public string TaskId { get; set; } = string.Empty;

    /// <summary>同任务内单调递增。</summary>
    public long Sequence { get; set; }

    /// <summary>日志类型，见 <see cref="WorkExecutionLogTypes"/>。</summary>
    public string LogType { get; set; } = string.Empty;

    /// <summary>类型相关 JSON 内容。</summary>
    public string Content { get; set; } = string.Empty;

    /// <summary>创建时间（Unix 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
