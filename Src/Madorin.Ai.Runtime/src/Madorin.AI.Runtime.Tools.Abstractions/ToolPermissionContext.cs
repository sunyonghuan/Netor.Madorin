using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolPermissionContext(
    string GrantId,
    string RunId,
    string WorkspaceRoot,
    string[] AllowedReadRoots,
    string[] AllowedWriteRoots,
    bool AllowOverwrite = false,
    bool AllowMove = false,
    bool AllowDelete = false,
    string[] AllowedExecutables = null!,
    bool AllowPowerShell = false,
    bool DenyNetwork = true,
    DateTimeOffset? ExpiresAt = null,
    string[]? AllowedToolIds = null,
    string[]? AllowedCallIds = null,
    ToolRiskLevel MaximumRisk = ToolRiskLevel.Write,
    string[]? AllowedEnvironmentVariables = null,
    NetworkPolicy? NetworkPolicy = null,
    string? AgentId = null,
    string? ParentGrantId = null,
    string? RootGrantId = null,
    string[]? DelegationChain = null,
    string? ApprovalRequestId = null,
    string? StepId = null,
    string? ParentInvocationId = null,
    string? PlanVersion = null)
{
    public string[] AllowedExecutables { get; init; } = AllowedExecutables ?? [];
}
