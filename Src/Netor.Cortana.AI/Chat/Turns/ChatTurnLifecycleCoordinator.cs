using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI;

/// <summary>
/// 统一处理聊天 turn 的取消通知、完成事件去重与僵尸 turn 清理。
/// </summary>
public sealed class ChatTurnLifecycleCoordinator(
    ChatTurnCancellationRegistry turnRegistry,
    ChatConversationEventPublisher conversationEventPublisher,
    IEnumerable<IAiOutputChannel> outputChannels,
    ILogger<ChatTurnLifecycleCoordinator> logger)
{
    private static readonly TimeSpan OutputCancellationTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan CurrentRunStopTimeout = TimeSpan.FromSeconds(5);

    public async Task NotifyChannelsCancelledAsync(ChatTurnContext? turnContext, CancellationToken cancellationToken)
    {
        if (turnContext is null || string.IsNullOrWhiteSpace(turnContext.TurnId))
        {
            return;
        }

        if (!turnContext.TryMarkCancellationNotified())
        {
            return;
        }

        foreach (var channel in outputChannels.Where(static channel => channel.IsActive))
        {
            try
            {
                await channel.OnCancelledAsync(turnContext.TurnId)
                    .WaitAsync(OutputCancellationTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                logger.LogWarning(ex, "输出通道 {Channel} 取消通知超时", channel.Name);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogDebug("输出通道取消通知被中止：{Channel}", channel.Name);
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "输出通道 {Channel} 处理取消事件失败", channel.Name);
            }
        }
    }

    public async Task WaitForCurrentRunToStopAsync(CancellationToken cancellationToken)
    {
        var waitUntil = DateTimeOffset.UtcNow + CurrentRunStopTimeout;
        while (turnRegistry.HasActiveTurn())
        {
            if (DateTimeOffset.UtcNow >= waitUntil)
            {
                var zombieTurn = turnRegistry.ForceClearCurrentTurn();
                var zombieTurnId = zombieTurn?.TurnId ?? string.Empty;
                logger.LogWarning("等待当前 AI turn 停止超时，已强制释放运行态。Turn：{TurnId}", zombieTurnId);

                try
                {
                    zombieTurn?.Cts.Dispose();
                }
                catch (ObjectDisposedException)
                {
                }

                PublishConversationTurnCompletedOnce(
                    zombieTurn,
                    ConversationTurnStatus.Failed,
                    string.Empty,
                    string.Empty,
                    "zombie killed: HTTP/MCP 未响应取消",
                    0,
                    0);
                return;
            }

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        }
    }

    public ConversationEventMetadata CreateConversationEventMetadata(
        string sessionId,
        string turnId,
        string traceId,
        string userMessageId,
        string assistantMessageId,
        AiProviderEntity provider,
        AgentEntity agentEntity,
        AiModelEntity model,
        AgentOrchestrationResult? orchestrationResult = null)
    {
        return new ConversationEventMetadata(
            sessionId,
            turnId,
            traceId,
            provider.Id,
            provider.Name,
            agentEntity.Id,
            agentEntity.Name,
            model.Id,
            model.Name,
            userMessageId,
            assistantMessageId,
            orchestrationResult?.Mode ?? AgentOrchestrationMode.None,
            orchestrationResult?.UsedAgentIds,
            orchestrationResult?.Warnings);
    }

    public void PublishConversationTurnCompletedOnce(
        ChatTurnContext? turnContext,
        ConversationTurnStatus status,
        string userInput,
        string assistantResponse,
        string? errorMessage,
        int assistantDeltaCount,
        int attachmentCount)
    {
        var metadata = turnContext?.Metadata;
        if (metadata is null)
        {
            return;
        }

        if (turnContext is null || !turnContext.TryMarkCompletionPublished())
        {
            return;
        }

        conversationEventPublisher.PublishTurnCompleted(
            metadata,
            status,
            userInput,
            assistantResponse,
            errorMessage,
            assistantDeltaCount,
            attachmentCount);
    }
}
