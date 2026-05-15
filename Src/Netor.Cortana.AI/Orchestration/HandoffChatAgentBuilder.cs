using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.Logging;

namespace Netor.Cortana.AI.Orchestration;

/// <summary>
/// 阶段 3A：把 SDK <c>HandoffWorkflowBuilder</c> 的调用细节封装为单一职责工具类。
/// 输入是已经构建好的 triage / specialists（<see cref="AIAgent"/>），输出是包装好的
/// <see cref="WorkflowHostAgent"/>（实现 <see cref="AIAgent"/>）。
///
/// 设计要点：
/// - 每个 specialist 必须有 <c>Description</c> 或 <c>Name</c> 之一作为 handoff reason，
///   否则 SDK 会抛 <see cref="ArgumentException"/>（决策 3A-E）。本类提前过滤+记 warning。
/// - <c>EnableReturnToPrevious()</c>：后续轮次直达上次专家（文档 §3A.2 必选）。
/// - <c>EmitAgentResponseUpdateEvents(true)</c>：流式 delta 透传给宿主 Chat 路径，
///   保证 conversation.assistant.delta 事件链不断（文档 §3A.4）。
///
/// 详见：docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 3A。
/// </summary>
internal static class HandoffChatAgentBuilder
{
    /// <summary>
    /// 构建 HandoffChat <see cref="AIAgent"/>。
    /// </summary>
    /// <param name="triage">分流入口 Agent（用户视角的"客服"），必须由 <c>AIAgentFactory.Build</c> 完整路径构建（带 ChatHistoryDataProvider）。</param>
    /// <param name="specialists">候选专家 Agent 集合，由 <c>AIAgentFactory.BuildSubAgent</c> 轻量路径构建。</param>
    /// <param name="sessionId">会话 ID，用于生成稳定的 workflow agent id；可空。</param>
    /// <param name="options">编排器选项；当前仅用于 logger 提示 <see cref="AgentOrchestratorOptions.HandoffMaxChainLength"/>。</param>
    /// <param name="logger">日志输出器。</param>
    /// <returns>包装好的 <see cref="WorkflowHostAgent"/>，对调用方透明：<see cref="AIAgent.RunStreamingAsync"/> 行为与普通 ChatClientAgent 一致。</returns>
    /// <exception cref="InvalidOperationException">specialists 为空，或全部 specialist 缺少 Description/Name 导致注册失败。</exception>
    public static AIAgent Build(
        AIAgent triage,
        IReadOnlyList<AIAgent> specialists,
        string sessionId,
        AgentOrchestratorOptions options,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(triage);
        ArgumentNullException.ThrowIfNull(specialists);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);

        if (specialists.Count == 0)
        {
            throw new InvalidOperationException(
                "HandoffChat requires at least 1 specialist agent.");
        }

#pragma warning disable MAAIW001 // HandoffWorkflowBuilder 是 SDK 实验性 API
        var builder = AgentWorkflowBuilder.CreateHandoffBuilderWith(triage);

        var registered = 0;
        foreach (var specialist in specialists)
        {
            // 决策 3A-E：跳过缺少 handoff reason 的 specialist，避免 SDK 抛 ArgumentException。
            if (string.IsNullOrWhiteSpace(specialist.Description)
                && string.IsNullOrWhiteSpace(specialist.Name))
            {
                logger.LogWarning(
                    "HandoffChat 跳过 specialist：缺少 Description 和 Name (Id={Id})",
                    specialist.Id);
                continue;
            }

            builder = builder.WithHandoff(triage, specialist);
            registered++;
        }

        if (registered == 0)
        {
            throw new InvalidOperationException(
                "HandoffChat: 0 valid specialists registered (all lack Description and Name).");
        }

        var workflow = builder
            .EnableReturnToPrevious()              // 后续轮次直达上次专家（文档 §3A.2）
            .EmitAgentResponseUpdateEvents(true)   // 流式 delta 透传（文档 §3A.4）
            .Build();
#pragma warning restore MAAIW001

        var agentId = string.IsNullOrEmpty(sessionId)
            ? $"handoffchat-{Guid.NewGuid():N}"
            : $"handoffchat-{sessionId}";

        logger.LogInformation(
            "HandoffChat 已构建：triage={Triage}, specialists={Count}, maxChain={MaxChain}",
            triage.Name ?? triage.Id,
            registered,
            options.HandoffMaxChainLength);

        return workflow.AsAIAgent(
            id: agentId,
            name: "HandoffChat",
            description: "Customer-service triage and routing");
    }
}
