using System.Runtime.CompilerServices;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingExecutorResumeTests
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
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-resume-{Guid.NewGuid():N}.db");
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
    public async Task ResumeAsync_WhenCalledTwice_AppendsAndPublishesReplyOnce()
    {
        const string meetingId = "meeting-resume";
        CreateMeeting(meetingId);
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");
        var userSpoke = new List<MeetingUserSpokeArgs>();
        _subscriber.Subscribe<MeetingUserSpokeArgs>(
            Events.OnMeetingUserSpoke,
            (_, args) =>
            {
                userSpoke.Add(args);
                return Task.FromResult(false);
            });

        var executor = CreateExecutor();
        await executor.ResumeAsync(meetingId, "  可以结束  ");
        await executor.ResumeAsync(meetingId, "可以结束");

        var messages = _messages.ListByMeeting(meetingId);
        Assert.HasCount(1, messages);
        Assert.AreEqual("可以结束", messages[0].ContentMd);
        Assert.HasCount(1, userSpoke);
        Assert.AreEqual(messages[0].Id, userSpoke[0].MessageId);
        Assert.AreEqual("reply", userSpoke[0].Kind);
        Assert.IsNull(_sessions.GetById(meetingId)?.PendingRequestId);
    }

    private MeetingExecutor CreateExecutor()
    {
        var pending = new MeetingPendingInputService(_db);
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
            pending,
            attachments,
            history,
            CreateUninitialized<MeetingCompactionService>(),
            new AiProviderService(_db),
            new AiModelService(_db),
            new MeetingCancellationRegistry(),
            _publisher,
            NullLogger<MeetingExecutor>.Instance,
            NullLoggerFactory.Instance);
    }

    private void CreateMeeting(string meetingId)
    {
        const string sessionId = "session-resume";
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO ChatSessions (
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
            WorkspaceId = "workspace-resume",
            Topic = "恢复幂等测试",
            ParticipantsJson = "[]",
            Provider = "provider-test",
            Model = "model-test",
        });
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
}
