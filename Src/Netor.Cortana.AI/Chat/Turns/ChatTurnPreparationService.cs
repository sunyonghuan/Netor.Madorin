using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Delegation;
using Netor.Cortana.AI.Handoff;
using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 负责专家模式 turn 执行前的 Agent、handoff tool 与 Session 准备。
/// </summary>
public sealed class ChatTurnPreparationService(
    IAgentOrchestrator agentOrchestrator,
    ChatSessionService sessionService,
    WorkHandoffTools handoffTools,
    ExpertDelegationTools delegationTools)
{
    public async Task<bool> TryStartNewSessionAsync(
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        HandoffRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var agent = await ResolveAgentForCurrentTurnAsync(
            userInput: null,
            mentions: [],
            provider,
            agentEntity,
            model,
            sessionId: runtimeContext.SessionId,
            workspaceId: runtimeContext.WorkspaceId,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);

        _ = await sessionService.NewSessionAsync(
            agent.Agent,
            provider,
            agentEntity,
            model,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TryResumeSessionAsync(
        string sessionId,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        HandoffRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var agent = await ResolveAgentForCurrentTurnAsync(
            userInput: null,
            mentions: [],
            provider,
            agentEntity,
            model,
            sessionId,
            runtimeContext.WorkspaceId,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);

        _ = await sessionService.ResumeSessionAsync(
            sessionId,
            agent.Agent,
            provider,
            agentEntity,
            model,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<ChatPreparedTurnState?> TryPrepareConversationTurnAsync(
        string? userInput,
        IReadOnlyList<AgentMention> mentions,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        HandoffRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var orchestration = await ResolveAgentForCurrentTurnAsync(
            userInput,
            mentions,
            provider,
            agentEntity,
            model,
            sessionService.CurrentId,
            runtimeContext.WorkspaceId,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);

        // 复用当前活跃会话（恢复/新建后 _currentSession 已就绪），避免每轮重解析为"最近会话"
        // 导致恢复的历史会话被覆盖、AI 上下文串到别的会话。冷启动无当前会话时自动回退到最近会话。
        var session = await sessionService.EnsureCurrentSessionAsync(
            orchestration.Agent,
            provider,
            agentEntity,
            model,
            cancellationToken).ConfigureAwait(false);
        return new ChatPreparedTurnState(
            orchestration.Agent,
            session,
            provider,
            agentEntity,
            model,
            orchestration);
    }

    public async Task<ChatPreparedTurnState> PrepareGenerationTurnAsync(
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        HandoffRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var orchestration = await ResolveAgentForCurrentTurnAsync(
            userInput: null,
            mentions: [],
            provider,
            agentEntity,
            model,
            sessionService.CurrentId,
            runtimeContext.WorkspaceId,
            runtimeContext,
            cancellationToken).ConfigureAwait(false);

        var session = await sessionService.EnsureCurrentSessionAsync(
            orchestration.Agent,
            provider,
            agentEntity,
            model,
            cancellationToken).ConfigureAwait(false);
        return new ChatPreparedTurnState(
            orchestration.Agent,
            session,
            provider,
            agentEntity,
            model,
            orchestration);
    }

    private async Task<AgentOrchestrationResult> ResolveAgentForCurrentTurnAsync(
        string? userInput,
        IReadOnlyList<AgentMention> mentions,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        string? sessionId,
        string? workspaceId,
        HandoffRuntimeContext runtimeContext,
        CancellationToken cancellationToken)
    {
        var request = new AgentOrchestrationRequest(
            mentions.Count >= 2 ? AgentOrchestrationMode.ToolDelegation : AgentOrchestrationMode.None,
            AgentExecutionStrategy.Sequential,
            agentEntity,
            provider,
            model,
            mentions,
            [],
            sessionId,
            workspaceId,
            userInput);

        return await agentOrchestrator.BuildAsync(
            request,
            CreateChatHandoffTools(agentEntity, runtimeContext),
            cancellationToken).ConfigureAwait(false);
    }

    private IReadOnlyList<AIFunction> CreateChatHandoffTools(
        AgentEntity agentEntity,
        HandoffRuntimeContext runtimeContext)
    {
        return [
            handoffTools.CreateStartWorkTaskTool(
                "chat",
                // 工具被调用时（非构建时）才读 CurrentId，确保 EnsureCurrentSessionAsync 已就绪。
                () => runtimeContext with { SessionId = sessionService.CurrentId ?? runtimeContext.SessionId },
                sourceId: runtimeContext.SessionId),
            ..delegationTools.CreateTools(agentEntity, runtimeContext)
        ];
    }
}
