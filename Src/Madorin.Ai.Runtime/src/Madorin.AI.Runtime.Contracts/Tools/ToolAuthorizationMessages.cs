namespace Madorin.AI.Runtime.Contracts;

/// <summary>Returns the host decision for a tool permission request.</summary>
public sealed record ToolPermissionResponse(
    string CorrelationId,
    string CallId,
    ToolAuthorizationDecision Decision,
    ToolGrant? Grant = null,
    string? Reason = null);

/// <summary>Requests an explicit user decision for a tool call.</summary>
public sealed record ApprovalRequest(
    string CorrelationId,
    string ApprovalRequestId,
    string CallId,
    string RunId,
    string AgentId,
    string? ParentAgentId,
    string ToolId,
    string ArgumentsHash,
    ToolRiskLevel Risk,
    string TargetSummary,
    string Reason,
    string? SessionId = null,
    string? InvocationId = null,
    string? ParentInvocationId = null,
    string? WorkStepId = null,
    string? PlanVersion = null);

/// <summary>Returns the user decision and an optional narrowed grant.</summary>
public sealed record ApprovalResponse(
    string CorrelationId,
    string ApprovalRequestId,
    ToolAuthorizationDecision Decision,
    ToolGrant? Grant = null,
    string? Reason = null);

/// <summary>Revokes a grant and, by default, all grants delegated from it.</summary>
public sealed record GrantRevokeRequest(
    string GrantId,
    bool RevokeDescendants = true,
    string? Reason = null);

/// <summary>Reports how many grants were revoked.</summary>
public sealed record GrantRevokeResponse(
    string GrantId,
    int RevokedCount);
