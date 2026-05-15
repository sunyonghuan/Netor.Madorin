namespace Netor.Cortana.AI.Workflow;

/// <summary>
/// 创建 Workflow 任务的请求。阶段 2B 占位实现仅消费 Title / Mode / SubMode / InitialInput 等字段。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B.2。
/// </summary>
public sealed record WorkflowTaskRequest
{
    /// <summary>任务标题。允许为空，决策 6-A 由 LLM 兜底生成。</summary>
    public string? Title { get; init; }

    /// <summary>任务摘要（1024 字以内）。</summary>
    public string? Summary { get; init; }

    /// <summary>顶层模式 "discussion" / "execution"。</summary>
    public required string Mode { get; init; }

    /// <summary>子模式 "groupchat" / "magentic" / "parallelanalysis" / "handoffexecution"。</summary>
    public required string SubMode { get; init; }

    /// <summary>任务初始描述（用户输入）。</summary>
    public required string InitialInput { get; init; }

    /// <summary>JSON 数组，附件路径与描述（可空）。</summary>
    public string? InitialAttachmentsJson { get; init; }

    /// <summary>工作区 ID（与 ChatSession.Categorize 同源）。</summary>
    public required string WorkspaceId { get; init; }

    /// <summary>触发任务的来源："user" / "chat-suggested" / "duplicated" / "schedule"。</summary>
    public string CreatedBy { get; init; } = "user";

    /// <summary>触发该任务的 Chat 会话 ID（如有）。</summary>
    public string? SourceSessionId { get; init; }

    /// <summary>决策 10-A：复制为新任务时记录原任务 ID。</summary>
    public string? SourceTaskId { get; init; }

    /// <summary>追踪 ID。如果为空，执行器会自动生成。</summary>
    public string? TraceId { get; init; }

    /// <summary>Magentic Manager / GroupChat Moderator 的智能体 ID。</summary>
    public string? ManagerAgentId { get; init; }

    /// <summary>Manager 智能体显示名称（冗余存储）。</summary>
    public string? ManagerAgentName { get; init; }

    /// <summary>参与该任务的 Member 智能体 ID 列表。</summary>
    public IReadOnlyList<string> MemberAgentIds { get; init; } = [];

    /// <summary>OverridesJson（阶段 5B+ 用于 MaxRounds 等覆盖）。</summary>
    public string? OverridesJson { get; init; }
}
