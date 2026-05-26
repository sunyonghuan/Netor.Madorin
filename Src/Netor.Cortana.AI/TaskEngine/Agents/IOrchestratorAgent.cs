using Netor.Cortana.AI.TaskEngine.Models;
using Netor.Cortana.AI.TaskEngine.Persistence;

namespace Netor.Cortana.AI.TaskEngine.Agents;

/// <summary>
/// 主智能体编排接口。
/// 每个方法内部都是：创建子智能体 → 委托任务 → 收集结果。
/// 主智能体自身只做编排决策，不做具体工作。
/// P4-1 仅定义接口，P4-3 提供实现。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §8.3。
/// </summary>
public interface IOrchestratorAgent
{
    /// <summary>
    /// 阶段 1：需求分析。
    /// 创建需求分析师子智能体，与用户多轮对话，输出结构化需求。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="userInput">用户原始输入。</param>
    /// <param name="onAskUser">
    ///   用户交互回调：子智能体提出澄清问题时调用。
    ///   参数 (轮次, 问题文本) → 返回用户回答。传 null 则退化为 LLM 自我迭代。
    /// </param>
    /// <param name="onAiMessage">AI 每轮回复完成时的回调（用于发布到对话流）。传 null 则不通知。</param>
    /// <param name="ct">取消令牌。</param>
    Task<RequirementsAnalysis> RunRequirementsPhaseAsync(
        string taskId,
        string userInput,
        Func<int, string, Task<string>>? onAskUser,
        Action<string>? onAiMessage,
        CancellationToken ct);

    /// <summary>
    /// 阶段 2：计划制定。
    /// 创建计划制定师子智能体，根据需求 + 可选模板生成执行计划。
    /// 包含与用户的对话式讨论（用户可多轮修改），直到用户确认。
    /// </summary>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="requirements">需求分析结果。</param>
    /// <param name="template">可选的模板。</param>
    /// <param name="onAskUser">
    ///   用户交互回调：子智能体提出讨论问题时调用。
    ///   参数 (轮次, 问题文本) → 返回用户回答。传 null 则退化为单轮生成。
    /// </param>
    /// <param name="onAiMessage">AI 每轮回复完成时的回调（用于发布到对话流）。传 null 则不通知。</param>
    /// <param name="ct">取消令牌。</param>
    Task<ExecutionPlan> RunPlanningPhaseAsync(
        string taskId,
        RequirementsAnalysis requirements,
        ExecutionTemplate? template,
        Func<int, string, Task<string>>? onAskUser,
        Action<string>? onAiMessage,
        CancellationToken ct);

    /// <summary>
    /// 阶段 3 单步执行：为指定步骤创建子智能体并委托执行。
    /// 子智能体的 instructions 和工具由主智能体根据步骤描述动态生成。
    /// </summary>
    /// <param name="onAiMessage">子智能体每轮输出时的回调（用于发布到对话流）。传 null 则不通知。</param>
    /// <param name="workspaceDir">本步骤的工作目录（绝对路径），注入到系统提示词中。</param>
    Task<StepResult> ExecuteStepAsync(
        string taskId,
        ExecutionPlan plan,
        PlanStep step,
        Action<string>? onAiMessage,
        string? workspaceDir,
        CancellationToken ct);

    /// <summary>
    /// 阶段 4：验证。
    /// 创建验证专家子智能体，检查执行结果是否满足需求。
    /// </summary>
    /// <returns>验证结果（分数/摘要/问题列表）。</returns>
    Task<ValidationResult> RunValidationPhaseAsync(
        string taskId,
        ExecutionPlan plan,
        CancellationToken ct);

    /// <summary>
    /// 暂停后恢复：创建差异分析子智能体，做增量分析。
    /// 判断哪些已完成步骤需要重做、哪些可保留。
    /// </summary>
    Task<PlanDiffResult> AnalyzePlanDiffAsync(
        string taskId,
        ExecutionPlan oldPlan,
        ExecutionPlan newPlan,
        CancellationToken ct);

    /// <summary>
    /// 文档 08 §3.2：对话模式意图识别。
    /// 主智能体根据对话历史 + 当前用户输入，识别意图并生成回复。
    /// </summary>
    /// <param name="taskId">任务 ID（用于日志）。</param>
    /// <param name="conversationHistory">对话历史（role=user/ai 的消息列表，按时间升序）。</param>
    /// <param name="userMessage">用户当前输入。</param>
    /// <param name="planContext">当前执行计划的简要描述（供 LLM 参考）。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>意图识别结果（意图标识 + AI 回复文本）。</returns>
    Task<ConversationIntentResponseDto> RecognizeConversationIntentAsync(
        string taskId,
        IReadOnlyList<(string Role, string Content)> conversationHistory,
        string userMessage,
        string? planContext,
        CancellationToken ct);
}

/// <summary>
/// 计划差异分析结果。暂停修改计划后恢复执行时，
/// 由差异分析子智能体输出，指导引擎从合适的位置恢复。
/// </summary>
public sealed class PlanDiffResult
{
    /// <summary>需要重做的步骤 ID 列表（Status 重置为 Pending）。</summary>
    public List<string> StepsToRedo { get; set; } = [];

    /// <summary>可保留结果的步骤 ID 列表（Status 保持 Completed）。</summary>
    public List<string> StepsToKeep { get; set; } = [];

    /// <summary>需要更新的依赖关系描述。</summary>
    public List<string> UpdatedDependencies { get; set; } = [];
}
