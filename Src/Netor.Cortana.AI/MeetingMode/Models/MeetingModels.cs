using System.Text.Json.Serialization;

namespace Netor.Cortana.AI.MeetingMode.Models;

/// <summary>
/// 会议工具调用快照，写入 MeetingMessages.ToolCallsJson。
/// </summary>
public sealed record MeetingToolCallRecord(
    string CallId,
    string Name,
    string ArgsJson,
    string? ResultText,
    string? ExceptionMessage,
    long DurationMs);

/// <summary>
/// 会议附件引用快照，写入消息或工具上下文 JSON。
/// </summary>
public sealed record MeetingAttachmentRef(
    string Name,
    string Path,
    string MimeType);

/// <summary>
/// 会议每轮结构化调度决策，用于按业务依赖选择下一位发言者。
/// </summary>
public sealed record MeetingTurnDecision(
    [property: JsonPropertyName("phase")] string Phase,
    [property: JsonPropertyName("need")] string Need,
    [property: JsonPropertyName("nextSpeakerId")] string NextSpeakerId,
    [property: JsonPropertyName("reason")] string Reason,
    [property: JsonPropertyName("prerequisites")] IReadOnlyList<string> Prerequisites,
    [property: JsonPropertyName("readyForSpeaker")] bool ReadyForSpeaker,
    [property: JsonPropertyName("currentPhaseId")] string? CurrentPhaseId = null,
    [property: JsonPropertyName("executionPlan")] MeetingExecutionPlan? ExecutionPlan = null,
    [property: JsonPropertyName("interruptType")] string? InterruptType = null,
    [property: JsonPropertyName("affectedPhaseIds")] IReadOnlyList<string>? AffectedPhaseIds = null,
    [property: JsonPropertyName("invalidatedPhaseIds")] IReadOnlyList<string>? InvalidatedPhaseIds = null,
    [property: JsonPropertyName("resumeAction")] string? ResumeAction = null);

/// <summary>
/// 会议动态执行计划，由主持人按会议主题和参会者生成。
/// </summary>
public sealed record MeetingExecutionPlan(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("assumptions")] IReadOnlyList<string> Assumptions,
    [property: JsonPropertyName("phases")] IReadOnlyList<MeetingExecutionPhase> Phases);

/// <summary>
/// 会议执行计划阶段。
/// </summary>
public sealed record MeetingExecutionPhase(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("goal")] string Goal,
    [property: JsonPropertyName("allowedAgentIds")] IReadOnlyList<string> AllowedAgentIds,
    [property: JsonPropertyName("prerequisites")] IReadOnlyList<string> Prerequisites,
    [property: JsonPropertyName("completionCriteria")] string CompletionCriteria);
