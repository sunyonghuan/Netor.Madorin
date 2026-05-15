using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 默认 <see cref="IAgentOrchestrator"/> 实现。
/// 阶段 2A 仅做"根据 Mode 委托给 AIAgentFactory"的薄封装，把 Chat 行为从 AiChatHostedService 抽离出来。
/// 阶段 3A 起接入 HandoffChat 分支（<see cref="HandoffChatAgentBuilder"/>），调用 SDK
/// <c>AgentWorkflowBuilder.CreateHandoffBuilderWith</c> 构造客服式分流智能体。
/// </summary>
public sealed class AgentOrchestrator(
    AIAgentFactory factory,
    AiProviderService providerService,
    AiModelService modelService,
    AgentOrchestratorOptions options,
    ILogger<AgentOrchestrator> logger) : IAgentOrchestrator
{
    private AgentOrchestrationResult? _lastResult;

    /// <summary>
    /// 根据 mentions 数量解析编排模式。阶段 2A 默认规则：
    /// 0 mentions → None（普通对话）；
    /// 1 mention → None（直接对话该 Agent）；
    /// 2+ mentions → ToolDelegation（Coordinator 模式，阶段 1 已实现）。
    /// 阶段 3A 起 HandoffChat 由调用方显式传 <see cref="AgentOrchestrationMode.HandoffChat"/> 触发（决策 3A-A），
    /// 不在此处自动判定，避免破坏 Coordinator 默认行为。阶段 5+ 引入 <c>IsTriageAgent</c> 字段后再扩展。
    /// </summary>
    public static AgentOrchestrationMode ResolveMode(IReadOnlyList<AgentMention> mentions)
        => mentions.Count switch
        {
            0 => AgentOrchestrationMode.None,
            1 => AgentOrchestrationMode.None,
            _ => AgentOrchestrationMode.ToolDelegation,
        };

    public Task<AIAgent> BuildAgentAsync(
        AgentOrchestrationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        // 决策 2A.7：UsedAgentIds 由宿主真实记录提供，不再依赖模型自述。
        // 阶段 2A 时 UsedAgentIds 与 mentions 完全一致；阶段 3A+ 起补充 Handoff 切换记录。
        _lastResult = new AgentOrchestrationResult
        {
            UsedAgentIds = [.. request.Mentions.Select(m => m.Agent.Id)],
            Warnings = [],
            Failures = [],
        };

        AIAgent agent = request.Mode switch
        {
            AgentOrchestrationMode.None
                => factory.Build(request.MainAgent, request.MainProvider, request.MainModel),

            AgentOrchestrationMode.ToolDelegation
                => factory.BuildWithSubAgents(
                    request.MainAgent,
                    request.MainProvider,
                    request.MainModel,
                    [.. request.Mentions],
                    providerService,
                    modelService),

            AgentOrchestrationMode.HandoffChat
                => BuildHandoffChatAgent(request),

            _ => throw new ArgumentOutOfRangeException(
                nameof(request),
                request.Mode,
                "Unknown AgentOrchestrationMode"),
        };

        logger.LogDebug(
            "AgentOrchestrator built agent: mode={Mode}, mainAgent={MainAgent}, mentions={MentionCount}, maxSubTasks={MaxSubTasks}",
            request.Mode, request.MainAgent.Name, request.Mentions.Count, options.MaxSubTasks);

        return Task.FromResult(agent);
    }

    /// <summary>
    /// 阶段 3A：HandoffChat 构建路径。
    /// 决策 3A-B：<c>Mentions[0]</c> 为 triage，<c>Mentions[1..]</c> 为 specialists。
    /// 决策 3A-F：以下情况降级为单 Agent（<c>factory.Build(MainAgent)</c>）：
    ///   1. <c>Mentions.Count &lt; 2</c>
    ///   2. specialists 全部解析失败或缺少 Description/Name
    ///   3. SDK 构建抛异常
    /// 降级路径仅记 warning/error，保证用户感知不到失败。
    /// </summary>
    private AIAgent BuildHandoffChatAgent(AgentOrchestrationRequest request)
    {
        if (request.Mentions.Count < 2)
        {
            logger.LogWarning(
                "HandoffChat 需要至少 2 个 mentions（1 triage + 1+ specialist），实际 {Count}，降级为单 Agent",
                request.Mentions.Count);
            return factory.Build(request.MainAgent, request.MainProvider, request.MainModel);
        }

        var triageEntity = request.Mentions[0].Agent;
        var specialistEntities = request.Mentions.Skip(1).Select(m => m.Agent).ToList();

        try
        {
            var (triage, specialists) = factory.BuildHandoffAgents(
                triageEntity,
                request.MainProvider,
                request.MainModel,
                specialistEntities,
                providerService,
                modelService);

            if (specialists.Count == 0)
            {
                logger.LogWarning(
                    "HandoffChat 0 个有效 specialist（厂商/模型解析全部失败），降级为单 triage Agent");
                return triage;
            }

            return HandoffChatAgentBuilder.Build(
                triage,
                specialists,
                request.SessionId ?? string.Empty,
                options,
                logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "HandoffChat 构建失败，降级为单 Agent");
            return factory.Build(request.MainAgent, request.MainProvider, request.MainModel);
        }
    }

    public AgentOrchestrationResult? GetLastResult() => _lastResult;
}
