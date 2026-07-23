using Madorin.AI.Runtime.Contracts;

namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record ToolAuthorizationResult(
    ToolAuthorizationDecision Decision,
    ToolPermissionContext? EffectivePermission = null,
    string[]? MissingCapabilities = null,
    string? Reason = null);
