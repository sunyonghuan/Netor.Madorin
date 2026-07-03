using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Json;
using Netor.Cortana.AI.MeetingMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using MeetingParticipant = Netor.Cortana.Entitys.MeetingParticipantDto;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingCompletionArchiveServiceTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private ChatMessageService _chatMessages = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private MeetingCompletionArchiveService _archive = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-archive-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _chatMessages = new ChatMessageService(_db);
        _services = new ServiceCollection()
            .AddLogging()
            .AddEventHub()
            .BuildServiceProvider();
        _publisher = _services.GetRequiredService<IPublisher>();
        _archive = new MeetingCompletionArchiveService(
            _sessions,
            _db,
            _services.GetRequiredService<ISubscriber>(),
            NullLogger<MeetingCompletionArchiveService>.Instance);
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
    public async Task MeetingCompleted_ArchivesOneAssistantSummaryToChatMessages()
    {
        const string meetingId = "meeting-archive";
        const string sessionId = "session-archive";
        CreateChatSession(sessionId);
        CreateMeeting(meetingId, sessionId);
        _sessions.SetFinalSummary(meetingId, "最终结论：采用方案 A。");
        await _archive.StartAsync(CancellationToken.None);

        await _publisher.PublishAsync(
            Events.OnMeetingCompleted,
            CreateCompletedArgs(meetingId, "事件总结应被 DB 最终总结覆盖"));
        await _publisher.PublishAsync(
            Events.OnMeetingCompleted,
            CreateCompletedArgs(meetingId, "重复事件不应追加第二条"));

        var messages = _chatMessages.GetBySessionId(sessionId);
        Assert.HasCount(1, messages);
        var message = messages[0];
        Assert.AreEqual($"meeting_archive_{meetingId}", message.Id);
        Assert.AreEqual("assistant", message.Role);
        Assert.AreEqual("会议主持人", message.AuthorName);
        Assert.AreEqual("meeting.archive", message.ModelName);
        Assert.Contains("会议ID：meeting-archive", message.Content);
        Assert.Contains("主题：季度复盘", message.Content);
        Assert.Contains("参会者：产品、研发", message.Content);
        Assert.Contains("最终结论：采用方案 A。", message.Content);
        Assert.IsFalse(message.Content.Contains("事件总结应被 DB 最终总结覆盖", StringComparison.Ordinal));

        var archived = _db.ExecuteScalar<int>(
            "SELECT IsArchived FROM ChatSessions WHERE Id = @Id",
            command => command.Parameters.AddWithValue("@Id", sessionId));
        Assert.AreEqual(1, archived);
    }

    private void CreateMeeting(string meetingId, string sessionId)
    {
        _sessions.Create(new MeetingSessionEntity
        {
            Id = meetingId,
            SessionId = sessionId,
            WorkspaceId = "workspace-archive",
            Topic = "季度复盘",
            ParticipantsJson = JsonSerializer.Serialize(
                new List<MeetingParticipant>
                {
                    new("agent-product", "产品", 0),
                    new("agent-dev", "研发", 1),
                },
                MeetingJsonContext.Default.ListMeetingParticipantDto),
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

    private static MeetingCompletedArgs CreateCompletedArgs(string meetingId, string finalSummary)
    {
        return new MeetingCompletedArgs(
            MeetingId: meetingId,
            FinalSummary: finalSummary,
            SessionId: "session-archive",
            WorkspaceId: "workspace-archive",
            Topic: "季度复盘",
            HostAgentId: "system-meeting-host",
            Model: "model-test",
            CreatedAt: 100,
            EndedAt: 200);
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
