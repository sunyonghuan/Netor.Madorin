using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class WebSocketMeetingFeedRelayServiceTests
{
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private CapturingPluginBusBroadcaster _broadcaster = null!;
    private WebSocketMeetingFeedRelayService _relay = null!;

    [TestInitialize]
    public void Setup()
    {
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _broadcaster = new CapturingPluginBusBroadcaster();
        _relay = new WebSocketMeetingFeedRelayService(
            _broadcaster,
            _services.GetRequiredService<ISubscriber>());
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
    }

    [TestMethod]
    public async Task MeetingCreated_BroadcastsParticipants()
    {
        await _relay.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnMeetingCreated,
            new MeetingCreatedArgs(
                "meeting-1",
                "session-1",
                "架构讨论",
                [new MeetingParticipantDto("agent-1", "架构师", 0)]));

        var published = await _broadcaster.WaitForMessageAsync();
        using var doc = JsonDocument.Parse(published.Message);
        var root = doc.RootElement;
        var payload = root.GetProperty("payload");

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, published.Topic);
        Assert.AreEqual("meeting.created", root.GetProperty("eventType").GetString());
        Assert.AreEqual("meeting-1", payload.GetProperty("MeetingId").GetString());
        Assert.AreEqual("架构讨论", payload.GetProperty("Topic").GetString());
        var participants = payload.GetProperty("Participants").EnumerateArray().ToArray();
        Assert.HasCount(1, participants);
        Assert.AreEqual("agent-1", participants[0].GetProperty("AgentId").GetString());
    }

    [TestMethod]
    public async Task MeetingCompleted_BroadcastsMeetingCompletedEvent()
    {
        await _relay.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnMeetingCompleted,
            new MeetingCompletedArgs(
                MeetingId: "meeting-1",
                FinalSummary: "最终总结",
                SessionId: "session-1",
                WorkspaceId: "workspace-1",
                Topic: "架构讨论",
                HostAgentId: "system-meeting-host",
                Model: "model-1",
                CreatedAt: 100,
                EndedAt: 200));

        var published = await _broadcaster.WaitForMessageAsync();
        using var doc = JsonDocument.Parse(published.Message);
        var root = doc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, published.Topic);
        Assert.AreEqual("event", root.GetProperty("type").GetString());
        Assert.AreEqual(CortanaWsEndpoints.PluginBusProtocol, root.GetProperty("protocol").GetString());
        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, root.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.MeetingEventPublishOperation, root.GetProperty("op").GetString());
        Assert.AreEqual("meeting.completed", root.GetProperty("eventType").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual("meeting-1", payload.GetProperty("MeetingId").GetString());
        Assert.AreEqual("最终总结", payload.GetProperty("FinalSummary").GetString());
        Assert.AreEqual("session-1", payload.GetProperty("SessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("架构讨论", payload.GetProperty("Topic").GetString());
        Assert.AreEqual("system-meeting-host", payload.GetProperty("HostAgentId").GetString());
        Assert.AreEqual("model-1", payload.GetProperty("Model").GetString());
        Assert.AreEqual(100, payload.GetProperty("CreatedAt").GetInt64());
        Assert.AreEqual(200, payload.GetProperty("EndedAt").GetInt64());
    }

    [TestMethod]
    public async Task MeetingMessageCompleted_WithSummaryRole_BroadcastsMeetingMessageCompletedEvent()
    {
        await _relay.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnMeetingMessageCompleted,
            new MeetingMessageCompletedArgs(
                MeetingId: "meeting-1",
                MessageId: "message-summary-1",
                SpeakerId: "system-meeting-host",
                SpeakerKind: "host",
                ContentMd: "阶段总结",
                MessageRole: "summary",
                AwaitingUserReply: false,
                SessionId: "session-1",
                WorkspaceId: "workspace-1",
                Topic: "架构讨论",
                HostAgentId: "system-meeting-host",
                Model: "model-1",
                CreatedAt: 123));

        var published = await _broadcaster.WaitForMessageAsync();
        using var doc = JsonDocument.Parse(published.Message);
        var root = doc.RootElement;
        var payload = root.GetProperty("payload");

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, published.Topic);
        Assert.AreEqual(CortanaWsEndpoints.MeetingEventPublishOperation, root.GetProperty("op").GetString());
        Assert.AreEqual("meeting.message.completed", root.GetProperty("eventType").GetString());
        Assert.AreEqual("message-summary-1", payload.GetProperty("MessageId").GetString());
        Assert.AreEqual("summary", payload.GetProperty("MessageRole").GetString());
        Assert.AreEqual("阶段总结", payload.GetProperty("ContentMd").GetString());
        Assert.AreEqual("session-1", payload.GetProperty("SessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("架构讨论", payload.GetProperty("Topic").GetString());
        Assert.AreEqual("system-meeting-host", payload.GetProperty("HostAgentId").GetString());
        Assert.AreEqual("model-1", payload.GetProperty("Model").GetString());
        Assert.AreEqual(123, payload.GetProperty("CreatedAt").GetInt64());
    }

    [TestMethod]
    public async Task MeetingMessageCompleted_WithNormalRole_DoesNotBroadcast()
    {
        await _relay.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnMeetingMessageCompleted,
            new MeetingMessageCompletedArgs(
                MeetingId: "meeting-1",
                MessageId: "message-normal-1",
                SpeakerId: "agent-1",
                SpeakerKind: "agent",
                ContentMd: "普通发言",
                MessageRole: "normal",
                AwaitingUserReply: false,
                SessionId: "session-1",
                WorkspaceId: "workspace-1",
                Topic: "架构讨论",
                HostAgentId: "system-meeting-host",
                Model: "model-1",
                CreatedAt: 123));

        await Assert.ThrowsExactlyAsync<TimeoutException>(() => _broadcaster.WaitForMessageAsync());
    }

    [TestMethod]
    public void CreateConnected_IncludesMeetingAndWorkflowTopics()
    {
        using var doc = JsonDocument.Parse(PluginBusMessageFactory.CreateConnected("client-1"));
        var topics = doc.RootElement.GetProperty("topics")
            .EnumerateArray()
            .Select(static x => x.GetString())
            .ToArray();

        CollectionAssert.Contains(topics, CortanaWsEndpoints.WorkflowTopic);
        CollectionAssert.Contains(topics, CortanaWsEndpoints.MeetingTopic);
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
