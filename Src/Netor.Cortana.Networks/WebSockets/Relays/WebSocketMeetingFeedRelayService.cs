using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部 Meeting 事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// </summary>
public sealed class WebSocketMeetingFeedRelayService(
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
        subscriber.Subscribe<MeetingCreatedArgs>(Events.OnMeetingCreated, async (_, args) =>
        {
            await BroadcastMeetingEventAsync(
                Events.OnMeetingCreated.Eventid,
                args,
                WebSocketJsonContext.Default.MeetingCreatedArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<MeetingCompletedArgs>(Events.OnMeetingCompleted, async (_, args) =>
        {
            await BroadcastMeetingEventAsync(
                Events.OnMeetingCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.MeetingCompletedArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<MeetingMessageCompletedArgs>(Events.OnMeetingMessageCompleted, async (_, args) =>
        {
            if (!string.Equals(args.MessageRole, "summary", StringComparison.Ordinal))
            {
                return false;
            }

            await BroadcastMeetingEventAsync(
                Events.OnMeetingMessageCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.MeetingMessageCompletedArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<MeetingCancelledArgs>(Events.OnMeetingCancelled, async (_, args) =>
        {
            await BroadcastMeetingEventAsync(
                Events.OnMeetingCancelled.Eventid,
                args,
                WebSocketJsonContext.Default.MeetingCancelledArgs).ConfigureAwait(false);
            return false;
        });
    }

    private Task BroadcastMeetingEventAsync<TArgs>(
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
            Topic = CortanaWsEndpoints.MeetingTopic,
            Op = CortanaWsEndpoints.MeetingEventPublishOperation,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = eventType,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return server.BroadcastPluginBusAsync(CortanaWsEndpoints.MeetingTopic, message);
    }
}
