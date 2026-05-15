using Microsoft.Agents.AI;

namespace Netor.Cortana.AI.Workflow.Title;

/// <summary>
/// Workflow 任务标题生成器接口。
/// 决策 6-A：用户可以不填 Title，由 LLM 在任务完成后异步兜底生成。
/// 设计与 ChatHistoryDataProvider.GenerateAndUpdateTitleAsync 同模式：
/// 复用 IChatCompactionClientResolver 解析压缩模型，SuppressUsage 隔离 token 上报。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2B.2。
/// </summary>
public interface IWorkflowTitleGenerator
{
    /// <summary>
    /// 异步为指定任务生成标题。生成成功后：
    /// 1. 调用 <see cref="Entitys.Services.WorkflowTaskRepository.UpdateTitle"/> 更新数据库；
    /// 2. 发布 <see cref="Entitys.Events.OnWorkflowTaskTitleUpdated"/> 事件，UI 局部刷新。
    ///
    /// 调用方应当传入触发该任务的 Agent（通常是 Manager Agent），以便复用其 IChatClient 配置。
    /// </summary>
    /// <param name="agent">用于解析压缩模型的参考 Agent。</param>
    /// <param name="taskId">任务 ID。</param>
    /// <param name="initialInput">用户的初始任务描述（用于生成标题的上下文）。</param>
    /// <param name="finalReport">任务最终报告（可空，用于丰富标题上下文）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task GenerateAndUpdateTitleAsync(
        AIAgent agent,
        string taskId,
        string initialInput,
        string? finalReport,
        CancellationToken cancellationToken);
}
