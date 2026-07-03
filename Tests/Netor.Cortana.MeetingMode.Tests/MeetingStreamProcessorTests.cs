using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingStreamProcessorTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private ISubscriber _subscriber = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-stream-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _subscriber = _services.GetRequiredService<ISubscriber>();
    }

    [TestCleanup]
    public void Cleanup()
    {
        _services.Dispose();
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPath);
        DeleteIfExists($"{_dbPath}-shm");
        DeleteIfExists($"{_dbPath}-wal");
    }

    [TestMethod]
    public async Task ProcessEventAsync_TextDeltas_AppendCompletedMessage()
    {
        const string meetingId = "meeting-stream-text";
        CreateMeeting(meetingId);
        var deltas = new List<MeetingMessageDeltaArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        Subscribe(deltas);
        Subscribe(completed);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent("agent-a", "response-1", [new TextContent("第一段")]));
        await processor.ProcessEventAsync(CreateUpdateEvent("agent-a", "response-1", [new TextContent("第二段")]));
        await processor.ProcessEventAsync(CreateResponseEvent("agent-a", "response-1"));

        var message = AssertSingleMessage(meetingId);
        Assert.AreEqual("agent", message.SpeakerKind);
        Assert.AreEqual("agent-a", message.SpeakerId);
        Assert.AreEqual("第一段第二段", message.ContentMd);
        Assert.IsNull(message.ToolCallsJson);
        Assert.HasCount(2, deltas);
        Assert.AreEqual("专家A", deltas[0].SpeakerName);
        Assert.HasCount(1, completed);
        Assert.AreEqual(message.Id, completed[0].MessageId);
        Assert.AreEqual("专家A", completed[0].SpeakerName);
        Assert.AreEqual("normal", completed[0].MessageRole);
    }

    [TestMethod]
    public async Task ProcessEventAsync_TextDelta_PersistsRecoverablePartialBeforeCompletion()
    {
        const string meetingId = "meeting-stream-draft";
        CreateMeeting(meetingId);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent("agent-a", "response-draft", [new TextContent("进行中的观点")]));

        var draft = AssertSingleMessage(meetingId);
        Assert.AreEqual("agent", draft.SpeakerKind);
        Assert.AreEqual("agent-a", draft.SpeakerId);
        Assert.AreEqual("进行中的观点", draft.ContentMd);
        Assert.IsTrue(draft.IsPartial);

        await processor.ProcessEventAsync(CreateResponseEvent("agent-a", "response-draft"));

        var completed = AssertSingleMessage(meetingId);
        Assert.AreEqual(draft.Id, completed.Id);
        Assert.AreEqual("进行中的观点", completed.ContentMd);
        Assert.IsFalse(completed.IsPartial);
        Assert.IsNull(completed.ErrorMessage);
    }

    [TestMethod]
    public async Task ProcessEventAsync_ExecutorCompleted_CompletesRecoverablePartial()
    {
        const string meetingId = "meeting-stream-executor-completed";
        CreateMeeting(meetingId);
        var completedEvents = new List<MeetingMessageCompletedArgs>();
        Subscribe(completedEvents);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent("agent-a", "response-draft", [new TextContent("执行器完成时应定稿")]));

        var draft = AssertSingleMessage(meetingId);
        Assert.IsTrue(draft.IsPartial);

        await processor.ProcessEventAsync(new ExecutorCompletedEvent("agent-a", null!));

        var completed = AssertSingleMessage(meetingId);
        Assert.AreEqual(draft.Id, completed.Id);
        Assert.AreEqual("执行器完成时应定稿", completed.ContentMd);
        Assert.IsFalse(completed.IsPartial);
        Assert.IsNull(completed.ErrorMessage);
        Assert.HasCount(1, completedEvents);
        Assert.AreEqual(completed.Id, completedEvents[0].MessageId);
    }

    [TestMethod]
    public async Task ProcessEventAsync_ThinkingAndRegularTool_PublishesEventsAndPersistsToolRecord()
    {
        const string meetingId = "meeting-stream-tool";
        CreateMeeting(meetingId);
        var thinking = new List<MeetingThinkingDeltaArgs>();
        var toolCalls = new List<MeetingToolCallArgs>();
        var toolResults = new List<MeetingToolResultArgs>();
        Subscribe(thinking);
        Subscribe(toolCalls);
        Subscribe(toolResults);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "agent-a",
            "response-tool",
            [
                new TextReasoningContent("正在分析"),
                new FunctionCallContent("call-1", "file_read", new Dictionary<string, object?> { ["path"] = "README.md" }),
                new FunctionResultContent("call-1", "文件内容"),
                new TextContent("结论")
            ]));
        await processor.ProcessEventAsync(CreateResponseEvent("agent-a", "response-tool"));

        var message = AssertSingleMessage(meetingId);
        Assert.AreEqual("正在分析", message.ThinkingMd);
        Assert.AreEqual("结论", message.ContentMd);
        Assert.IsNotNull(message.ToolCallsJson);
        var records = JsonSerializer.Deserialize(
            message.ToolCallsJson,
            MeetingJsonContext.Default.ListMeetingToolCallRecord);
        Assert.IsNotNull(records);
        Assert.HasCount(1, records);
        Assert.AreEqual("file_read", records[0].Name);
        Assert.AreEqual("文件内容", records[0].ResultText);
        Assert.HasCount(1, thinking);
        Assert.HasCount(1, toolCalls);
        Assert.HasCount(1, toolResults);
    }

    [TestMethod]
    public async Task ProcessEventAsync_MeetingControlTool_SuppressesToolEventsAndSkipsToolOnlyMessage()
    {
        const string meetingId = "meeting-stream-suppressed";
        CreateMeeting(meetingId);
        var toolCalls = new List<MeetingToolCallArgs>();
        var toolResults = new List<MeetingToolResultArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        Subscribe(toolCalls);
        Subscribe(toolResults);
        Subscribe(completed);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("system-meeting-host", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "system-meeting-host",
            "response-suppressed",
            [
                new FunctionCallContent("call-check", "check_pending_user_input", new Dictionary<string, object?>()),
                new FunctionResultContent("call-check", "没有未处理的用户插话，可以继续。")
            ]));
        await processor.ProcessEventAsync(CreateResponseEvent("system-meeting-host", "response-suppressed"));

        Assert.HasCount(0, toolCalls);
        Assert.HasCount(0, toolResults);
        Assert.HasCount(0, completed);
        Assert.HasCount(0, _messages.ListByMeeting(meetingId));
    }

    [TestMethod]
    public async Task ProcessEventAsync_ThinkingOnlyResponse_DiscardsInternalMessage()
    {
        const string meetingId = "meeting-stream-thinking-only";
        CreateMeeting(meetingId);
        var thinking = new List<MeetingThinkingDeltaArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        var discarded = new List<MeetingMessageDiscardedArgs>();
        Subscribe(thinking);
        Subscribe(completed);
        Subscribe(discarded);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "agent-a",
            "response-thinking-only",
            [new TextReasoningContent("内部推理，不应作为正式发言。")]));
        await processor.ProcessEventAsync(CreateResponseEvent("agent-a", "response-thinking-only"));

        Assert.HasCount(1, thinking);
        Assert.HasCount(1, discarded);
        Assert.HasCount(0, completed);
        Assert.HasCount(0, _messages.ListByMeeting(meetingId));
    }

    [TestMethod]
    public async Task ProcessEventAsync_EmptyResponseId_ReusesActiveAccumulatorForSameSpeaker()
    {
        const string meetingId = "meeting-stream-empty-response-id";
        CreateMeeting(meetingId);
        var thinking = new List<MeetingThinkingDeltaArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        Subscribe(thinking);
        Subscribe(completed);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "agent-a",
            string.Empty,
            [new TextReasoningContent("先分析")]));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "agent-a",
            string.Empty,
            [new TextContent("正式结论")]));
        await processor.ProcessEventAsync(CreateResponseEvent("agent-a", string.Empty));

        var message = AssertSingleMessage(meetingId);
        Assert.AreEqual("先分析", message.ThinkingMd);
        Assert.AreEqual("正式结论", message.ContentMd);
        Assert.HasCount(1, thinking);
        Assert.HasCount(1, completed);
        Assert.AreEqual(thinking[0].MessageId, completed[0].MessageId);
        Assert.AreEqual(message.Id, completed[0].MessageId);
    }

    [TestMethod]
    public async Task ProcessEventAsync_AskUserTool_PublishesQuestionDeltaAndInquiryCompletion()
    {
        const string meetingId = "meeting-stream-ask-user";
        CreateMeeting(meetingId);
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");
        var deltas = new List<MeetingMessageDeltaArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        Subscribe(deltas);
        Subscribe(completed);
        var processor = new MeetingStreamProcessor(
            meetingId,
            [CreateAgent("system-meeting-host", "主持人"), CreateAgent("agent-a", "专家A")],
            _sessions,
            _messages,
            CreateUninitialized<MeetingCompactionService>(),
            _publisher,
            NullLogger<MeetingStreamProcessor>.Instance);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("system-meeting-host", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent(
            "system-meeting-host",
            "response-ask-user",
            [
                new FunctionCallContent(
                    "call-ask",
                    "ask_user",
                    new Dictionary<string, object?> { ["question"] = "老板，您希望先聚焦哪个市场？" })
            ]));
        await processor.ProcessEventAsync(CreateResponseEvent("system-meeting-host", "response-ask-user"));

        var message = AssertSingleMessage(meetingId);
        Assert.AreEqual("host", message.SpeakerKind);
        Assert.AreEqual("inquiry", message.MessageRole);
        Assert.AreEqual("老板，您希望先聚焦哪个市场？", message.ContentMd);
        Assert.IsTrue(message.AwaitingUserReply);
        Assert.HasCount(1, deltas);
        Assert.AreEqual(message.Id, completed[0].MessageId);
        Assert.AreEqual("inquiry", completed[0].MessageRole);
        Assert.IsTrue(completed[0].AwaitingUserReply);
    }

    [TestMethod]
    public async Task ProcessEventAsync_FinalResponseAskUserTool_PublishesQuestionDeltaAndInquiryCompletion()
    {
        const string meetingId = "meeting-stream-final-ask-user";
        CreateMeeting(meetingId);
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");
        var deltas = new List<MeetingMessageDeltaArgs>();
        Subscribe(deltas);
        var processor = new MeetingStreamProcessor(
            meetingId,
            [CreateAgent("system-meeting-host", "主持人"), CreateAgent("agent-a", "专家A")],
            _sessions,
            _messages,
            CreateUninitialized<MeetingCompactionService>(),
            _publisher,
            NullLogger<MeetingStreamProcessor>.Instance);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("system-meeting-host", null!));
        await processor.ProcessEventAsync(CreateResponseEvent(
            "system-meeting-host",
            "response-final-ask-user",
            [
                new FunctionCallContent(
                    "call-ask",
                    "ask_user",
                    new Dictionary<string, object?> { ["question"] = "老板，是否需要先确认预算范围？" })
            ]));

        var message = AssertSingleMessage(meetingId);
        Assert.AreEqual("host", message.SpeakerKind);
        Assert.AreEqual("inquiry", message.MessageRole);
        Assert.AreEqual("老板，是否需要先确认预算范围？", message.ContentMd);
        Assert.IsTrue(message.AwaitingUserReply);
        Assert.HasCount(1, deltas);
    }

    [TestMethod]
    public async Task ProcessEventAsync_HostInWorkflowParticipants_PublishesHostSpeakerKind()
    {
        const string meetingId = "meeting-stream-host-kind";
        CreateMeeting(meetingId);
        var speakers = new List<MeetingSpeakerChangedArgs>();
        Subscribe(speakers);
        var processor = new MeetingStreamProcessor(
            meetingId,
            [CreateAgent("system-meeting-host", "主持人"), CreateAgent("agent-a", "专家A")],
            _sessions,
            _messages,
            CreateUninitialized<MeetingCompactionService>(),
            _publisher,
            NullLogger<MeetingStreamProcessor>.Instance);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("system-meeting-host", null!));

        Assert.HasCount(1, speakers);
        Assert.AreEqual("system-meeting-host", speakers[0].SpeakerId);
        Assert.AreEqual("host", speakers[0].SpeakerKind);
    }

    [TestMethod]
    public async Task SavePartialsAsync_PersistsUnfinishedText_AndPublishesPartialSaved()
    {
        const string meetingId = "meeting-stream-partial";
        CreateMeeting(meetingId);
        var partials = new List<MeetingMessagePartialSavedArgs>();
        Subscribe(partials);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new ExecutorInvokedEvent("agent-a", null!));
        await processor.ProcessEventAsync(CreateUpdateEvent("agent-a", "response-partial", [new TextContent("半截内容")]));
        await processor.SavePartialsAsync(new TimeoutException("网络超时"));

        var message = AssertSingleMessage(meetingId);
        Assert.IsTrue(message.IsPartial);
        Assert.AreEqual("半截内容", message.ContentMd);
        Assert.AreEqual("网络超时", message.ErrorMessage);
        Assert.HasCount(1, partials);
        Assert.AreEqual(message.Id, partials[0].MessageId);
        Assert.AreEqual("半截内容", partials[0].PartialContent);
    }

    [TestMethod]
    public async Task WorkflowOutputEvent_PublishesFinalSummaryFromSessionBeforeOutputData()
    {
        const string meetingId = "meeting-stream-output";
        CreateMeeting(meetingId);
        _sessions.SetFinalSummary(meetingId, "数据库最终总结");
        var completed = new List<MeetingCompletedArgs>();
        Subscribe(completed);
        var processor = CreateProcessor(meetingId);

        await processor.ProcessEventAsync(new WorkflowOutputEvent("输出事件总结", "system-meeting-host"));

        Assert.HasCount(1, completed);
        Assert.AreEqual(meetingId, completed[0].MeetingId);
        Assert.AreEqual("数据库最终总结", completed[0].FinalSummary);
    }

    private MeetingStreamProcessor CreateProcessor(string meetingId)
    {
        return new MeetingStreamProcessor(
            meetingId,
            [CreateAgent("agent-a", "专家A")],
            _sessions,
            _messages,
            CreateUninitialized<MeetingCompactionService>(),
            _publisher,
            NullLogger<MeetingStreamProcessor>.Instance);
    }

    private static AgentResponseUpdateEvent CreateUpdateEvent(
        string agentId,
        string responseId,
        IList<AIContent> contents)
    {
        return new AgentResponseUpdateEvent(
            agentId,
            new AgentResponseUpdate(ChatRole.Assistant, contents)
            {
                AgentId = agentId,
                ResponseId = responseId,
            });
    }

    private static AgentResponseEvent CreateResponseEvent(string agentId, string responseId, string text = "")
    {
        return new AgentResponseEvent(
            agentId,
            new AgentResponse(new ChatMessage(ChatRole.Assistant, text))
            {
                AgentId = agentId,
                ResponseId = responseId,
            });
    }

    private static AgentResponseEvent CreateResponseEvent(
        string agentId,
        string responseId,
        IList<AIContent> contents)
    {
        return new AgentResponseEvent(
            agentId,
            new AgentResponse(new ChatMessage(ChatRole.Assistant, contents))
            {
                AgentId = agentId,
                ResponseId = responseId,
            });
    }

    private static AIAgent CreateAgent(string id, string name)
    {
        return new TestAgent(id, name);
    }

    private MeetingMessageEntity AssertSingleMessage(string meetingId)
    {
        var messages = _messages.ListByMeeting(meetingId);
        Assert.HasCount(1, messages);
        return messages[0];
    }

    private void CreateMeeting(string meetingId)
    {
        const string sessionId = "session-stream";
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT OR IGNORE INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary,
                RawDiscription, AgentName, LastActiveTimestamp
            ) VALUES (
                @Id, @Now, @Now, '', '测试会话', '', '', '', @Now
            )
            """,
            command =>
            {
                command.Parameters.AddWithValue("@Id", sessionId);
                command.Parameters.AddWithValue("@Now", now);
            });

        _sessions.Create(new MeetingSessionEntity
        {
            Id = meetingId,
            SessionId = sessionId,
            WorkspaceId = "workspace-stream",
            Topic = "流式处理器测试",
            ParticipantsJson = "[]",
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private void Subscribe<T>(List<T> sink)
        where T : IEventArgs
    {
        _subscriber.Subscribe<T>(
            ResolveEventId<T>(),
            (_, args) =>
            {
                sink.Add(args);
                return Task.FromResult(false);
            });
    }

    private static EventID<T> ResolveEventId<T>()
        where T : IEventArgs
    {
        if (typeof(T) == typeof(MeetingMessageDeltaArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingMessageDelta;
        }

        if (typeof(T) == typeof(MeetingSpeakerChangedArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingSpeakerChanged;
        }

        if (typeof(T) == typeof(MeetingThinkingDeltaArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingThinkingDelta;
        }

        if (typeof(T) == typeof(MeetingToolCallArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingToolCall;
        }

        if (typeof(T) == typeof(MeetingToolResultArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingToolResult;
        }

        if (typeof(T) == typeof(MeetingMessageCompletedArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingMessageCompleted;
        }

        if (typeof(T) == typeof(MeetingMessageDiscardedArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingMessageDiscarded;
        }

        if (typeof(T) == typeof(MeetingMessagePartialSavedArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingMessagePartialSaved;
        }

        if (typeof(T) == typeof(MeetingCompletedArgs))
        {
            return (EventID<T>)(object)Events.OnMeetingCompleted;
        }

        throw new InvalidOperationException($"未注册测试事件类型：{typeof(T).Name}");
    }

    private static T CreateUninitialized<T>()
        where T : class
    {
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private sealed class TestAgent(string id, string name) : AIAgent
    {
        protected override string IdCore => id;

        public override string Name => name;

        public override string Description => string.Empty;

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(
            AgentSession session,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(
            JsonElement serializedState,
            JsonSerializerOptions? jsonSerializerOptions,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override Task<AgentResponse> RunCoreAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(
            IEnumerable<ChatMessage> messages,
            AgentSession? session = null,
            AgentRunOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
