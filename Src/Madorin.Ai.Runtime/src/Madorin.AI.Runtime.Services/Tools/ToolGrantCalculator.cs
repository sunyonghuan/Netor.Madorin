using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Tools.Abstractions;

namespace Madorin.AI.Runtime.Services.Tools;

/// <summary>Computes least-privilege intersections without persistence or transport side effects.</summary>
public static class ToolGrantCalculator
{
    public static ToolPermissionContext Intersect(
        IReadOnlyList<ToolPermissionContext> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        if (permissions.Count == 0)
        {
            throw new ArgumentException("At least one permission is required.", nameof(permissions));
        }

        var result = Normalize(permissions[0]);
        for (var index = 1; index < permissions.Count; index++)
        {
            var next = Normalize(permissions[index]);
            var runId = IntersectRunId(result.RunId, next.RunId);
            EnsureSameWorkspace(result.WorkspaceRoot, next.WorkspaceRoot);
            var network = IntersectNetwork(
                result.NetworkPolicy ?? new NetworkPolicy(result.DenyNetwork, []),
                next.NetworkPolicy ?? new NetworkPolicy(next.DenyNetwork, []));
            result = new ToolPermissionContext(
                next.GrantId,
                runId,
                result.WorkspaceRoot,
                IntersectRoots(result.AllowedReadRoots, next.AllowedReadRoots),
                IntersectRoots(result.AllowedWriteRoots, next.AllowedWriteRoots),
                result.AllowOverwrite && next.AllowOverwrite,
                result.AllowMove && next.AllowMove,
                result.AllowDelete && next.AllowDelete,
                IntersectValues(result.AllowedExecutables, next.AllowedExecutables) ?? [],
                result.AllowPowerShell && next.AllowPowerShell,
                network.DenyAll,
                Earliest(result.ExpiresAt, next.ExpiresAt),
                IntersectValues(result.AllowedToolIds, next.AllowedToolIds),
                IntersectValues(result.AllowedCallIds, next.AllowedCallIds),
                (ToolRiskLevel)Math.Min((int)result.MaximumRisk, (int)next.MaximumRisk),
                IntersectValues(
                    result.AllowedEnvironmentVariables,
                    next.AllowedEnvironmentVariables),
                network,
                next.AgentId ?? result.AgentId,
                next.ParentGrantId,
                next.RootGrantId ?? result.RootGrantId,
                next.DelegationChain ?? result.DelegationChain,
                next.ApprovalRequestId ?? result.ApprovalRequestId,
                MergeTraceValue(
                    result.StepId,
                    next.StepId,
                    nameof(ToolPermissionContext.StepId)),
                MergeTraceValue(
                    result.ParentInvocationId,
                    next.ParentInvocationId,
                    nameof(ToolPermissionContext.ParentInvocationId)),
                MergeTraceValue(
                    result.PlanVersion,
                    next.PlanVersion,
                    nameof(ToolPermissionContext.PlanVersion)));
        }

        return result;
    }

    public static bool IsSubset(
        ToolPermissionContext candidate,
        ToolPermissionContext parent)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(parent);
        ToolPermissionContext intersection;
        try
        {
            intersection = Intersect([parent, candidate]);
        }
        catch (ArgumentException)
        {
            return false;
        }

