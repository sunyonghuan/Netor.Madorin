using System.Text.Json;

using Microsoft.Extensions.Hosting;
using System.Text.Json.Serialization.Metadata;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部 Conversation 事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// </summary>
public sealed class WebSocketConversationFeedRelayService(
    IPluginBusBroadcaster server,
    ISubscriber subscriber) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        SubscribeEvents();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void SubscribeEvents()
    {
        subscriber.Subscribe<ConversationTurnStartedArgs>(Events.OnConversationTurnStarted, async (_, args) =>
        {
            await BroadcastConversationEventAsync(
                Events.OnConversationTurnStarted.Eventid,
                args,
                WebSocketJsonContext.Default.ConversationTurnStartedArgs);
            return false;
        });

        subscriber.Subscribe<ConversationUserMessageArgs>(Events.OnConversationUserMessage, async (_, args) =>
        {
            await BroadcastConversationEventAsync(
                Events.OnConversationUserMessage.Eventid,
                args,
                WebSocketJsonContext.Default.ConversationUserMessageArgs);
            return false;
        });

        subscriber.Subscribe<ConversationAssistantDeltaArgs>(Events.OnConversationAssistantDelta, async (_, args) =>
        {
            await BroadcastConversationEventAsync(
                Events.OnConversationAssistantDelta.Eventid,
                args,
                WebSocketJsonContext.Default.ConversationAssistantDeltaArgs);
            return false;
        });

        subscriber.Subscribe<ConversationTurnCompletedArgs>(Events.OnConversationTurnCompleted, async (_, args) =>
        {
            await BroadcastConversationEventAsync(
                Events.OnConversationTurnCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.ConversationTurnCompletedArgs);
            return false;
        });

        // 阶段 5B Phase 3 新增：Chat→Workflow 启发式建议转发（决策：走 conversation topic，不是 workflow topic）
        // 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3。
        subscriber.Subscribe<WorkflowSuggestionArgs>(Events.OnWorkflowSuggestion, async (_, args) =>
        {
            await BroadcastConversationEventAsync(
                Events.OnWorkflowSuggestion.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowSuggestionArgs);
            return false;
        });
    }

    private Task BroadcastConversationEventAsync<TArgs>(
        string eventType,
        TArgs args,
        JsonTypeInfo<TArgs> jsonTypeInfo)
    {
        var payload = JsonSerializer.SerializeToElement(args, jsonTypeInfo);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "event",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.ConversationTopic,
            Op = CortanaWsEndpoints.ConversationEventPublishOperation,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = eventType,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return server.BroadcastPluginBusAsync(CortanaWsEndpoints.ConversationTopic, message);
    }
}
