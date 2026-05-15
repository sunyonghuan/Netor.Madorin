namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// Workflow 任务状态。与 OrchestrationTask 表 Status 列的字符串值一一对应。
/// 详见 docs/未来版本策划/多智能体编排模式策划/06-工作模式独立模块设计.md §3.1。
/// </summary>
public enum WorkflowTaskStatus
{
    /// <summary>待启动（已创建但未进入执行）。</summary>
    Pending = 0,

    /// <summary>执行中。</summary>
    Running = 1,

    /// <summary>已暂停（HITL 等待用户输入，阶段 5B+）。</summary>
    Paused = 2,

    /// <summary>已成功完成，FinalReport 可用。</summary>
    Completed = 3,

    /// <summary>已失败（含宿主重启孤儿清理）。</summary>
    Failed = 4,

    /// <summary>已取消（用户主动取消或超时）。</summary>
    Cancelled = 5,
}

/// <summary>
/// <see cref="WorkflowTaskStatus"/> 与数据库字符串值之间的转换器。
/// </summary>
public static class WorkflowTaskStatusExtensions
{
    public static string ToDbValue(this WorkflowTaskStatus status) => status switch
    {
        WorkflowTaskStatus.Pending => "pending",
        WorkflowTaskStatus.Running => "running",
        WorkflowTaskStatus.Paused => "paused",
        WorkflowTaskStatus.Completed => "completed",
        WorkflowTaskStatus.Failed => "failed",
        WorkflowTaskStatus.Cancelled => "cancelled",
        _ => "unknown",
    };

    public static WorkflowTaskStatus FromDbValue(string? value) => value switch
    {
        "pending" => WorkflowTaskStatus.Pending,
        "running" => WorkflowTaskStatus.Running,
        "paused" => WorkflowTaskStatus.Paused,
        "completed" => WorkflowTaskStatus.Completed,
        "failed" => WorkflowTaskStatus.Failed,
        "cancelled" => WorkflowTaskStatus.Cancelled,
        _ => WorkflowTaskStatus.Pending,
    };
}
