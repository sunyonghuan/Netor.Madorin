namespace Netor.Cortana.AI.WorkMode.Models;

/// <summary>
/// 工作模式执行日志的 Content 字段对应的强类型 record（按 LogType 分发）。
/// 详见 Docs/已完成功能规划/工作模式方案策划/10-数据模型与持久化设计.md §3.2。
/// </summary>
public static class WorkExecutionLogRecords
{
    public sealed record StepStart(string StepTitle, string Department, string Context);
    public sealed record StepComplete(string StepTitle, string Result);
    public sealed record Thinking(string? AuthorName, string Text);
    public sealed record ToolCall(string CallId, string ToolName, string? ParametersJson);
    public sealed record ToolResult(string CallId, string Status, string? ResultText, string? Error);
    public sealed record Acceptance(string StepTitle, string Verdict, string Reason);
    public sealed record Error(string Source, string Message);
    public sealed record UserInterrupt(string Content);

    // 16 文档：长任务可靠性
    public sealed record SelfEvaluation(string StepTitle, int Iteration, int Score, string Verdict, IReadOnlyList<string> Issues);
    public sealed record RetryAttempt(int Attempt, int MaxAttempts, string ErrorMessage, string ExceptionType);
    public sealed record DeadlockDetected(string Reason);

    // 11 文档：并行 / 嵌套
    public sealed record ParallelBlockStart(string BlockId, IReadOnlyList<string> StepTitles);
    public sealed record ParallelBlockEnd(string BlockId, int TotalCount, int SuccessCount);
    public sealed record NestedWorkflowStart(string NestedTaskId, string Goal);
    public sealed record NestedWorkflowEnd(string NestedTaskId, string FinalReport);

    // 子智能体背景执行
    public sealed record SubAgentDelta(string JobId, string? AuthorName, string? Text);
    public sealed record SubAgentJobStart(string JobId, string? OwnerName, string Query);
    public sealed record SubAgentJobEnd(string JobId, string Status, string? ResultJson, string? Error);
}
