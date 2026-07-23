namespace Madorin.AI.Runtime.Persistence.Abstractions;

public sealed record ToolApprovalState(
    string ApprovalRequestId,
    string CallId,
    string RunId,
    string AgentId,
    string ToolId,
    string ArgumentsHash,
    string Decision,
    string? GrantId,
    string? Reason,
    DateTimeOffset RequestedAt,
    DateTimeOffset? DecidedAt);
