using System.Runtime.CompilerServices;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using MeetingParticipant = Netor.Cortana.Entitys.MeetingParticipantDto;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingRuntimeControlTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private MeetingPendingInputService _pending = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private ISubscriber _subscriber = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-runtime-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _pending = new MeetingPendingInputService(_db);
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
    public async Task EnqueueInterruptAsync_QueuesInterrupt_AndPublishesUserSpoke()
    {
        const string meetingId = "meeting-interrupt";
        CreateMeeting(meetingId);
        var userSpoke = new List<MeetingUserSpokeArgs>();
        _subscriber.Subscribe<MeetingUserSpokeArgs>(
            Events.OnMeetingUserSpoke,
            (_, args) =>
            {
                userSpoke.Add(args);
                return Task.FromResult(false);
            });

        await CreateExecutor().EnqueueInterruptAsync(meetingId, "  请补充风险  ");

        var queued = _pending.PeekUnconsumed(meetingId);
        Assert.HasCount(1, queued);
        Assert.AreEqual("请补充风险", queued[0].Content);
        Assert.AreEqual("interrupt", queued[0].Kind);
        Assert.IsFalse(queued[0].Consumed);
        Assert.HasCount(1, userSpoke);
        Assert.AreEqual(meetingId, userSpoke[0].MeetingId);
        Assert.AreEqual("请补充风险", userSpoke[0].Text);
        Assert.AreEqual("interrupt", userSpoke[0].Kind);

        var messages = _messages.ListByMeeting(meetingId);
        Assert.HasCount(1, messages);
        Assert.AreEqual(messages[0].Id, userSpoke[0].MessageId);
        Assert.AreEqual("user", messages[0].SpeakerKind);
        Assert.AreEqual("老板", messages[0].SpeakerName);
        Assert.AreEqual("请补充风险", messages[0].ContentMd);
        Assert.AreEqual("interrupt", messages[0].MessageRole);
    }

    [TestMethod]
    public async Task CancelAsync_WhenMeetingIsNotRegistered_AdjournsAndPublishesUserCancel()
    {
        const string meetingId = "meeting-cancel-idle";
        CreateMeeting(meetingId);
        var cancelled = new List<MeetingCancelledArgs>();
        _subscriber.Subscribe<MeetingCancelledArgs>(
            Events.OnMeetingCancelled,
            (_, args) =>
            {
                cancelled.Add(args);
                return Task.FromResult(false);
            });

        await CreateExecutor().CancelAsync(meetingId);

        var meeting = _sessions.GetById(meetingId);
        Assert.IsNotNull(meeting);
        Assert.AreEqual(3, meeting.Status);
        Assert.IsNotNull(meeting.EndedAt);
        Assert.HasCount(1, cancelled);
        Assert.AreEqual(meetingId, cancelled[0].MeetingId);
        Assert.AreEqual("user_cancel", cancelled[0].Reason);
    }

    [TestMethod]
    public async Task PauseAsync_WhenMeetingIsNotRegistered_KeepsMeetingContinuable()
    {
        const string meetingId = "meeting-pause-idle";
        CreateMeeting(meetingId);
        var paused = new List<MeetingPausedArgs>();
        _subscriber.Subscribe<MeetingPausedArgs>(
            Events.OnMeetingPaused,
            (_, args) =>
            {
                paused.Add(args);
                return Task.FromResult(false);
            });

        await CreateExecutor().PauseAsync(meetingId);

        var meeting = _sessions.GetById(meetingId);
        Assert.IsNotNull(meeting);
        Assert.AreEqual(0, meeting.Status);
        Assert.IsNull(meeting.EndedAt);
        Assert.HasCount(1, paused);
        Assert.AreEqual(meetingId, paused[0].MeetingId);
        Assert.AreEqual("user_pause", paused[0].Reason);
    }

    [TestMethod]
    public async Task MeetingCancellationRegistry_RejectsDuplicateRegistration_AndCancelsRegisteredToken()
    {
        var registry = new MeetingCancellationRegistry();
        using var first = new CancellationTokenSource();
        using var duplicate = new CancellationTokenSource();

        Assert.IsTrue(registry.TryRegister("meeting-running", first));
        Assert.IsFalse(registry.TryRegister("meeting-running", duplicate));

        var cancelled = await registry.CancelAsync("meeting-running");

        Assert.IsTrue(cancelled);
        Assert.IsTrue(first.IsCancellationRequested);
        Assert.IsFalse(duplicate.IsCancellationRequested);
    }

    [TestMethod]
    public async Task MeetingCancellationRegistry_UnregistersMeeting_AndAllowsFutureRegistration()
    {
        var registry = new MeetingCancellationRegistry();
        using var first = new CancellationTokenSource();
        using var second = new CancellationTokenSource();

        Assert.IsTrue(registry.TryRegister("meeting-rerun", first));
        Assert.IsTrue(registry.TryUnregister("meeting-rerun"));

        var cancelled = await registry.CancelAsync("meeting-rerun");
        var registeredAgain = registry.TryRegister("meeting-rerun", second);

        Assert.IsFalse(cancelled);
        Assert.IsFalse(first.IsCancellationRequested);
        Assert.IsTrue(registeredAgain);
    }

    [TestMethod]
    public void IsTerminalWorkflowOutput_DoesNotTreatAgentEventsAsWorkflowCompletion()
    {
        var update = new AgentResponseUpdateEvent(
            "system-meeting-host",
            new AgentResponseUpdate(ChatRole.Assistant, [new TextContent("主持人开始发言")]));
        var response = new AgentResponseEvent(
            "system-meeting-host",
            new AgentResponse(new ChatMessage(ChatRole.Assistant, "主持人完成本轮")));
        var finalOutput = new WorkflowOutputEvent(
            new List<ChatMessage> { new(ChatRole.Assistant, "会议最终输出") },
            "GroupChatHost");

        Assert.IsFalse(MeetingExecutor.IsTerminalWorkflowOutput(update));
        Assert.IsFalse(MeetingExecutor.IsTerminalWorkflowOutput(response));
        Assert.IsTrue(MeetingExecutor.IsTerminalWorkflowOutput(finalOutput));
    }

    private MeetingExecutor CreateExecutor(ILogger<MeetingExecutor>? logger = null)
    {
        var attachments = new MeetingAttachmentService(_db);
        var segments = new MeetingCompactionSegmentService(_db);
        var history = new MeetingHistoryProvider(
            _messages,
            segments,
            new SystemSettingsService(_db),
            _db);

        return new MeetingExecutor(
            CreateUninitialized<MeetingAgentBuilder>(),
            _sessions,
            _messages,
            _pending,
            attachments,
            history,
            CreateUninitialized<MeetingCompactionService>(),
            new AiProviderService(_db),
            new AiModelService(_db),
            new MeetingCancellationRegistry(),
            _publisher,
            logger ?? NullLogger<MeetingExecutor>.Instance,
            NullLoggerFactory.Instance);
    }

    private void CreateMeeting(string meetingId, string participantsJson = "[]")
    {
        const string sessionId = "session-runtime";
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
            WorkspaceId = "workspace-runtime",
            Topic = "运行时控制测试",
            ParticipantsJson = participantsJson,
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private void CreateProviderAndModel()
    {
        new AiProviderService(_db).Add(new AiProviderEntity
        {
            Id = "provider-test",
            Name = "测试厂商",
            Url = "https://example.invalid/v1",
            Key = "test-key",
            ProviderType = "OpenAI",
            IsEnabled = true,
        });

        new AiModelService(_db).Add(new AiModelEntity
        {
            Id = "model-test",
            Name = "test-model",
            DisplayName = "测试模型",
            ProviderId = "provider-test",
            IsEnabled = true,
        });
    }

    private static string SerializeParticipants(int count)
    {
        var participants = Enumerable.Range(1, count)
            .Select(index => new MeetingParticipant($"agent-{index}", $"专家{index}", index))
            .ToList();

        return JsonSerializer.Serialize(
            participants,
            MeetingJsonContext.Default.ListMeetingParticipantDto);
    }

    private static T CreateUninitialized<T>()
        where T : class
    {
        return (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogMessage> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return NullScope.Instance;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Messages.Add(new LogMessage(logLevel, formatter(state, exception)));
        }
    }

    private sealed record LogMessage(LogLevel Level, string Text);

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
