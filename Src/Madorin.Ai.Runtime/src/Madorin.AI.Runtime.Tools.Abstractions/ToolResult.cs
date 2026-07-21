namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolResult(
    string CallId,
    bool Succeeded,
    string? ResultJson,
    string? ErrorCode);
