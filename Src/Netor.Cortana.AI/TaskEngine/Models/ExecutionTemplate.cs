namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 执行模板：用户保存的"满意的工作流"。
/// 下次类似任务可选用模板 → 计划制定子智能体参考模板重新规划（不是死板复制）。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §4.4。
/// </summary>
public sealed class ExecutionTemplate
{
    /// <summary>模板 ID。</summary>
    public required string TemplateId { get; init; }

    /// <summary>模板名称（用户命名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>模板描述（用户可编辑）。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>适用场景标签（如 "市场分析" / "视频制作" / "代码审查"）。</summary>
    public List<string> Tags { get; set; } = [];

    /// <summary>来源任务 ID（从哪个成功任务保存的）。</summary>
    public string SourceTaskId { get; set; } = string.Empty;

    /// <summary>
    /// 模板步骤结构（从成功任务的 ExecutionPlan 精简而来）。
    /// 保留：步骤标题、描述、执行模式、依赖关系、智能体类型描述。
    /// 不保留：运行时状态、具体结果、时间戳。
    /// </summary>
    public List<TemplateStep> Steps { get; set; } = [];

    /// <summary>创建时间。</summary>
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>使用次数（统计用）。</summary>
    public int UsageCount { get; set; }

    /// <summary>所属工作区 ID。</summary>
    public string WorkspaceId { get; set; } = string.Empty;
}

/// <summary>模板步骤（精简版，不含运行时状态）。</summary>
public sealed class TemplateStep
{
    /// <summary>步骤序号。</summary>
    public int Sequence { get; set; }

    /// <summary>步骤标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>步骤描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>执行模式（sequential / parallel / await_user）。</summary>
    public string ExecutionMode { get; set; } = "sequential";

    /// <summary>依赖步骤序号列表。</summary>
    public List<string> DependsOn { get; set; } = [];

    /// <summary>子智能体类型描述。</summary>
    public string AgentTypeDescription { get; set; } = string.Empty;

    /// <summary>所需工具列表。</summary>
    public List<string> RequiredTools { get; set; } = [];

    /// <summary>是否需要用户确认。</summary>
    public bool RequireUserConfirmation { get; set; }

    /// <summary>子任务列表（并行模式时使用）。</summary>
    public List<TemplateSubTask> SubTasks { get; set; } = [];
}

/// <summary>模板子任务（精简版）。</summary>
public sealed class TemplateSubTask
{
    /// <summary>子任务标题。</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>子任务描述。</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>子智能体类型描述。</summary>
    public string AgentTypeDescription { get; set; } = string.Empty;
}
