using System.Text.Json;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部工作区变更事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// </summary>
public sealed class WebSocketWorkspaceRelayService(
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
        subscriber.Subscribe<WorkspaceChangedArgs>(Events.OnWorkspaceChanged, async (_, args) =>
        {
            await BroadcastWorkspaceEventAsync(Events.OnWorkspaceChanged.Eventid, args);
            return false;
        });
    }

    private Task BroadcastWorkspaceEventAsync(string eventType, WorkspaceChangedArgs args)
    {
        var payload = JsonSerializer.SerializeToElement(args, WebSocketJsonContext.Default.WorkspaceChangedArgs);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "event",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.WorkspaceTopic,
            Op = CortanaWsEndpoints.WorkspaceEventPublishOperation,
            Source = "host",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = eventType,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return server.BroadcastPluginBusAsync(CortanaWsEndpoints.WorkspaceTopic, message);
    }
}
