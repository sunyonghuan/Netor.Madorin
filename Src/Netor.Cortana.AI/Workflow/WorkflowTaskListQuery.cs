namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// Workflow 任务列表查询参数。决策 7-A：对齐 Chat 风格的列表查询能力。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B.2。
/// </summary>
public sealed record WorkflowTaskListQuery
{
    /// <summary>工作区 ID。空字符串表示不过滤。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>是否包含归档项；默认 false（与 Chat 列表一致）。</summary>
    public bool IncludeArchived { get; init; }

    /// <summary>状态过滤（可空，传 null 表示不过滤）。</summary>
    public IReadOnlyList<string>? Statuses { get; init; }

    /// <summary>每页数量，默认 30。</summary>
    public int Limit { get; init; } = 30;

    /// <summary>分页偏移，默认 0。</summary>
    public int Offset { get; init; }
}
