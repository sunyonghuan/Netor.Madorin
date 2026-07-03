using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部 Workflow 事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// </summary>
public sealed class WebSocketWorkflowFeedRelayService(
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
        subscriber.Subscribe<WorkTaskCompletedArgs>(Events.OnWorkTaskCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                "workflow.task.completed",
                args,
                WebSocketJsonContext.Default.WorkTaskCompletedArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<WorkTaskFailedArgs>(Events.OnWorkTaskFailed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                "workflow.task.failed",
                args,
                WebSocketJsonContext.Default.WorkTaskFailedArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<WorkTaskCancelledArgs>(Events.OnWorkTaskCancelled, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                "workflow.task.cancelled",
                args,
                WebSocketJsonContext.Default.WorkTaskCancelledArgs).ConfigureAwait(false);
            return false;
        });

        subscriber.Subscribe<WorkTaskTitleUpdatedArgs>(Events.OnWorkTaskTitleUpdated, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                "workflow.task.title.updated",
                args,
                WebSocketJsonContext.Default.WorkTaskTitleUpdatedArgs).ConfigureAwait(false);
            return false;
        });
    }

    private Task BroadcastWorkflowEventAsync<TArgs>(
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
            Topic = CortanaWsEndpoints.WorkflowTopic,
            Op = CortanaWsEndpoints.WorkflowEventPublishOperation,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = eventType,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return server.BroadcastPluginBusAsync(CortanaWsEndpoints.WorkflowTopic, message);
    }
}
