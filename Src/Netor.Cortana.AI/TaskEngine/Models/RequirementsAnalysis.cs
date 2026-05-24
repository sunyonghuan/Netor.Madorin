namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 需求分析阶段的结构化输出。
/// 由需求分析子智能体产出，作为计划制定子智能体的输入。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §4.3。
/// </summary>
public sealed class RequirementsAnalysis
{
    /// <summary>用户原始输入。</summary>
    public string OriginalInput { get; set; } = string.Empty;

    /// <summary>需求要点列表（子智能体整理）。</summary>
    public List<string> KeyPoints { get; set; } = [];

    /// <summary>约束条件。</summary>
    public List<string> Constraints { get; set; } = [];

    /// <summary>预期交付物描述。</summary>
    public string ExpectedDeliverable { get; set; } = string.Empty;

    /// <summary>复杂度评估（low / medium / high）。</summary>
    public string ComplexityLevel { get; set; } = "medium";

    /// <summary>对话历史 ID 列表（需求分析阶段的多轮对话）。</summary>
    public List<string> ConversationMessageIds { get; set; } = [];
}
