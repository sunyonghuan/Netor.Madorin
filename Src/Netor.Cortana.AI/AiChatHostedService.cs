using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.AI;

/// <summary>
/// 统一 AI 对话引擎。纯粹的 AI 处理核心，不感知具体输入来源，
/// 通过 <see cref="IAiChatEngine"/> 接口接收输入，
/// 将 AI 流式回复广播到所有活跃的 <see cref="IAiOutputChannel"/> 输出通道。
/// </summary>
public sealed class AiChatHostedService(
    ChatAgentResolver agentResolver,
    ChatMessageWriter messageWriter,
    ChatSessionService sessionService,
    ChatTurnCancellationRegistry turnRegistry,
    ChatTurnInterruptionCoordinator turnInterruptionCoordinator,
    ChatSelectionContextService selectionContextService,
    ChatConfigurationCoordinator configurationCoordinator,
    ChatTurnLifecycleCoordinator turnLifecycleCoordinator,
    ChatTurnPreparationService turnPreparationService,
    ChatTurnExecutor turnExecutor,
    ChatImageTurnExecutor imageTurnExecutor,
    ChatVideoTurnExecutor videoTurnExecutor,
    ILogger<AiChatHostedService> logger) : IAiChatEngine, IHostedService, IDisposable
{
    private bool _disposed;
    private CancellationTokenSource? _serviceCts;

    /// <summary>
    /// 当前是否正在进行 AI 对话。
    /// </summary>
    public bool IsRunning => turnInterruptionCoordinator.IsRunning;

    // ──────────────────── 工作模式内部访问器 ────────────────────

    /// <summary>
    /// 当前会话 ID（供 WorkMode 使用）。
    /// </summary>
    public string? CurrentSessionId => sessionService.CurrentId;

    /// <summary>
    /// 当前工作区 ID（供 WorkMode 使用）。
    /// </summary>
    public string? CurrentWorkspaceId => selectionContextService.CurrentWorkspaceId;

    /// <summary>
    /// 当前 AI 提供商（供 WorkMode 使用）。
    /// </summary>
    public AiProviderEntity? CurrentProvider => selectionContextService.CurrentProvider;

    /// <summary>
    /// 当前智能体（供 WorkMode 使用）。
    /// </summary>
    public AgentEntity? CurrentAgent => selectionContextService.CurrentAgent;

    /// <summary>
    /// 当前模型（供 WorkMode 使用）。
    /// </summary>
    public AiModelEntity? CurrentModel => selectionContextService.CurrentModel;

    // ──────────────────── IHostedService ────────────────────

    /// <summary>
    /// 启动 AI 对话引擎。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _serviceCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        configurationCoordinator.InitializeDefaults();
        configurationCoordinator.SubscribeConfigChangeEvents();
        logger.LogInformation("AI 对话引擎已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 AI 对话引擎。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _serviceCts?.Cancel();
        await CancelCurrentTaskAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("AI 对话引擎已停止");
    }

    /// <summary>
    /// 切换 AI 提供商。
    /// </summary>
    public void ChangeProvider(string providerId) => agentResolver.ChangeProvider(providerId);

    /// <summary>
    /// 切换 AI 模型。
    /// </summary>
    public void ChangeModel(string modelId) => agentResolver.ChangeModel(modelId);

    /// <summary>
    /// 切换智能体。
    /// </summary>
    public void ChangeAgent(string agentId) => agentResolver.ChangeAgent(agentId);

    /// <summary>
    /// 创建新会话。
    /// </summary>
    public async Task NewSessionAsync()
    {
        if (!selectionContextService.TryGetCurrent(out var selectionContext))
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），无法新建会话");
            return;
        }

        var started = await turnPreparationService.TryStartNewSessionAsync(
            selectionContext.Provider,
            selectionContext.AgentEntity,
            selectionContext.Model,
            selectionContext.CreateRuntimeContext(),
            CancellationToken.None).ConfigureAwait(false);
        if (!started)
        {
            logger.LogWarning("无法根据当前默认配置构建 Agent，新会话创建已跳过");
        }
    }

    /// <summary>
    /// 恢复指定历史会话。
    /// </summary>
    public async Task ResumeSessionAsync(string sessionId)
    {
        if (!selectionContextService.TryGetCurrent(out var selectionContext))
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），无法恢复会话");
            return;
        }

        var resumed = await turnPreparationService.TryResumeSessionAsync(
            sessionId,
            selectionContext.Provider,
            selectionContext.AgentEntity,
            selectionContext.Model,
            selectionContext.CreateRuntimeContext(),
            CancellationToken.None).ConfigureAwait(false);
        if (!resumed)
        {
            logger.LogWarning("无法根据当前默认配置构建 Agent，历史会话恢复已跳过");
        }
    }

    /// <summary>
    /// 中止当前正在进行的流式响应。
    /// </summary>
    public void Stop()
    {
        turnInterruptionCoordinator.StopCurrentTurn();
    }

    /// <summary>
    /// 取消当前正在进行的 AI 对话，并通知所有输出通道清理。
    /// </summary>
    public void CancelCurrentTask()
    {
        turnInterruptionCoordinator.CancelCurrentTurn();
    }

    /// <summary>
    /// 异步取消当前正在进行的 AI 对话，并清理当前活跃输出通道。
    /// </summary>
    public async Task CancelCurrentTaskAsync(CancellationToken cancellationToken = default)
    {
        await turnInterruptionCoordinator.CancelCurrentTurnAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 发送用户消息并启动流式响应。支持附件（图片/文件）的多模态消息。
    /// AI 流式回复将广播到所有活跃的输出通道。
    /// </summary>
    public async Task SendMessageAsync(string userInput, CancellationToken cancellationToken, List<AttachmentInfo>? attachments = null, List<AgentMention>? mentions = null)
    {
        if (string.IsNullOrWhiteSpace(userInput) && (attachments is null || attachments.Count == 0)) return;

        if (IsRunning)
        {
            logger.LogInformation("当前对话正在进行中，取消旧任务以接受新输入");
        }

        await turnInterruptionCoordinator.EnsureReadyForNextTurnAsync(cancellationToken).ConfigureAwait(false);

        if (!selectionContextService.TryGetCurrent(out var selectionContext))
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），跳过回复");
            return;
        }

        // 阶段 2A：通过 IAgentOrchestrator 解析编排模式并构建 Agent。
        // mentions==0 时复用当前缓存 Agent（与现状一致）；其它情况按 Mode 重新构建。
        // 参见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §2A.3 / §2A.4。
        var safeMentions = (IReadOnlyList<AgentMention>)(mentions ?? []);

        // P4：复杂任务建议已移除（老 WorkflowSuggestionDetector 功能），
        // P4 架构下用户直接在工作流 tab 发起任务，不需要从 Chat 桥接。

        var turnState = await turnPreparationService.TryPrepareConversationTurnAsync(
            userInput,
            safeMentions,
            selectionContext.Provider,
            selectionContext.AgentEntity,
            selectionContext.Model,
            selectionContext.CreateRuntimeContext(),
            cancellationToken).ConfigureAwait(false);
        if (turnState is null)
        {
            logger.LogWarning("当前默认配置无法构建 Agent，已跳过本轮发送");
            return;
        }

        LogOrchestration(turnState.OrchestrationResult);

        await turnExecutor.ExecuteAsync(
            userInput,
            attachments ?? [],
            safeMentions,
            turnState.Agent,
            turnState.Session,
            turnState.Provider,
            turnState.AgentEntity,
            turnState.Model,
            turnState.OrchestrationResult,
            messageWriter.SaveUserMessage,
            turnLifecycleCoordinator.PublishConversationTurnCompletedOnce,
            turnLifecycleCoordinator.CreateConversationEventMetadata,
            turnLifecycleCoordinator.NotifyChannelsCancelledAsync,
            agentResolver.ClearCachedAgent,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过当前模型的图片生成端点生成图片，并将结果作为普通对话消息展示和持久化。
    /// </summary>
    public async Task GenerateImageAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await turnInterruptionCoordinator.EnsureReadyForNextTurnAsync(cancellationToken).ConfigureAwait(false);

        if (!selectionContextService.TryGetCurrent(out var selectionContext))
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），无法生成图片");
            return;
        }

        selectionContextService.EnsureOutputCapability(selectionContext, OutputCapabilities.Image);

        var turnState = await PrepareGenerationTurnAsync(selectionContext, cancellationToken).ConfigureAwait(false);
        await imageTurnExecutor.ExecuteAsync(
            prompt,
            turnState.Agent,
            turnState.Session,
            turnState.Provider,
            turnState.AgentEntity,
            turnState.Model,
            messageWriter.SaveUserMessage,
            messageWriter.SaveAssistantMessage,
            turnLifecycleCoordinator.PublishConversationTurnCompletedOnce,
            turnLifecycleCoordinator.CreateConversationEventMetadata,
            turnLifecycleCoordinator.NotifyChannelsCancelledAsync,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过当前模型的视频生成端点生成视频，并将结果作为普通对话消息展示和持久化。
    /// </summary>
    public async Task GenerateVideoAsync(string prompt, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(prompt))
        {
            return;
        }

        await turnInterruptionCoordinator.EnsureReadyForNextTurnAsync(cancellationToken).ConfigureAwait(false);

        if (!selectionContextService.TryGetCurrent(out var selectionContext))
        {
            logger.LogWarning("AI 服务未初始化（缺少默认提供商/智能体/模型），无法生成视频");
            return;
        }

        selectionContextService.EnsureOutputCapability(selectionContext, OutputCapabilities.Video);

        var turnState = await PrepareGenerationTurnAsync(selectionContext, cancellationToken).ConfigureAwait(false);
        await videoTurnExecutor.ExecuteAsync(
            prompt,
            turnState.Agent,
            turnState.Session,
            turnState.Provider,
            turnState.AgentEntity,
            turnState.Model,
            messageWriter.SaveUserMessage,
            messageWriter.SaveAssistantMessage,
            turnLifecycleCoordinator.PublishConversationTurnCompletedOnce,
            turnLifecycleCoordinator.CreateConversationEventMetadata,
            turnLifecycleCoordinator.NotifyChannelsCancelledAsync,
            cancellationToken).ConfigureAwait(false);
    }

    // ──────────────────── 初始化 ────────────────────

    private async Task<ChatPreparedTurnState> PrepareGenerationTurnAsync(
        ChatSelectionContext selectionContext,
        CancellationToken cancellationToken)
    {
        return await turnPreparationService.PrepareGenerationTurnAsync(
            selectionContext.Provider,
            selectionContext.AgentEntity,
            selectionContext.Model,
            selectionContext.CreateRuntimeContext(),
            cancellationToken).ConfigureAwait(false);
    }

    private void LogOrchestration(Orchestration.AgentOrchestrationResult? result)
    {
        if (result is null)
        {
            return;
        }

        if (result.Mode == Orchestration.AgentOrchestrationMode.None && result.Warnings.Count == 0)
        {
            logger.LogDebug("当前 turn 使用默认单智能体路径。");
            return;
        }

        logger.LogInformation(
            "当前 turn 编排模式：{Mode}，参与智能体：{Agents}，Warnings：{WarningCount}",
            result.Mode,
            result.UsedAgentIds.Count == 0 ? "无" : string.Join(", ", result.UsedAgentIds),
            result.Warnings.Count);
    }

    // ──────────────────── IDisposable ────────────────────

    /// <summary>
    /// 释放资源。
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _serviceCts?.Cancel();
        _serviceCts?.Dispose();
        turnRegistry.CancelCurrentTurn()?.Cts.Dispose();
    }
}
