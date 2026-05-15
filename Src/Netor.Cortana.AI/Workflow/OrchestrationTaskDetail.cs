using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// Workflow 任务详情 DTO，<see cref="IWorkflowExecutor.GetTaskDetailAsync"/> 的返回类型。
/// 在 OrchestrationTask 实体的基础上叠加运行时开关（IsLive / CanCancel / CanDuplicate）。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B.2。
/// </summary>
public sealed record OrchestrationTaskDetail
{
    /// <summary>底层任务实体（数据库快照）。</summary>
    public required OrchestrationTaskEntity Task { get; init; }

    /// <summary>参与者列表。</summary>
    public IReadOnlyList<OrchestrationParticipantEntity> Participants { get; init; } = [];

    /// <summary>步骤列表（按 Sequence 升序）。</summary>
    public IReadOnlyList<OrchestrationStepEntity> Steps { get; init; } = [];

    /// <summary>消息列表（按 Sequence 升序）。阶段 2B 占位实现为空。</summary>
    public IReadOnlyList<OrchestrationMessageEntity> Messages { get; init; } = [];

    /// <summary>
    /// 是否处于"活跃"运行时态（Running 或 Paused）。
    /// 用于 UI 决定是否走 SubscribeAsync 实时跟随。
    /// </summary>
    public bool IsLive { get; init; }

    /// <summary>是否允许取消（Running 中可取消，其他状态不可）。</summary>
    public bool CanCancel { get; init; }

    /// <summary>是否允许复制为新任务（Completed / Failed / Cancelled 都允许，决策 10-A）。</summary>
    public bool CanDuplicate { get; init; }
}
