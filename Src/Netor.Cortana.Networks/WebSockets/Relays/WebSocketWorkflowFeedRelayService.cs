using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// 订阅宿主内部 Workflow 事件，并通过内部 PluginBus 转发给插件侧订阅者（阶段 2B 起）。
/// 与 <see cref="WebSocketConversationFeedRelayService"/> 同模式，但走独立的 workflow topic。
/// 详见 docs/未来版本策划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.4。
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
        subscriber.Subscribe<WorkflowTaskStartedArgs>(Events.OnWorkflowTaskStarted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkflowTaskStarted.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowTaskStartedArgs);
            return false;
        });

        subscriber.Subscribe<WorkflowStepCompletedArgs>(Events.OnWorkflowStepCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkflowStepCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowStepCompletedArgs);
            return false;
        });

        subscriber.Subscribe<WorkflowTaskCompletedArgs>(Events.OnWorkflowTaskCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkflowTaskCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowTaskCompletedArgs);
            return false;
        });

        subscriber.Subscribe<WorkflowTaskFailedArgs>(Events.OnWorkflowTaskFailed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkflowTaskFailed.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowTaskFailedArgs);
            return false;
        });

        subscriber.Subscribe<WorkflowTaskTitleUpdatedArgs>(Events.OnWorkflowTaskTitleUpdated, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnWorkflowTaskTitleUpdated.Eventid,
                args,
                WebSocketJsonContext.Default.WorkflowTaskTitleUpdatedArgs);
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
