namespace Madorin.AI.Runtime.Persistence.Abstractions;

public sealed record ToolIntentState(
    string CallId,
    string InvocationId,
    string RunId,
    string SessionId,
    string AgentId,
    string? ParentAgentId,
    string ToolId,
    string ToolCatalogVersion,
    string ArgumentsHash,
    ToolIntentStatus Status,
    string? GrantId,
    string? ApprovalRequestId,
    string? ResultJson,
    string? ResultHash,
    ToolResultBlobState? ResultBlob,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsResultVisible,
    DateTimeOffset CreatedAt,
    DateTimeOffset? SentAt,
    DateTimeOffset? CompletedAt,
    string? WorkStepId = null,
    string? PlanVersion = null);

public sealed record ToolIntentCompletion(
    string CallId,
    ToolIntentStatus ExpectedStatus,
    ToolIntentStatus Status,
    string? ResultJson,
    string? ResultHash,
    ToolResultBlobState? ResultBlob,
    string? ErrorCode,
    string? ErrorMessage,
    bool IsResultVisible,
    DateTimeOffset CompletedAt);
