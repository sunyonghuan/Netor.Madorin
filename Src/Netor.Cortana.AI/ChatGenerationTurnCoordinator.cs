using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Orchestration;
using Netor.Cortana.Entitys;
using Netor.EventHub;

using AIChatMessage = Microsoft.Extensions.AI.ChatMessage;
using ChatRole = Microsoft.Extensions.AI.ChatRole;

namespace Netor.Cortana.AI;

/// <summary>
/// 封装图片/视频生成 turn 的公共初始化、事件元数据与统一收尾。
/// </summary>
public sealed class ChatGenerationTurnCoordinator(
    ChatTurnCancellationRegistry turnRegistry,
    ChatConversationEventPublisher conversationEventPublisher,
    ChatRealtimeEventPublisher realtimeEventPublisher,
    IPublisher publisher)
{
    public ChatGenerationTurnState Start(
        string prompt,
        string generationKind,
        string queuedMessage,
        AIAgent turnAgent,
        AgentSession turnSession,
        AiProviderEntity turnProvider,
        AgentEntity turnAgentEntity,
        AiModelEntity turnModel,
        Action<AIChatMessage, AgentSession, AgentEntity, AiModelEntity> saveUserMessage,
        Func<string, string, string, string, string, AiProviderEntity, AgentEntity, AiModelEntity, AgentOrchestrationResult?, ConversationEventMetadata> createConversationEventMetadata,
        CancellationToken cancellationToken)
    {
        publisher.Publish(Events.OnAiStarted, new VoiceSignalArgs());

        var turnId = Guid.NewGuid().ToString("N");
        var streamCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var traceId = Guid.NewGuid().ToString("N");
        var userMessageId = Guid.NewGuid().ToString("N");
        var assistantMessageId = Guid.NewGuid().ToString("N");
        var currentSessionId = turnSession.StateBag.GetValue<string>("sessionid") ?? string.Empty;
        var metadata = createConversationEventMetadata(
            currentSessionId,
            turnId,
            traceId,
            userMessageId,
            assistantMessageId,
            turnProvider,
            turnAgentEntity,
            turnModel,
            null);
        var turnContext = new ChatTurnContext(turnId, turnAgent, turnSession, streamCts)
        {
            Metadata = metadata
        };
        turnRegistry.Register(turnContext);

        ChatSessionService.ApplySelectionState(turnSession, turnProvider, turnAgentEntity, turnModel);
        turnSession.StateBag.SetValue("turnid", turnId);
        turnSession.StateBag.SetValue("traceid", traceId);
        turnSession.StateBag.SetValue("usermessageid", userMessageId);
        turnSession.StateBag.SetValue("assistantmessageid", assistantMessageId);

        var userMessage = new AIChatMessage
        {
            Role = ChatRole.User,
            CreatedAt = DateTimeOffset.UtcNow,
            Contents = [new TextContent(prompt)],
            AuthorName = "用户",
            MessageId = userMessageId
        };
        saveUserMessage(userMessage, turnSession, turnAgentEntity, turnModel);

        conversationEventPublisher.PublishTurnStarted(metadata, 0, []);
        conversationEventPublisher.PublishUserMessage(metadata, prompt, []);

        return new ChatGenerationTurnState(
            turnId,
            generationKind,
            queuedMessage,
            traceId,
            userMessageId,
            assistantMessageId,
            currentSessionId,
            metadata,
            turnContext,
            streamCts);
    }

    public Task PublishQueuedAsync(ChatGenerationTurnState state, CancellationToken cancellationToken)
    {
        return realtimeEventPublisher.PublishProcessEventAsync(
            state.TurnId,
            state.ProcessId,
            "generation",
            state.GenerationTitle,
            "running",
            state.QueuedMessage,
            null,
            0,
            cancellationToken);
    }

    public Task PublishSucceededAsync(
        ChatGenerationTurnState state,
        string successMessage,
        CancellationToken cancellationToken)
    {
        return realtimeEventPublisher.PublishProcessEventAsync(
            state.TurnId,
            state.ProcessId,
            "generation",
            state.GenerationTitle,
            "success",
            successMessage,
            null,
            0,
            cancellationToken);
    }

    public Task PublishCancelledAsync(ChatGenerationTurnState state)
    {
        return realtimeEventPublisher.PublishProcessEventAsync(
            state.TurnId,
            state.ProcessId,
            "generation",
            state.GenerationTitle,
            "cancelled",
            "用户取消",
            null,
            0,
            CancellationToken.None);
    }

    public Task PublishFailedAsync(ChatGenerationTurnState state, string errorMessage)
    {
        return realtimeEventPublisher.PublishProcessEventAsync(
            state.TurnId,
            state.ProcessId,
            "generation",
            state.GenerationTitle,
            "failed",
            errorMessage,
            null,
            0,
            CancellationToken.None);
    }

    public void CompleteTurn(ChatGenerationTurnState state)
    {
        _ = turnRegistry.ClearCurrentTurn(state.TurnId);
        state.StreamCts.Dispose();
        publisher.Publish(Events.OnAiCompleted, new VoiceSignalArgs());
    }
}