        return EquivalentScope(intersection, Normalize(candidate));
    }

    private static ToolPermissionContext Normalize(ToolPermissionContext permission)
    {
        ArgumentNullException.ThrowIfNull(permission);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.GrantId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.RunId);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission.WorkspaceRoot);
        var workspaceRoot = Path.GetFullPath(permission.WorkspaceRoot);
        return permission with
        {
            WorkspaceRoot = workspaceRoot,
            AllowedReadRoots = NormalizeRoots(permission.AllowedReadRoots, workspaceRoot),
            AllowedWriteRoots = NormalizeRoots(permission.AllowedWriteRoots, workspaceRoot),
            AllowedExecutables = NormalizeValues(permission.AllowedExecutables) ?? [],
            AllowedToolIds = NormalizeValues(permission.AllowedToolIds),
            AllowedCallIds = NormalizeValues(permission.AllowedCallIds),
            AllowedEnvironmentVariables = NormalizeValues(permission.AllowedEnvironmentVariables),
            NetworkPolicy = NormalizeNetwork(
                permission.NetworkPolicy ?? new NetworkPolicy(permission.DenyNetwork, [])),
            DelegationChain = permission.DelegationChain?.ToArray()
        };
    }

    private static NetworkPolicy NormalizeNetwork(NetworkPolicy policy) =>
        policy with
        {
            AllowedHosts = policy.DenyAll
                ? []
                : NormalizeValues(policy.AllowedHosts) ?? [],
            AllowedSchemes = NormalizeValues(policy.AllowedSchemes),
            AllowedPorts = policy.AllowedPorts?.Distinct().Order().ToArray(),
            AllowedAddressRanges = NormalizeValues(policy.AllowedAddressRanges)
        };

    private static NetworkPolicy IntersectNetwork(NetworkPolicy first, NetworkPolicy second)
    {
        var denyAll = first.DenyAll || second.DenyAll;
        return new NetworkPolicy(
            denyAll,
            denyAll
                ? []
                : IntersectValues(first.AllowedHosts, second.AllowedHosts) ?? [],
            IntersectValues(first.AllowedSchemes, second.AllowedSchemes),
            IntersectValues(first.AllowedPorts, second.AllowedPorts),
            IntersectValues(first.AllowedAddressRanges, second.AllowedAddressRanges),
            Minimum(first.MaxRedirects, second.MaxRedirects),
            Minimum(first.MaxResponseBytes, second.MaxResponseBytes));
    }

    private static string[] IntersectRoots(string[] first, string[] second)
    {
        var intersections = new HashSet<string>(PathComparer);
        foreach (var firstRoot in first)
        {
            foreach (var secondRoot in second)
            {
                if (IsWithin(firstRoot, secondRoot))
                {
                    intersections.Add(Path.GetFullPath(secondRoot));
                }
                else if (IsWithin(secondRoot, firstRoot))
                {
                    intersections.Add(Path.GetFullPath(firstRoot));
                }
            }
        }

        return intersections.Order(PathComparer).ToArray();
    }

    private static string[] NormalizeRoots(string[] roots, string workspaceRoot)
    {
        ArgumentNullException.ThrowIfNull(roots);
        var normalized = roots
            .Select(root => Path.GetFullPath(root, workspaceRoot))
            .Distinct(PathComparer)
            .Order(PathComparer)
            .ToArray();
        if (normalized.Any(root => !IsWithin(workspaceRoot, root)))
        {
            throw new ArgumentException("A permission root is outside its workspace.", nameof(roots));
        }

        return normalized;
    }

    private static string[]? IntersectValues(string[]? first, string[]? second)
    {
        if (first is null)
        {
            return second?.ToArray();
        }

        if (second is null)
        {
            return first.ToArray();
        }

        return first.Intersect(second, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    }

    private static int[]? IntersectValues(int[]? first, int[]? second)
    {
        if (first is null)
        {
            return second?.ToArray();
        }

        if (second is null)
        {
            return first.ToArray();
        }

        return first.Intersect(second).Order().ToArray();
    }

    private static string[]? NormalizeValues(string[]? values) =>
        values?.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private static string? MergeTraceValue(
        string? first,
        string? second,
        string valueName)
    {
        if (string.IsNullOrWhiteSpace(first))
        {
            return string.IsNullOrWhiteSpace(second) ? null : second;
        }

        if (string.IsNullOrWhiteSpace(second)
            || string.Equals(first, second, StringComparison.Ordinal))
        {
            return first;
        }

        throw new ArgumentException(
            $"Conflicting tool permission trace value '{valueName}'.");
    }

    private static bool EquivalentScope(
        ToolPermissionContext first,
        ToolPermissionContext second) =>
        string.Equals(first.RunId, second.RunId, StringComparison.Ordinal)
        && PathComparer.Equals(first.WorkspaceRoot, second.WorkspaceRoot)
        && SetEquals(first.AllowedReadRoots, second.AllowedReadRoots, PathComparer)
        && SetEquals(first.AllowedWriteRoots, second.AllowedWriteRoots, PathComparer)
        && first.AllowOverwrite == second.AllowOverwrite
        && first.AllowMove == second.AllowMove
        && first.AllowDelete == second.AllowDelete
        && SetEquals(first.AllowedExecutables, second.AllowedExecutables, StringComparer.Ordinal)
        && first.AllowPowerShell == second.AllowPowerShell
        && first.DenyNetwork == second.DenyNetwork
        && Nullable.Equals(first.ExpiresAt, second.ExpiresAt)
        && NullableSetEquals(first.AllowedToolIds, second.AllowedToolIds)
        && NullableSetEquals(first.AllowedCallIds, second.AllowedCallIds)
        && first.MaximumRisk == second.MaximumRisk
        && NullableSetEquals(
            first.AllowedEnvironmentVariables,
            second.AllowedEnvironmentVariables)
        && NetworkEquivalent(first.NetworkPolicy, second.NetworkPolicy);

    private static bool NetworkEquivalent(NetworkPolicy? first, NetworkPolicy? second)
    {
        if (first is null || second is null)
        {
            return first is null && second is null;
        }

        return first.DenyAll == second.DenyAll
            && SetEquals(first.AllowedHosts ?? [], second.AllowedHosts ?? [], StringComparer.Ordinal)
            && NullableSetEquals(first.AllowedSchemes, second.AllowedSchemes)
            && NullableSetEquals(first.AllowedAddressRanges, second.AllowedAddressRanges)
            && NullableIntSetEquals(first.AllowedPorts, second.AllowedPorts)
            && first.MaxRedirects == second.MaxRedirects
            && first.MaxResponseBytes == second.MaxResponseBytes;
    }

    private static bool NullableSetEquals(string[]? first, string[]? second) =>
        first is null || second is null
            ? first is null && second is null
            : SetEquals(first, second, StringComparer.Ordinal);

    private static bool NullableIntSetEquals(int[]? first, int[]? second) =>
        first is null || second is null
            ? first is null && second is null
            : first.ToHashSet().SetEquals(second);

    private static bool SetEquals(
        IEnumerable<string> first,
        IEnumerable<string> second,
        StringComparer comparer) =>
        first.ToHashSet(comparer).SetEquals(second);

    private static DateTimeOffset? Earliest(DateTimeOffset? first, DateTimeOffset? second) =>
        first is null
            ? second
            : second is null
                ? first
                : first <= second ? first : second;

    private static int? Minimum(int? first, int? second) =>
        first is null ? second : second is null ? first : Math.Min(first.Value, second.Value);

    private static long? Minimum(long? first, long? second) =>
        first is null ? second : second is null ? first : Math.Min(first.Value, second.Value);

    private static string IntersectRunId(string first, string second)
    {
        if (string.Equals(first, "*", StringComparison.Ordinal))
        {
            return second;
        }

        if (string.Equals(second, "*", StringComparison.Ordinal)
            || string.Equals(first, second, StringComparison.Ordinal))
        {
            return first;
        }

        throw new ArgumentException("Permissions belong to different Runs.");
    }

    private static void EnsureSameWorkspace(string first, string second)
    {
        if (!PathComparer.Equals(Path.GetFullPath(first), Path.GetFullPath(second)))
        {
            throw new ArgumentException("Permissions belong to different workspaces.");
        }
    }

    private static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return string.Equals(relative, ".", StringComparison.Ordinal)
            || (!Path.IsPathRooted(relative)
                && !string.Equals(relative, "..", StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.DirectorySeparatorChar}",
                    StringComparison.Ordinal)
                && !relative.StartsWith(
                    $"..{Path.AltDirectorySeparatorChar}",
                    StringComparison.Ordinal));
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
