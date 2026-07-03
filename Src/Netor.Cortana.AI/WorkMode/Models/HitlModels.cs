namespace Netor.Cortana.AI.WorkMode.Models;

/// <summary>
/// HITL 挂起请求载荷兼容别名。新代码请使用 <see cref="Netor.Cortana.AI.Hitl.Models.HitlPendingRequestSnapshot"/>。
/// </summary>
[Obsolete("Use Netor.Cortana.AI.Hitl.Models.HitlPendingRequestSnapshot instead.")]
public sealed record HitlPendingRequestSnapshot(
    string RequestId,
    string Kind,
    string? ToolName,
    string? ParameterText,
    string? Risk,
    string? Question) : Netor.Cortana.AI.Hitl.Models.HitlPendingRequestSnapshot(
        RequestId,
        Kind,
        ToolName,
        ParameterText,
        Risk,
        Question);

/// <summary>
/// AI 触发 ask_user 工具时的请求载荷（写入 RequestInfo.Data）。
/// </summary>
public sealed record HitlAskUserRequest(string Question);

/// <summary>
/// @ 子智能体提及的快照（持久化到 WorkTasks.MentionsJson）。
/// </summary>
public sealed record AgentMentionDto(string AgentId, string AgentName);
