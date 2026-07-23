namespace Madorin.AI.Runtime.Contracts;

public sealed record ToolPermissionRequest(
    string ApprovalRequestId,
    string AgentId,
    string? ParentAgentId,
    string ToolId,
    string CallId,
    string ArgumentsHash,
    string Risk,
    string RequestedScope,
    string Reason,
    string? CorrelationId = null,
    string? RunId = null,
    string? ToolCatalogVersion = null,
    string[]? RequestedCapabilities = null);
