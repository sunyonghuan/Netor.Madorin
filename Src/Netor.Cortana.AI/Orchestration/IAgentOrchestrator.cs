using Microsoft.Agents.AI;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// Chat 模式编排器接口。仅服务 None / ToolDelegation / HandoffChat 三种 Chat 范畴的编排模式。
/// Workflow 模式由 <see cref="TaskEngine.TaskExecutionEngine"/> 承接，不走此接口。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2A.2 / §2A.6。
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// 根据请求构建一个 <see cref="AIAgent"/>，可以是普通 ChatClientAgent，也可以是 Handoff Workflow 包装。
    /// 调用方（AiChatHostedService）使用返回的 Agent 走 RunStreamingAsync 生命周期。
    /// </summary>
    /// <param name="request">本轮编排请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    Task<AIAgent> BuildAgentAsync(
        AgentOrchestrationRequest request,
        CancellationToken cancellationToken);

    /// <summary>
    /// 获取最近一次编排的结果（参与 Agent 列表、警告、失败、token 用量等）。
    /// 阶段 2A 仅返回 mentions 投影；阶段 3A+ 起补充 Handoff 切换记录与真实 token 用量。
    /// </summary>
    AgentOrchestrationResult? GetLastResult();
}
