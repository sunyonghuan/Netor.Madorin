namespace Netor.Cortana.AI.Hitl.Models;

/// <summary>
/// HITL 挂起请求载荷，持久化到具体模式的 PendingRequestData 字段。
/// </summary>
public record HitlPendingRequestSnapshot(
    string RequestId,
    string Kind,
    string? ToolName,
    string? ParameterText,
    string? Risk,
    string? Question);
