using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class PluginBusConversationHistoryDispatcherTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private List<string> _sent = null!;
    private PluginBusConversationHistoryDispatcher _dispatcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-conversation-replay-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sent = [];
        _dispatcher = new PluginBusConversationHistoryDispatcher(
            _db,
            NullLogger.Instance,
            (_, message, _) =>
            {
                _sent.Add(message);
                return Task.CompletedTask;
            });
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
    public async Task ReplayAsync_FiltersWorkflowMeetingAndArchivedChatMessages()
    {
        CreateSession("expert-session", "workspace-1", "专家普通对话", isArchived: false);
        CreateSession("source-task-session", "workspace-1", "SourceTask 工作记录", isArchived: false, sourceTaskId: "task-source");
        CreateSession("work-session", "workspace-1", "WorkTasks 工作记录", isArchived: false);
        CreateSession("meeting-session", "workspace-1", "会议记录", isArchived: false);
        CreateSession("archived-session", "workspace-1", "归档记录", isArchived: true);
        LinkWorkTask("task-linked", "work-session", "workspace-1");
        LinkMeeting("meeting-linked", "meeting-session", "workspace-1");

        CreateMessage("msg-expert", "expert-session", "普通专家内容", 100);
        CreateMessage("msg-source-task", "source-task-session", "SourceTask 工作内容", 200);
        CreateMessage("msg-work", "work-session", "WorkTasks 工作内容", 300);
        CreateMessage("msg-meeting", "meeting-session", "会议内容", 400);
        CreateMessage("msg-archived", "archived-session", "归档内容", 500);

        await _dispatcher.ReplayAsync("client-1", "request-filter", 0, 100, CancellationToken.None);

        Assert.HasCount(2, _sent);
        using var batchDoc = JsonDocument.Parse(_sent[0]);
        using var completedDoc = JsonDocument.Parse(_sent[1]);
        var items = batchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, items);
        Assert.AreEqual("msg-expert", items[0].GetProperty("id").GetString());
        Assert.AreEqual("expert-session", items[0].GetProperty("sessionId").GetString());
        Assert.AreEqual("普通专家内容", items[0].GetProperty("content").GetString());
        Assert.AreEqual(1, completedDoc.RootElement.GetProperty("payload").GetProperty("total").GetInt32());
    }

    private void CreateSession(
        string id,
        string workspaceId,
        string title,
        bool isArchived,
        string sourceTaskId = "")
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO ChatSessions (
                Id, CreatedTimestamp, UpdatedTimestamp, Categorize, Title, Summary,
                RawDiscription, AgentName, SourceTaskId, IsArchived, IsPinned,
                LastActiveTimestamp, TotalTokenCount, CompactedContext, CompactedAtCount
            ) VALUES (
                @Id, @Now, @Now, @WorkspaceId, @Title, '', '', 'agent-default', @SourceTaskId,
                @IsArchived, 0, @Now, 0, '', 0
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Now", now);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Title", title);
                cmd.Parameters.AddWithValue("@SourceTaskId", sourceTaskId);
                cmd.Parameters.AddWithValue("@IsArchived", isArchived ? 1 : 0);
            });
    }

    private void CreateMessage(string id, string sessionId, string content, long createdTimestamp)
    {
        _db.Execute("""
            INSERT INTO ChatMessages (
                Id, CreatedTimestamp, UpdatedTimestamp, SessionId, Role, AuthorName,
                Content, ContentsJson, TokenCount, ModelName, CreatedAt, AgentId, AgentName
            ) VALUES (
                @Id, @CreatedTimestamp, @CreatedTimestamp, @SessionId, 'assistant', '助手',
                @Content, '', 0, 'model-default', NULL, 'agent-default', '默认助手'
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@CreatedTimestamp", createdTimestamp);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@Content", content);
            });
    }

    private void LinkWorkTask(string taskId, string sessionId, string workspaceId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO WorkTasks (
                Id, SessionId, WorkspaceId, Title, InitialInput, RunId, ManagerRunId, IsActive,
                CurrentPlanJson, PendingRequestId, PendingRequestKind, PendingRequestData,
                CompletedAt, FinalReport, ErrorMessage, Provider, Model, AgentName, MentionsJson,
                SourceTaskId, ParentTaskId, IsOrphaned, OrphanedDetectedAt, HeartbeatAt,
                OrchestratorState, OrchestratorHeartbeatAt, HasPreemption, CreatedAt, UpdatedAt,
                LastActiveAt
            ) VALUES (
                @Id, @SessionId, @WorkspaceId, '工作任务', '输入', NULL, NULL, 0,
                NULL, NULL, NULL, NULL, @Now, '报告', NULL, 'provider-default',
                'model-default', 'agent-default', NULL, NULL, NULL, 0, NULL, NULL,
                NULL, NULL, 0, @Now, @Now, @Now
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", taskId);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Now", now);
            });
    }

    private void LinkMeeting(string meetingId, string sessionId, string workspaceId)
    {
        var now = DateTimeOffset.Now.ToUnixTimeMilliseconds();
        _db.Execute("""
            INSERT INTO MeetingSessions (
                Id, SessionId, WorkspaceId, Topic, HostAgentId, ParticipantsJson, Status,
                RunId, PendingRequestId, PendingRequestKind, PendingRequestData, FinalSummaryMd,
                Provider, Model, CreatedAt, UpdatedAt, EndedAt
            ) VALUES (
                @Id, @SessionId, @WorkspaceId, '会议', 'system-meeting-host', '[]', 2,
                NULL, NULL, NULL, NULL, '总结', 'provider-default', 'model-default',
                @Now, @Now, @Now
            )
            """,
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Id", meetingId);
                cmd.Parameters.AddWithValue("@SessionId", sessionId);
                cmd.Parameters.AddWithValue("@WorkspaceId", workspaceId);
                cmd.Parameters.AddWithValue("@Now", now);
            });
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
}
