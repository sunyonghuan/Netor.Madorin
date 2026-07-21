namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolInvocation(
    string CallId,
    string ToolId,
    string ArgumentsJson,
    ToolPermissionContext PermissionContext);
