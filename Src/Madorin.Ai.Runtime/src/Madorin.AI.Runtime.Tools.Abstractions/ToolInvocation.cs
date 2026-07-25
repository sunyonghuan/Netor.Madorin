namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolInvocation(
    string CallId,
    string ToolId,
    string AgentId,
    string? ParentAgentId,
    string ArgumentsJson,
    string RunId,
    string SessionId,
    string? InvocationId = null,
    ToolPermissionContext? PermissionContext = null,
    string? ToolCatalogVersion = null,
    int? TimeoutMilliseconds = null,
    string? WorkStepId = null,
    string? PlanVersion = null,
    string? ParentInvocationId = null);
