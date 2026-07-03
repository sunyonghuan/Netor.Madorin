using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// Chat 模式多智能体编排扩展点。
/// 当前阶段只定义契约，不接管现有聊天生命周期。
/// </summary>
public interface IAgentOrchestrator
{
    /// <summary>
    /// 根据编排请求构建当前 turn 的编排结果快照。
    /// </summary>
    Task<AgentOrchestrationResult> BuildAsync(
        AgentOrchestrationRequest request,
        IReadOnlyList<AIFunction>? additionalTools = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// 返回最近一次编排构建的结果快照。
    /// </summary>
    AgentOrchestrationResult? GetLastResult();
}
