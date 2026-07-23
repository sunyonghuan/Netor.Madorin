using System.Text.Json.Serialization;

namespace Madorin.AI.Runtime.Contracts;

public sealed record ToolGrant(
    string GrantId,
    string RunId,
    string WorkspaceRoot,
    string[] AllowedReadRoots,
    string[] AllowedWriteRoots,
    bool AllowOverwrite,
    bool AllowMove,
    bool AllowDelete,
    string[] AllowedExecutables,
    bool AllowPowerShell,
    NetworkPolicy NetworkPolicy,
    DateTimeOffset ExpiresAt,
    bool AllowDelegation,
    string[] DelegatedAgentIds,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AllowedToolIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AllowedCallIds = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    ToolRiskLevel? MaximumRisk = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? AllowedEnvironmentVariables = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? AgentId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ParentGrantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RootGrantId = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string[]? DelegationChain = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? ApprovalRequestId = null);
