namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolPermissionContext(
    string RunId,
    string SessionId,
    string InvocationId,
    string AgentId,
    string? ParentInvocationId);
