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
public sealed class MeetingServiceHardConstraintTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-services-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPath);
        DeleteIfExists($"{_dbPath}-shm");
        DeleteIfExists($"{_dbPath}-wal");
    }

    [TestMethod]
    public async Task Append_AssignsMonotonicUniqueSequence_WhenManyMessagesAreAppended()
    {
        const string meetingId = "meeting-sequence";
        CreateMeeting(meetingId, "session-sequence");

        var appendTasks = Enumerable.Range(0, 20)
            .Select(index => Task.Run(() => _messages.Append(new MeetingMessageEntity
            {
                MeetingId = meetingId,
                SpeakerKind = "agent",
                SpeakerId = $"agent-{index}",
                SpeakerName = $"专家{index}",
                ContentMd = $"观点{index}",
            })))
            .ToArray();

        await Task.WhenAll(appendTasks);

        var messages = _messages.ListByMeeting(meetingId);

        Assert.HasCount(20, messages);
        CollectionAssert.AreEqual(
            Enumerable.Range(0, 20).ToArray(),
            messages.Select(static message => message.Sequence).ToArray());
    }

    [TestMethod]
    public void MeetingMessages_RejectsDuplicateSequence_ByUniqueIndex()
    {
        const string meetingId = "meeting-duplicate-sequence";
        CreateMeeting(meetingId, "session-duplicate-sequence");
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-a",
            SpeakerName = "专家A",
            ContentMd = "第一条",
        });

        Assert.ThrowsExactly<SqliteException>(() =>
            _db.Execute("""
                INSERT INTO MeetingMessages (
                    Id, MeetingId, Sequence, SpeakerKind, SpeakerId, SpeakerName, ContentMd,
                    MessageRole, AwaitingUserReply, IsPartial, CreatedAt
                ) VALUES (
                    @Id, @MeetingId, 0, 'agent', 'agent-b', '专家B', '重复序号',
                    'normal', 0, 0, @Now
                )
                """,
                command =>
                {
                    command.Parameters.AddWithValue("@Id", Guid.NewGuid().ToString("N"));
                    command.Parameters.AddWithValue("@MeetingId", meetingId);
                    command.Parameters.AddWithValue("@Now", DateTimeOffset.Now.ToUnixTimeMilliseconds());
                }));
    }

    [TestMethod]
    public void TryClearPending_ClearsOnceOnly_WhenRequestIdMatches()
    {
        const string meetingId = "meeting-pending";
        CreateMeeting(meetingId, "session-pending");
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");

        var first = _sessions.TryClearPending(meetingId, "request-1");
        var second = _sessions.TryClearPending(meetingId, "request-1");
        var meeting = _sessions.GetById(meetingId);

        Assert.AreEqual(1, first);
        Assert.AreEqual(0, second);
        Assert.IsNotNull(meeting);
        Assert.AreEqual(0, meeting.Status);
        Assert.IsNull(meeting.PendingRequestId);
        Assert.IsNull(meeting.PendingRequestKind);
        Assert.IsNull(meeting.PendingRequestData);
    }

    [TestMethod]
    public void CreateBackingChatSession_CreatesArchivedSession_ForMeetingOnly()
    {
        var sessionId = _sessions.CreateBackingChatSession("workspace-test", "agent-a");

        var archived = _db.ExecuteScalar<int>(
            "SELECT IsArchived FROM ChatSessions WHERE Id = @Id",
            command => command.Parameters.AddWithValue("@Id", sessionId));
        var chatMessages = _db.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM ChatMessages WHERE SessionId = @Id",
            command => command.Parameters.AddWithValue("@Id", sessionId));

        Assert.AreEqual(1, archived);
        Assert.AreEqual(0, chatMessages);
    }

    [TestMethod]
    public void Constructor_ArchivesLegacyMeetingBackingSession_EvenWhenChatMessagesExist()
    {
        const string meetingId = "meeting-legacy-visible";
        const string sessionId = "session-legacy-visible";
        CreateMeeting(meetingId, sessionId);
        _db.Execute(
            "UPDATE ChatSessions SET IsArchived = 0 WHERE Id = @Id",
            command => command.Parameters.AddWithValue("@Id", sessionId));
        _db.Execute("""
            INSERT INTO ChatMessages (
                Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName,
                Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName
            ) VALUES (
                @Id, @Now, @Now, @SessionId, 'assistant', '会议主持人',
                '旧会议总结', '', 0, 'meeting.archive', @CreatedAt, '', '会议主持人'
            )
            """,
            command =>
            {
                var now = DateTimeOffset.Now;
                command.Parameters.AddWithValue("@Id", "meeting_archive_legacy_visible");
                command.Parameters.AddWithValue("@Now", now.ToUnixTimeMilliseconds());
                command.Parameters.AddWithValue("@SessionId", sessionId);
                command.Parameters.AddWithValue("@CreatedAt", now.ToString("O"));
            });

        _db.Dispose();
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);

        var archived = _db.ExecuteScalar<int>(
            "SELECT IsArchived FROM ChatSessions WHERE Id = @Id",
            command => command.Parameters.AddWithValue("@Id", sessionId));

        Assert.AreEqual(1, archived);
    }

    [TestMethod]
    public void ListByWorkspace_ReturnsMeetingsAcrossBackingSessions()
    {
        CreateMeeting("meeting-a", "session-a");
        CreateMeeting("meeting-b", "session-b");
        CreateMeeting("meeting-other", "session-other", workspaceId: "workspace-other");

        var meetings = _sessions.ListByWorkspace("workspace-test");

        CollectionAssert.AreEquivalent(
            new[] { "meeting-a", "meeting-b" },
            meetings.Select(static meeting => meeting.Id).ToArray());
    }

    [TestMethod]
    public async Task StartAsync_AdjournsOrphanActiveMeetings_AndPublishesCancelledEvents()
    {
        CreateMeeting("meeting-running", "session-running", status: 0);
        CreateMeeting("meeting-waiting", "session-waiting", status: 1);
        CreateMeeting("meeting-completed", "session-completed", status: 2);

        using var services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        var publisher = services.GetRequiredService<IPublisher>();
        var subscriber = services.GetRequiredService<ISubscriber>();
        var cancelled = new List<MeetingCancelledArgs>();
        subscriber.Subscribe<MeetingCancelledArgs>(
            Events.OnMeetingCancelled,
            (_, args) =>
            {
                cancelled.Add(args);
                return Task.FromResult(false);
            });

        var startup = new MeetingStartupService(
            _sessions,
            publisher,
            NullLogger<MeetingStartupService>.Instance);

        await startup.StartAsync(CancellationToken.None);

        Assert.AreEqual(3, _sessions.GetById("meeting-running")?.Status);
        Assert.AreEqual(3, _sessions.GetById("meeting-waiting")?.Status);
        Assert.AreEqual(2, _sessions.GetById("meeting-completed")?.Status);
        Assert.HasCount(2, cancelled);
        CollectionAssert.AreEquivalent(
            new[] { "meeting-running", "meeting-waiting" },
            cancelled.Select(static args => args.MeetingId).ToArray());
        Assert.IsTrue(cancelled.All(static args => args.Reason == "auto_adjourn_on_startup"));
    }

    private void CreateMeeting(string meetingId, string sessionId, int status = 0, string workspaceId = "workspace-test")
    {
        CreateChatSession(sessionId);
        _sessions.Create(new MeetingSessionEntity
        {
            Id = meetingId,
            SessionId = sessionId,
            WorkspaceId = workspaceId,
            Topic = "测试会议",
            ParticipantsJson = "[]",
            Status = status,
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private void CreateChatSession(string sessionId)
    {
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
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
