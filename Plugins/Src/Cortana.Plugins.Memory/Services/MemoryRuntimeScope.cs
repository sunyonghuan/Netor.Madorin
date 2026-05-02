namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 当前运行模式下的默认记忆作用域。
/// </summary>
public sealed record MemoryRuntimeScope(string AgentId, string? WorkspaceId, string Source);
