namespace Madorin.AI.Runtime.Server;

/// <summary>Describes the current in-process Runtime state for direct CLI diagnostics.</summary>
public sealed record LocalRuntimeStatus(
    string RuntimeInstanceId,
    string WorkspaceRoot,
    int ActiveRunCount,
    long ManagedMemoryBytes,
    int ToolCount);
