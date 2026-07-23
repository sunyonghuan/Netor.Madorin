namespace Madorin.AI.Runtime.Persistence.Abstractions;

public sealed record ToolAuditRecord(
    string AuditId,
    string CallId,
    string RunId,
    string AgentId,
    string? ParentAgentId,
    string ToolId,
    string ToolCatalogVersion,
    int Risk,
    string ArgumentsHash,
    string TargetSummary,
    string? GrantId,
    string? ApprovalRequestId,
    string[] DelegationChain,
    string Status,
    string? ResultHash,
    string DiagnosticId,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt = null,
    long? DurationMilliseconds = null);
