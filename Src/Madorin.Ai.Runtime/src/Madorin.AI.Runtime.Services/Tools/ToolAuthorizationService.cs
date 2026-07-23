using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

public sealed class ToolAuthorizationService(
    IToolStateStore stateStore,
    RuntimeToolPolicy runtimePolicy,
    TimeProvider? timeProvider = null) : IToolAuthorizationService
{
    private readonly RuntimeToolPolicy _runtimePolicy = runtimePolicy
        ?? throw new ArgumentNullException(nameof(runtimePolicy));
    private readonly IToolStateStore _stateStore = stateStore
        ?? throw new ArgumentNullException(nameof(stateStore));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<ToolAuthorizationResult> AuthorizeAsync(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolPermissionContext? suppliedPermission = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!string.Equals(invocation.ToolId, descriptor.ToolId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The invocation and descriptor tool ids differ.", nameof(descriptor));
        }

        var now = _timeProvider.GetUtcNow();
        var policy = BindRun(_runtimePolicy.DefaultPermission, invocation.RunId);
        if (suppliedPermission is not null)
        {
            return AuthorizeContexts(
                invocation,
                descriptor,
                CreateExplicitGrantContexts(invocation.RunId, [suppliedPermission]),
                now);
        }

        if (invocation.ParentAgentId is null)
        {
            var defaultResult = AuthorizeContexts(invocation, descriptor, [policy], now);
            if (defaultResult.Decision is ToolAuthorizationDecision.Granted
                && IsDefaultAllowedTool(descriptor.ToolId)
                && !descriptor.RequiresApproval)
            {
                return defaultResult;
            }
        }

        var grants = await _stateStore.ListActiveGrantsAsync(invocation.RunId, now, ct)
            .ConfigureAwait(false);
        var byId = grants.ToDictionary(static grant => grant.GrantId, StringComparer.Ordinal);
        foreach (var grant in grants.Where(grant => IsCandidate(grant, invocation)))
        {
            if (!TryBuildGrantChain(grant, byId, invocation, out var grantChain))
            {
                continue;
            }

            var contexts = CreateExplicitGrantContexts(
                invocation.RunId,
                grantChain.Select(ToPermissionContext));
            var result = AuthorizeContexts(invocation, descriptor, contexts, now);
            if (result.Decision is ToolAuthorizationDecision.Granted)
            {
                return result;
            }
        }

        return descriptor.RequiresApproval
            ? new ToolAuthorizationResult(
                ToolAuthorizationDecision.NeedsApproval,
                MissingCapabilities: ["approval"],
                Reason: $"Tool '{descriptor.ToolId}' requires a call-bound approval.")
            : new ToolAuthorizationResult(
                ToolAuthorizationDecision.Denied,
                MissingCapabilities: [$"tool:{descriptor.ToolId}"],
                Reason: $"No effective Grant permits tool '{descriptor.ToolId}'.");
    }

    public async Task SaveGrantAsync(
        ToolGrant grant,
        string? agentId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(grant);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.GrantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(grant.WorkspaceRoot);
        ArgumentNullException.ThrowIfNull(grant.NetworkPolicy);
        var now = _timeProvider.GetUtcNow();
        if (grant.ExpiresAt <= now)
        {
            throw new ArgumentException("A Grant must expire in the future.", nameof(grant));
        }

        var effectiveAgentId = grant.AgentId ?? agentId;
        var context = ToolGrantCalculator.Intersect(
        [
            new ToolPermissionContext(
                grant.GrantId,
                grant.RunId,
                grant.WorkspaceRoot,
                grant.AllowedReadRoots ?? [],
                grant.AllowedWriteRoots ?? [],
                grant.AllowOverwrite,
                grant.AllowMove,
                grant.AllowDelete,
                grant.AllowedExecutables ?? [],
                grant.AllowPowerShell,
                grant.NetworkPolicy.DenyAll,
                grant.ExpiresAt,
                grant.AllowedToolIds,
                grant.AllowedCallIds,
                grant.MaximumRisk ?? ToolRiskLevel.Network,
                grant.AllowedEnvironmentVariables ?? [],
                grant.NetworkPolicy,
                effectiveAgentId,
                grant.ParentGrantId,
                grant.RootGrantId,
                grant.DelegationChain,
                grant.ApprovalRequestId)
        ]);

        ToolGrantState? parent = null;
        string rootGrantId;
        string[] delegationChain;
        if (grant.ParentGrantId is { } parentGrantId)
        {
            parent = await _stateStore.GetGrantAsync(parentGrantId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Parent Grant '{parentGrantId}' was not found.");
            if (parent.RevokedAt is not null || parent.ExpiresAt <= now || !parent.AllowDelegation)
            {
                throw new InvalidOperationException("The parent Grant cannot delegate.");
            }

            if (string.IsNullOrWhiteSpace(effectiveAgentId)
                || !parent.DelegatedAgentIds.Contains(effectiveAgentId, StringComparer.Ordinal))
            {
                throw new InvalidOperationException(
                    "The parent Grant does not permit delegation to this Agent.");
            }

            var parentContext = ToPermissionContext(parent);
            if (!ToolGrantCalculator.IsSubset(context, parentContext))
            {
                throw new InvalidOperationException(
                    "A delegated Grant cannot widen any parent permission dimension.");
            }

            rootGrantId = parent.RootGrantId;
            delegationChain = [.. parent.DelegationChain, parent.GrantId];
            if (grant.RootGrantId is not null
                && !string.Equals(grant.RootGrantId, rootGrantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("The delegated Grant root id is invalid.");
            }

            if (grant.DelegationChain is not null
                && !grant.DelegationChain.SequenceEqual(delegationChain, StringComparer.Ordinal))
            {
                throw new InvalidOperationException("The delegated Grant chain is invalid.");
            }
        }
        else
        {
            rootGrantId = grant.GrantId;
            delegationChain = [];
            if (grant.RootGrantId is not null
                && !string.Equals(grant.RootGrantId, grant.GrantId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("A root Grant must reference itself.");
            }
        }

        var networkJson = JsonSerializer.Serialize(
            grant.NetworkPolicy,
            RuntimeJsonContext.Default.NetworkPolicy);
        await _stateStore.UpsertGrantAsync(
            new ToolGrantState(
                grant.GrantId,
                grant.RunId,
                effectiveAgentId,
                grant.ParentGrantId,
                rootGrantId,
                context.WorkspaceRoot,
                context.AllowedToolIds ?? [],
                context.AllowedCallIds ?? [],
                (int)context.MaximumRisk,
                context.AllowedReadRoots,
                context.AllowedWriteRoots,
                context.AllowOverwrite,
                context.AllowMove,
                context.AllowDelete,
                context.AllowedExecutables,
                context.AllowedEnvironmentVariables ?? [],
                context.AllowPowerShell,
                networkJson,
                grant.ExpiresAt,
                grant.AllowDelegation,
                NormalizeValues(grant.DelegatedAgentIds),
                delegationChain,
                now,
                ApprovalRequestId: grant.ApprovalRequestId),
            ct).ConfigureAwait(false);
    }

    public Task<int> RevokeGrantAsync(
        string grantId,
        bool revokeDescendants = true,
        string? reason = null,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(grantId);
        return _stateStore.RevokeGrantAsync(
            grantId,
            revokeDescendants,
            _timeProvider.GetUtcNow(),
            reason,
            ct);
    }

    private static ToolAuthorizationResult AuthorizeContexts(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        IReadOnlyList<ToolPermissionContext> contexts,
        DateTimeOffset now)
    {
        foreach (var context in contexts)
        {
            if (context.ExpiresAt is { } expiresAt && expiresAt <= now)
            {
                return Denied("expired", "The effective Grant has expired.");
            }

            if (!string.Equals(context.RunId, "*", StringComparison.Ordinal)
                && !string.Equals(context.RunId, invocation.RunId, StringComparison.Ordinal))
            {
                return Denied("run", "The Grant belongs to another Run.");
            }

        }

        var effectiveAgentId = contexts
            .LastOrDefault(static context => context.AgentId is not null)
            ?.AgentId;
        if (effectiveAgentId is not null
            && !string.Equals(effectiveAgentId, invocation.AgentId, StringComparison.Ordinal))
        {
            return Denied("agent", "The effective Grant belongs to another Agent.");
        }

        if (invocation.ParentAgentId is not null
            && contexts.Skip(1).All(context => context.AgentId is null))
        {
            return Denied("delegation", "A child Agent requires an explicit delegated Grant.");
        }

        ToolPermissionContext effective;
        try
        {
            effective = ToolGrantCalculator.Intersect(contexts);
        }
        catch (ArgumentException ex)
        {
            return Denied("scope", ex.Message);
        }

        var missing = GetMissingCapabilities(invocation, descriptor, effective);
        return missing.Length == 0
            ? new ToolAuthorizationResult(
                ToolAuthorizationDecision.Granted,
                effective,
                MissingCapabilities: [])
            : descriptor.RequiresApproval && missing.Contains("approval", StringComparer.Ordinal)
                ? new ToolAuthorizationResult(
                    ToolAuthorizationDecision.NeedsApproval,
                    effective,
                    missing,
                    $"Tool '{descriptor.ToolId}' requires a call-bound approval.")
                : new ToolAuthorizationResult(
                    ToolAuthorizationDecision.Denied,
                    effective,
                    missing,
                    $"Grant '{effective.GrantId}' lacks: {string.Join(", ", missing)}.");
    }

    private static string[] GetMissingCapabilities(
        ToolInvocation invocation,
        ToolDescriptor descriptor,
        ToolPermissionContext permission)
    {
        var missing = new List<string>();
        if (permission.AllowedToolIds is not null
            && !permission.AllowedToolIds.Contains(descriptor.ToolId, StringComparer.Ordinal))
        {
            missing.Add($"tool:{descriptor.ToolId}");
        }

        if (permission.AllowedCallIds is not null
            && !permission.AllowedCallIds.Contains(invocation.CallId, StringComparer.Ordinal))
        {
            missing.Add($"call:{invocation.CallId}");
        }

        if (descriptor.Risk > permission.MaximumRisk)
        {
            missing.Add($"risk:{descriptor.Risk}");
        }

        if (descriptor.RequiresApproval
            && permission.ApprovalRequestId is null
            && (permission.AllowedCallIds is null
                || !permission.AllowedCallIds.Contains(invocation.CallId, StringComparer.Ordinal)))
        {
            missing.Add("approval");
        }

        var capabilities = descriptor.Capabilities is { Count: > 0 }
            ? descriptor.Capabilities
            : GetLegacyCapabilities(descriptor.Risk);
        foreach (var capability in capabilities)
        {
            switch (capability)
            {
                case "filesystem.read" when permission.AllowedReadRoots.Length == 0:
                    missing.Add(capability);
                    break;
                case "filesystem.write" when permission.AllowedWriteRoots.Length == 0:
                    missing.Add(capability);
                    break;
                case "filesystem.destructive"
                    when !permission.AllowOverwrite
                        && !permission.AllowMove
                        && !permission.AllowDelete:
                    missing.Add(capability);
                    break;
                case "process.execute" when permission.AllowedExecutables.Length == 0:
                    missing.Add(capability);
                    break;
                case "powershell.execute" when !permission.AllowPowerShell:
                    missing.Add(capability);
                    break;
                case "network.access" when permission.DenyNetwork:
                    missing.Add(capability);
                    break;
            }
        }

        return missing.Distinct(StringComparer.Ordinal).ToArray();
    }

    private static string[] GetLegacyCapabilities(ToolRiskLevel risk) => risk switch
    {
        ToolRiskLevel.SensitiveRead => ["filesystem.read"],
        ToolRiskLevel.Write => ["filesystem.write"],
        ToolRiskLevel.Destructive => ["filesystem.destructive"],
        ToolRiskLevel.Process => ["process.execute"],
        ToolRiskLevel.PowerShell => ["powershell.execute"],
        ToolRiskLevel.Network => ["network.access"],
        _ => []
    };

    private static bool IsCandidate(ToolGrantState grant, ToolInvocation invocation)
    {
        if (!string.Equals(grant.RunId, invocation.RunId, StringComparison.Ordinal))
        {
            return false;
        }

        return invocation.ParentAgentId is null
            ? grant.ParentGrantId is null
                && (grant.AgentId is null
                    || string.Equals(grant.AgentId, invocation.AgentId, StringComparison.Ordinal))
            : grant.ParentGrantId is not null
                && string.Equals(grant.AgentId, invocation.AgentId, StringComparison.Ordinal);
    }

    private static bool TryBuildGrantChain(
        ToolGrantState leaf,
        Dictionary<string, ToolGrantState> activeGrants,
        ToolInvocation invocation,
        out IReadOnlyList<ToolGrantState> chain)
    {
        var reversed = new List<ToolGrantState>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var current = leaf;
        while (true)
        {
            if (!seen.Add(current.GrantId)
                || !string.Equals(current.RunId, invocation.RunId, StringComparison.Ordinal))
            {
                chain = [];
                return false;
            }

            reversed.Add(current);
            if (current.ParentGrantId is not { } parentGrantId)
            {
                if (!string.Equals(current.RootGrantId, current.GrantId, StringComparison.Ordinal))
                {
                    chain = [];
                    return false;
                }

                break;
            }

            if (!activeGrants.TryGetValue(parentGrantId, out var parent)
                || !parent.AllowDelegation
                || current.AgentId is null
                || !parent.DelegatedAgentIds.Contains(current.AgentId, StringComparer.Ordinal)
                || !string.Equals(parent.RootGrantId, current.RootGrantId, StringComparison.Ordinal)
                || !ToolGrantCalculator.IsSubset(
                    ToPermissionContext(current),
                    ToPermissionContext(parent)))
            {
                chain = [];
                return false;
            }

            current = parent;
        }

        reversed.Reverse();
        if (invocation.ParentAgentId is not null
            && (reversed.Count < 2
                || !string.Equals(
                    reversed[^2].AgentId,
                    invocation.ParentAgentId,
                    StringComparison.Ordinal)))
        {
            chain = [];
            return false;
        }

        var expectedIds = reversed.Take(reversed.Count - 1)
            .Select(static grant => grant.GrantId)
            .ToArray();
        if (!leaf.DelegationChain.SequenceEqual(expectedIds, StringComparer.Ordinal))
        {
            chain = [];
            return false;
        }

        chain = reversed;
        return true;
    }

    private static ToolPermissionContext ToPermissionContext(ToolGrantState grant)
    {
        var network = JsonSerializer.Deserialize(
            grant.NetworkPolicyJson,
            RuntimeJsonContext.Default.NetworkPolicy)
            ?? throw new InvalidDataException($"Grant '{grant.GrantId}' has no network policy.");
        return new ToolPermissionContext(
            grant.GrantId,
            grant.RunId,
            grant.WorkspaceRoot,
            grant.AllowedReadRoots,
            grant.AllowedWriteRoots,
            grant.AllowOverwrite,
            grant.AllowMove,
            grant.AllowDelete,
            grant.AllowedExecutables,
            grant.AllowPowerShell,
            network.DenyAll,
            grant.ExpiresAt,
            grant.AllowedToolIds.Length == 0 ? null : grant.AllowedToolIds,
            grant.AllowedCallIds.Length == 0 ? null : grant.AllowedCallIds,
            (ToolRiskLevel)grant.MaximumRisk,
            grant.AllowedEnvironmentVariables,
            network,
            grant.AgentId,
            grant.ParentGrantId,
            grant.RootGrantId,
            grant.DelegationChain,
            grant.ApprovalRequestId);
    }

    private static ToolAuthorizationResult Denied(string capability, string reason) =>
        new(
            ToolAuthorizationDecision.Denied,
            MissingCapabilities: [capability],
            Reason: reason);

    private static bool IsDefaultAllowedTool(string toolId) =>
        string.Equals(toolId, "builtin.memory.read", StringComparison.Ordinal)
        || toolId.StartsWith("builtin.fs.", StringComparison.Ordinal)
        || toolId.StartsWith("builtin.content.", StringComparison.Ordinal);

    private static string[] NormalizeValues(string[]? values) =>
        values?
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray()
        ?? [];

    private List<ToolPermissionContext> CreateExplicitGrantContexts(
        string runId,
        IEnumerable<ToolPermissionContext> grants)
    {
        var contexts = new List<ToolPermissionContext>();
        if (_runtimePolicy.MaximumPermission is { } maximumPermission)
        {
            contexts.Add(BindRun(maximumPermission, runId));
        }

        contexts.AddRange(grants);
        return contexts;
    }

    private static ToolPermissionContext BindRun(
        ToolPermissionContext permission,
        string runId) =>
        string.Equals(permission.RunId, "*", StringComparison.Ordinal)
            ? permission with { RunId = runId }
            : permission;
}
