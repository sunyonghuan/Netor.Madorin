namespace Cortana.Plugins.Memory.Models;

/// <summary>
/// 表示记忆系统状态查询请求。
/// </summary>
public sealed class MemoryStatusRequest
{
    /// <summary>
    /// 需要查询的智能体标识。
    /// </summary>
    public string? AgentId { get; init; }

    /// <summary>
    /// 需要查询的工作区标识。
    /// </summary>
    public string? WorkspaceId { get; init; }
}

/// <summary>
/// 表示记忆系统基础状态。
/// </summary>
public sealed class MemoryStatusResult
{
    /// <summary>
    /// 观察记录数量。
    /// </summary>
    public int ObservationCount { get; init; }

    /// <summary>
    /// 记忆片段数量。
    /// </summary>
    public int FragmentCount { get; init; }

    /// <summary>
    /// 抽象记忆数量。
    /// </summary>
    public int AbstractionCount { get; init; }

    /// <summary>
    /// 召回日志数量。
    /// </summary>
    public int RecallLogCount { get; init; }

    /// <summary>
    /// 当前处理状态。
    /// </summary>
    public string ProcessingState { get; init; } = string.Empty;

    /// <summary>
    /// 最近处理时间。
    /// </summary>
    public string? LastProcessedAt { get; init; }

    /// <summary>
    /// 已知问题。
    /// </summary>
    public IReadOnlyList<string> KnownIssues { get; init; } = [];
}

/// <summary>
/// 表示存储层提供的记忆系统状态快照。
/// </summary>
public sealed class MemoryStoreStatusSnapshot
{
    /// <summary>
    /// 观察记录数量。
    /// </summary>
    public int ObservationCount { get; init; }

    /// <summary>
    /// 记忆片段数量。
    /// </summary>
    public int FragmentCount { get; init; }

    /// <summary>
    /// 抽象记忆数量。
    /// </summary>
    public int AbstractionCount { get; init; }

    /// <summary>
    /// 召回日志数量。
    /// </summary>
    public int RecallLogCount { get; init; }
}
