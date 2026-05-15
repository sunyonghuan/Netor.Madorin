namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// Workflow 任务最终结果。<see cref="IWorkflowExecutor.StartTaskAsync"/> 可同步返回 TaskId，
/// 客户端通过 <see cref="IWorkflowExecutor.GetTaskDetailAsync"/> 查询此结果。
/// </summary>
public sealed record WorkflowTaskResult
{
    /// <summary>任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>最终状态。</summary>
    public required WorkflowTaskStatus Status { get; init; }

    /// <summary>Markdown 最终报告。可空（任务未完成或失败时）。</summary>
    public string? FinalReport { get; init; }

    /// <summary>错误摘要（任务失败时）。</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>累计输入 token。</summary>
    public long TotalTokenInputCount { get; init; }

    /// <summary>累计输出 token。</summary>
    public long TotalTokenOutputCount { get; init; }
}
