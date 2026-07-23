namespace Madorin.AI.Runtime.Tools.Abstractions;

public sealed record RuntimeToolPolicy
{
    public RuntimeToolPolicy(ToolPermissionContext defaultPermission)
        : this(defaultPermission, defaultPermission)
    {
    }

    public RuntimeToolPolicy(
        ToolPermissionContext defaultPermission,
        ToolPermissionContext? maximumPermission)
    {
        DefaultPermission = defaultPermission
            ?? throw new ArgumentNullException(nameof(defaultPermission));
        MaximumPermission = maximumPermission;
    }

    /// <summary>Permission used when no explicit Grant has been issued.</summary>
    public ToolPermissionContext DefaultPermission { get; init; }

    /// <summary>
    /// Optional hard ceiling intersected with explicit Grants. A null value allows a Grant to
    /// elevate beyond the default while remaining bounded by its own scope.
    /// </summary>
    public ToolPermissionContext? MaximumPermission { get; init; }

    public static RuntimeToolPolicy CreateRestricted(string workspaceRoot, string runId = "*")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        var root = Path.GetFullPath(workspaceRoot);
        return new RuntimeToolPolicy(
            new ToolPermissionContext(
                "runtime-default",
                runId,
                root,
                [root],
                [root],
                AllowOverwrite: false,
                AllowMove: false,
                AllowDelete: false,
                AllowedExecutables: [],
                AllowPowerShell: false,
                DenyNetwork: true,
                ExpiresAt: null,
                AllowedToolIds: null),
            maximumPermission: null);
    }
}
