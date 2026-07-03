using System.Text.Json.Serialization;

using Netor.Cortana.AI.WorkMode.Models;

namespace Netor.Cortana.AI.WorkMode.Json;

/// <summary>
/// 工作模式 AOT JSON 源生成上下文。
/// 所有持久化（WorkExecutionLogs.Content / WorkTasks.* JSON 列）
/// 与跨进程传输的对象都必须在此注册。
///
/// 不能用 <c>JsonSerializer.Serialize(obj)</c>（无 Context）——会触发反射，AOT 失败。
/// 正确用法：<c>JsonSerializer.Serialize(obj, WorkModeJsonContext.Default.&lt;TypeInfo&gt;)</c>。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(WorkPlan))]
[JsonSerializable(typeof(WorkPlanMainStep))]
[JsonSerializable(typeof(WorkPlanSubStep))]
// LogType=StepStart / StepComplete 等
[JsonSerializable(typeof(WorkExecutionLogRecords.StepStart))]
[JsonSerializable(typeof(WorkExecutionLogRecords.StepComplete))]
[JsonSerializable(typeof(WorkExecutionLogRecords.Thinking))]
[JsonSerializable(typeof(WorkExecutionLogRecords.ToolCall))]
[JsonSerializable(typeof(WorkExecutionLogRecords.ToolResult))]
[JsonSerializable(typeof(WorkExecutionLogRecords.Acceptance))]
[JsonSerializable(typeof(WorkExecutionLogRecords.Error))]
[JsonSerializable(typeof(WorkExecutionLogRecords.UserInterrupt))]
[JsonSerializable(typeof(WorkExecutionLogRecords.SelfEvaluation))]
[JsonSerializable(typeof(WorkExecutionLogRecords.RetryAttempt))]
[JsonSerializable(typeof(WorkExecutionLogRecords.DeadlockDetected))]
[JsonSerializable(typeof(WorkExecutionLogRecords.ParallelBlockStart))]
[JsonSerializable(typeof(WorkExecutionLogRecords.ParallelBlockEnd))]
[JsonSerializable(typeof(WorkExecutionLogRecords.NestedWorkflowStart))]
[JsonSerializable(typeof(WorkExecutionLogRecords.NestedWorkflowEnd))]
[JsonSerializable(typeof(WorkExecutionLogRecords.SubAgentDelta))]
[JsonSerializable(typeof(WorkExecutionLogRecords.SubAgentJobStart))]
[JsonSerializable(typeof(WorkExecutionLogRecords.SubAgentJobEnd))]
// HITL
[JsonSerializable(typeof(Netor.Cortana.AI.Hitl.Models.HitlPendingRequestSnapshot))]
[JsonSerializable(typeof(HitlAskUserRequest))]
// Mentions
[JsonSerializable(typeof(AgentMentionDto))]
[JsonSerializable(typeof(IReadOnlyList<AgentMentionDto>))]
[JsonSerializable(typeof(List<AgentMentionDto>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<int>))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(System.Text.Json.JsonElement))]
// Background Jobs
[JsonSerializable(typeof(Background.BackgroundJobStartRequest))]
[JsonSerializable(typeof(Background.SubAgentJobInput))]
[JsonSerializable(typeof(Background.BackgroundJobStatus))]
[JsonSerializable(typeof(Background.BackgroundJobState))]
public partial class WorkModeJsonContext : JsonSerializerContext;
