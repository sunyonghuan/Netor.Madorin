using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class WebSocketWorkflowFeedRelayServiceTests
{
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private CapturingPluginBusBroadcaster _broadcaster = null!;
    private WebSocketWorkflowFeedRelayService _relay = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _broadcaster = new CapturingPluginBusBroadcaster();
        _relay = new WebSocketWorkflowFeedRelayService(
            _broadcaster,
            _services.GetRequiredService<ISubscriber>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
    }

    [TestMethod]
    public async Task WorkTaskCompleted_BroadcastsWorkflowTaskCompletedEvent()
    {
        await _relay.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnWorkTaskCompleted,
            new WorkTaskCompletedArgs(
                TaskId: "task-1",
                FinalReport: "最终报告",
                ManagerAgentId: "agent-1",
                ManagerAgentName: "测试智能体",
                SourceSessionId: "session-1",
                WorkspaceId: "workspace-1",
                CompletedAt: 123,
                AllowMemoryIngest: true));

        var published = await _broadcaster.WaitForMessageAsync();
        using var doc = JsonDocument.Parse(published.Message);
        var root = doc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.WorkflowTopic, published.Topic);
        Assert.AreEqual("event", root.GetProperty("type").GetString());
        Assert.AreEqual(CortanaWsEndpoints.PluginBusProtocol, root.GetProperty("protocol").GetString());
        Assert.AreEqual(CortanaWsEndpoints.WorkflowTopic, root.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.WorkflowEventPublishOperation, root.GetProperty("op").GetString());
        Assert.AreEqual("workflow.task.completed", root.GetProperty("eventType").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual("task-1", payload.GetProperty("TaskId").GetString());
        Assert.AreEqual("最终报告", payload.GetProperty("FinalReport").GetString());
        Assert.AreEqual("agent-1", payload.GetProperty("ManagerAgentId").GetString());
        Assert.AreEqual("测试智能体", payload.GetProperty("ManagerAgentName").GetString());
        Assert.AreEqual("session-1", payload.GetProperty("SourceSessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual(123, payload.GetProperty("CompletedAt").GetInt64());
        Assert.IsTrue(payload.GetProperty("AllowMemoryIngest").GetBoolean());
    }

    private sealed class CapturingPluginBusBroadcaster : IPluginBusBroadcaster
    {
        private readonly TaskCompletionSource<(string Topic, string Message)> _message = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public Task BroadcastPluginBusAsync(string topic, string message, CancellationToken cancellationToken = default)
        {
            _message.TrySetResult((topic, message));
            return Task.CompletedTask;
        }

        public Task<(string Topic, string Message)> WaitForMessageAsync()
        {
            return _message.Task.WaitAsync(TimeSpan.FromSeconds(1));
        }
    }
}
