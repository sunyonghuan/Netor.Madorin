namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 进程内记忆作用域上下文。
/// </summary>
public sealed class MemoryRuntimeContext(string defaultAgentId, string? defaultWorkspaceId, string defaultSource) : IMemoryRuntimeContext
{
    private readonly object _gate = new();
    private MemoryRuntimeScope _scope = new(
        NormalizeRequired(defaultAgentId, "default"),
        NormalizeOptional(defaultWorkspaceId),
        NormalizeRequired(defaultSource, "tool"));

    /// <inheritdoc />
    public MemoryRuntimeScope GetScope()
    {
        lock (_gate)
        {
            return _scope;
        }
    }

    /// <inheritdoc />
    public void SetScope(string? agentId, string? workspaceId, string? source)
    {
        lock (_gate)
        {
            _scope = new MemoryRuntimeScope(
                NormalizeOptional(agentId) ?? _scope.AgentId,
                NormalizeOptional(workspaceId) ?? _scope.WorkspaceId,
                NormalizeOptional(source) ?? _scope.Source);
        }
    }

    /// <inheritdoc />
    public string ResolveAgentId(string? agentId)
    {
        return NormalizeOptional(agentId) ?? GetScope().AgentId;
    }

    /// <inheritdoc />
    public string? ResolveWorkspaceId(string? workspaceId)
    {
        return NormalizeOptional(workspaceId) ?? GetScope().WorkspaceId;
    }

    /// <inheritdoc />
    public string ResolveSource(string? source)
    {
        return NormalizeOptional(source) ?? GetScope().Source;
    }

    private static string NormalizeRequired(string? value, string fallback)
    {
        return NormalizeOptional(value) ?? fallback;
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
