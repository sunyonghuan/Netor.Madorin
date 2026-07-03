namespace Netor.Cortana.AI.Handoff;

/// <summary>
/// start_work_task 调用时需要从当前模式读取的运行上下文。
/// </summary>
public sealed record HandoffRuntimeContext(
    string? SessionId,
    string? WorkspaceId,
    string? AgentId,
    string? ProviderId,
    string? ModelId);
