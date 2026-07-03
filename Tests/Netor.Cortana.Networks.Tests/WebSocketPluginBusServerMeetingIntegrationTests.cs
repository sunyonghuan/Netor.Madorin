using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.ModelCapability;
using Netor.Cortana.Entitys.Services;
using Netor.Cortana.Plugin;
using Netor.EventHub;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class WebSocketPluginBusServerMeetingIntegrationTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private SystemSettingsService _settings = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private AgentService _agents = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private WebSocketPluginBusServerService _server = null!;
    private WebSocketMeetingFeedRelayService _meetingRelay = null!;
    private WebSocketWorkflowFeedRelayService _workflowRelay = null!;

    [TestInitialize]
    public async Task Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-pluginbus-meeting-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _settings = new SystemSettingsService(_db);
        _settings.SetValue("WebSocket.Port", GetFreePort().ToString(System.Globalization.CultureInfo.InvariantCulture));
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _agents = CreateAgentService();
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _server = new WebSocketPluginBusServerService(
            NullLogger<WebSocketPluginBusServerService>.Instance,
            _settings,
            _db,
            _agents,
            new StubModelCapabilityService(),
            _publisher,
            new PluginManifestRegistry(NullLogger<PluginManifestRegistry>.Instance));
        _meetingRelay = new WebSocketMeetingFeedRelayService(
            _server,
            _services.GetRequiredService<ISubscriber>());
        _workflowRelay = new WebSocketWorkflowFeedRelayService(
            _server,
            _services.GetRequiredService<ISubscriber>());

        await _server.StartAsync(CancellationToken.None);
        await _meetingRelay.StartAsync(CancellationToken.None);
        await _workflowRelay.StartAsync(CancellationToken.None);
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        if (_server is not null)
        {
            await _server.StopAsync(CancellationToken.None);
            _server.Dispose();
        }

        _db.Dispose();
        _services.Dispose();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPath);
        DeleteIfExists($"{_dbPath}-shm");
        DeleteIfExists($"{_dbPath}-wal");
    }

    [TestMethod]
    public async Task MeetingHistoryReplay_OverRealWebSocket_ReturnsCompletedMeetings()
    {
        var meetingId = CreateCompletedMeeting("端到端会议");
        using var ws = await ConnectAsync();
        await ReadUntilTypeAsync(ws, "connected");

        await SendAsync(ws, $$"""
            {
              "type": "subscribe",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topics": ["meeting"],
              "capabilities": ["meeting.v1"]
            }
            """);
        await ReadUntilTypeAsync(ws, "subscribed");

        await SendAsync(ws, $$"""
            {
              "type": "request",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topic": "meeting",
              "op": "{{CortanaWsEndpoints.MeetingHistoryReplayOperation}}",
              "requestId": "meeting-replay-1",
              "payload": {
                "sinceTimestamp": 0,
                "batchSize": 100
              }
            }
            """);

        using var batchDoc = JsonDocument.Parse(await ReadUntilOpAsync(ws, CortanaWsEndpoints.MeetingHistoryBatchOperation));
        using var completedDoc = JsonDocument.Parse(await ReadUntilOpAsync(ws, CortanaWsEndpoints.MeetingHistoryCompletedOperation));
        var batch = batchDoc.RootElement;
        var completed = completedDoc.RootElement;
        var items = batch.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, batch.GetProperty("topic").GetString());
        Assert.AreEqual("meeting-replay-1", batch.GetProperty("requestId").GetString());
        Assert.HasCount(1, items);
        Assert.AreEqual(meetingId, items[0].GetProperty("meetingId").GetString());
        Assert.AreEqual("端到端会议", items[0].GetProperty("topic").GetString());
        Assert.AreEqual("端到端总结", items[0].GetProperty("finalSummaryMd").GetString());
        Assert.AreEqual(1, completed.GetProperty("payload").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task MeetingHistoryReplay_OverRealWebSocket_ReturnsUnfinishedMeetingSummaries()
    {
        var meetingId = CreateMeeting("未结束会议");
        var summary = _messages.AppendSummary(meetingId, "阶段性总结：会议记忆接入方向确定。");
        using var ws = await ConnectAsync();
        await ReadUntilTypeAsync(ws, "connected");

        await SendAsync(ws, $$"""
            {
              "type": "subscribe",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topics": ["meeting"],
              "capabilities": ["meeting.v1"]
            }
            """);
        await ReadUntilTypeAsync(ws, "subscribed");

        await SendAsync(ws, $$"""
            {
              "type": "request",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topic": "meeting",
              "op": "{{CortanaWsEndpoints.MeetingHistoryReplayOperation}}",
              "requestId": "meeting-replay-summary-1",
              "payload": {
                "sinceTimestamp": 0,
                "batchSize": 100
              }
            }
            """);

        using var batchDoc = JsonDocument.Parse(await ReadUntilOpAsync(ws, CortanaWsEndpoints.MeetingHistoryBatchOperation));
        var items = batchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, items);
        Assert.AreEqual("summary", items[0].GetProperty("recordKind").GetString());
        Assert.AreEqual(meetingId, items[0].GetProperty("meetingId").GetString());
        Assert.AreEqual(summary.Id, items[0].GetProperty("messageId").GetString());
        Assert.AreEqual("阶段性总结：会议记忆接入方向确定。", items[0].GetProperty("contentMd").GetString());
    }

    [TestMethod]
    public async Task MeetingCompleted_OverRealWebSocket_BroadcastsToMeetingSubscriber()
    {
        using var ws = await ConnectAsync();
        await ReadUntilTypeAsync(ws, "connected");

        await SendAsync(ws, $$"""
            {
              "type": "subscribe",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topics": ["meeting"],
              "capabilities": ["meeting.v1"]
            }
            """);
        await ReadUntilTypeAsync(ws, "subscribed");

        await _publisher.PublishAsync(
            Events.OnMeetingCompleted,
            CreateMeetingCompletedArgs("meeting-live-1", "实时总结"));

        using var doc = JsonDocument.Parse(await ReadUntilEventTypeAsync(ws, "meeting.completed"));
        var root = doc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, root.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.MeetingEventPublishOperation, root.GetProperty("op").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual("meeting-live-1", payload.GetProperty("MeetingId").GetString());
        Assert.AreEqual("实时总结", payload.GetProperty("FinalSummary").GetString());
        Assert.AreEqual("session-live-1", payload.GetProperty("SessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("实时会议", payload.GetProperty("Topic").GetString());
        Assert.AreEqual("agent-host", payload.GetProperty("HostAgentId").GetString());
        Assert.AreEqual("model-1", payload.GetProperty("Model").GetString());
    }

    [TestMethod]
    public async Task MeetingSummaryMessageCompleted_OverRealWebSocket_BroadcastsToMeetingSubscriber()
    {
        using var ws = await ConnectAsync();
        await ReadUntilTypeAsync(ws, "connected");

        await SendAsync(ws, $$"""
            {
              "type": "subscribe",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topics": ["meeting"],
              "capabilities": ["meeting.v1"]
            }
            """);
        await ReadUntilTypeAsync(ws, "subscribed");

        await _publisher.PublishAsync(
            Events.OnMeetingMessageCompleted,
            CreateMeetingMessageCompletedArgs(
                "meeting-live-summary-1",
                "message-live-summary-1",
                "实时阶段总结"));

        using var doc = JsonDocument.Parse(await ReadUntilEventTypeAsync(ws, "meeting.message.completed"));
        var root = doc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, root.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.MeetingEventPublishOperation, root.GetProperty("op").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual("message-live-summary-1", payload.GetProperty("MessageId").GetString());
        Assert.AreEqual("summary", payload.GetProperty("MessageRole").GetString());
        Assert.AreEqual("实时阶段总结", payload.GetProperty("ContentMd").GetString());
        Assert.AreEqual("session-live-1", payload.GetProperty("SessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.AreEqual("实时会议", payload.GetProperty("Topic").GetString());
        Assert.AreEqual("agent-host", payload.GetProperty("HostAgentId").GetString());
        Assert.AreEqual("model-1", payload.GetProperty("Model").GetString());
    }

    [TestMethod]
    public async Task WorkTaskCompleted_OverRealWebSocket_BroadcastsToWorkflowSubscriber()
    {
        using var ws = await ConnectAsync();
        await ReadUntilTypeAsync(ws, "connected");

        await SendAsync(ws, $$"""
            {
              "type": "subscribe",
              "protocol": "{{CortanaWsEndpoints.PluginBusProtocol}}",
              "version": "{{CortanaWsEndpoints.PluginBusVersion}}",
              "topics": ["workflow"],
              "capabilities": ["workflow.v1"]
            }
            """);
        await ReadUntilTypeAsync(ws, "subscribed");

        await _publisher.PublishAsync(
            Events.OnWorkTaskCompleted,
            CreateWorkTaskCompletedArgs("task-live-1", "工作模式最终报告"));

        using var doc = JsonDocument.Parse(await ReadUntilEventTypeAsync(ws, "workflow.task.completed"));
        var root = doc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.WorkflowTopic, root.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.WorkflowEventPublishOperation, root.GetProperty("op").GetString());
        var payload = root.GetProperty("payload");
        Assert.AreEqual("task-live-1", payload.GetProperty("TaskId").GetString());
        Assert.AreEqual("工作模式最终报告", payload.GetProperty("FinalReport").GetString());
        Assert.AreEqual("agent-host", payload.GetProperty("ManagerAgentId").GetString());
        Assert.AreEqual("主持人", payload.GetProperty("ManagerAgentName").GetString());
        Assert.AreEqual("session-live-1", payload.GetProperty("SourceSessionId").GetString());
        Assert.AreEqual("workspace-1", payload.GetProperty("WorkspaceId").GetString());
        Assert.IsTrue(payload.GetProperty("AllowMemoryIngest").GetBoolean());
    }

    private string CreateCompletedMeeting(string topic)
    {
        var meetingId = CreateMeeting(topic);
        _sessions.SetFinalSummary(meetingId, "端到端总结");
        return meetingId;
    }

    private string CreateMeeting(string topic)
    {
        var sessionId = _sessions.CreateBackingChatSession("workspace-1", "agent-host");
        return _sessions.Create(new MeetingSessionEntity
        {
            SessionId = sessionId,
            WorkspaceId = "workspace-1",
            Topic = topic,
            HostAgentId = "agent-host",
            ParticipantsJson = "[]",
            Provider = "provider-1",
            Model = "model-1"
        });
    }

    private static MeetingCompletedArgs CreateMeetingCompletedArgs(string meetingId, string finalSummary)
    {
        return new MeetingCompletedArgs(
            MeetingId: meetingId,
            FinalSummary: finalSummary,
            SessionId: "session-live-1",
            WorkspaceId: "workspace-1",
            Topic: "实时会议",
            HostAgentId: "agent-host",
            Model: "model-1",
            CreatedAt: 100,
            EndedAt: 200);
    }

    private static MeetingMessageCompletedArgs CreateMeetingMessageCompletedArgs(
        string meetingId,
        string messageId,
        string content)
    {
        return new MeetingMessageCompletedArgs(
            MeetingId: meetingId,
            MessageId: messageId,
            SpeakerId: "system-meeting-host",
            SpeakerKind: "host",
            ContentMd: content,
            MessageRole: "summary",
            AwaitingUserReply: false,
            SessionId: "session-live-1",
            WorkspaceId: "workspace-1",
            Topic: "实时会议",
            HostAgentId: "agent-host",
            Model: "model-1",
            CreatedAt: 123);
    }

    private static WorkTaskCompletedArgs CreateWorkTaskCompletedArgs(string taskId, string finalReport)
    {
        return new WorkTaskCompletedArgs(
            TaskId: taskId,
            FinalReport: finalReport,
            ManagerAgentId: "agent-host",
            ManagerAgentName: "主持人",
            SourceSessionId: "session-live-1",
            WorkspaceId: "workspace-1",
            CompletedAt: 123,
            AllowMemoryIngest: true);
    }

    private AgentService CreateAgentService()
    {
        var root = Path.Combine(Path.GetTempPath(), $"cortana-agent-files-{Guid.NewGuid():N}");
        var paths = new TestAppPaths(root);
        var serializer = new AgentManifestSerializer();
        var validator = new AgentManifestValidator();
        var index = new AgentFileIndex(serializer, validator, NullLogger<AgentFileIndex>.Instance);
        Directory.CreateDirectory(paths.UserAgentsDirectory);
        var fileService = new AgentFileService(paths, serializer, validator, index, NullLogger<AgentFileService>.Instance);
        return new AgentService(fileService, _settings);
    }

    private async Task<ClientWebSocket> ConnectAsync()
    {
        var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri(CortanaWsEndpoints.BuildPluginBusEndpoint(_server.Port)), CancellationToken.None);
        return ws;
    }

    private static async Task SendAsync(ClientWebSocket ws, string json)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private static async Task<string> ReadUntilTypeAsync(ClientWebSocket ws, string type)
    {
        return await ReadUntilAsync(ws, root =>
            root.TryGetProperty("type", out var element)
            && string.Equals(element.GetString(), type, StringComparison.Ordinal));
    }

    private static async Task<string> ReadUntilOpAsync(ClientWebSocket ws, string op)
    {
        return await ReadUntilAsync(ws, root =>
            root.TryGetProperty("op", out var element)
            && string.Equals(element.GetString(), op, StringComparison.Ordinal));
    }

    private static async Task<string> ReadUntilEventTypeAsync(ClientWebSocket ws, string eventType)
    {
        return await ReadUntilAsync(ws, root =>
            root.TryGetProperty("eventType", out var element)
            && string.Equals(element.GetString(), eventType, StringComparison.Ordinal));
    }

    private static async Task<string> ReadUntilAsync(ClientWebSocket ws, Func<JsonElement, bool> predicate)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!cts.IsCancellationRequested)
        {
            var message = await ReadAsync(ws, cts.Token);
            using var doc = JsonDocument.Parse(message);
            if (predicate(doc.RootElement))
            {
                return message;
            }
        }

        throw new TimeoutException("等待 PluginBus WebSocket 消息超时。");
    }

    private static async Task<string> ReadAsync(ClientWebSocket ws, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new WebSocketException("WebSocket closed.");
            }

            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return Encoding.UTF8.GetString(stream.ToArray());
            }
        }
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private sealed class StubModelCapabilityService : IPluginModelCapabilityService
    {
        public Task<ModelCapabilityResponse> InvokeAsync(ModelCapabilityRequest request, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new ModelCapabilityResponse
            {
                RequestId = request.RequestId,
                Content = string.Empty
            });
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory => Path.Combine(root, "workspace");

        public string UserDataDirectory => Path.Combine(root, "data");

        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

        public string PluginDirectory => UserPluginsDirectory;

        public string WorkspaceResourcesDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "resources");

        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");

        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
