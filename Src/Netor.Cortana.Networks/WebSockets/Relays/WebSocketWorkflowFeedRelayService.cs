using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Hosting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// P4 重写：订阅 TaskExecutionEngine P4 事件，并通过内部 PluginBus 转发给插件侧订阅者。
/// 与 <see cref="WebSocketConversationFeedRelayService"/> 同模式，但走独立的 workflow topic。
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
        // P4: 订阅 P4 任务引擎事件，转发给插件侧

        subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseStarted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskPhaseStarted.Eventid,
                args,
                WebSocketJsonContext.Default.TaskPhaseEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskPhaseEventArgs>(Events.OnTaskPhaseCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskPhaseCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.TaskPhaseEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanCreated, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskPlanCreated.Eventid,
                args,
                WebSocketJsonContext.Default.TaskPlanEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskPlanEventArgs>(Events.OnTaskPlanConfirmed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskPlanConfirmed.Eventid,
                args,
                WebSocketJsonContext.Default.TaskPlanEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepStarted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskStepStarted.Eventid,
                args,
                WebSocketJsonContext.Default.TaskStepEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskStepCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.TaskStepEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskStepEventArgs>(Events.OnTaskStepFailed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskStepFailed.Eventid,
                args,
                WebSocketJsonContext.Default.TaskStepEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineCompleted, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskEngineCompleted.Eventid,
                args,
                WebSocketJsonContext.Default.TaskLifecycleEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineFailed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskEngineFailed.Eventid,
                args,
                WebSocketJsonContext.Default.TaskLifecycleEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEnginePaused, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskEnginePaused.Eventid,
                args,
                WebSocketJsonContext.Default.TaskLifecycleEventArgs);
            return false;
        });

        subscriber.Subscribe<TaskLifecycleEventArgs>(Events.OnTaskEngineResumed, async (_, args) =>
        {
            await BroadcastWorkflowEventAsync(
                Events.OnTaskEngineResumed.Eventid,
                args,
                WebSocketJsonContext.Default.TaskLifecycleEventArgs);
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
