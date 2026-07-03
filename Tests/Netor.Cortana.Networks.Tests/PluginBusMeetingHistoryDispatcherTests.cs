using System.Text.Json;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Networks.Tests.WebSockets;

[TestClass]
public sealed class PluginBusMeetingHistoryDispatcherTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private List<string> _sent = null!;
    private PluginBusMeetingHistoryDispatcher _dispatcher = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-replay-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _sent = [];
        _dispatcher = new PluginBusMeetingHistoryDispatcher(
            _db,
            NullLogger.Instance,
            (clientId, message, cancellationToken) =>
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
    public async Task ReplayAsync_ReturnsOnlyCompletedMeetings()
    {
        var completedId = CreateMeeting("completed-topic");
        _sessions.SetFinalSummary(completedId, "会议最终总结");
        var cancelledId = CreateMeeting("cancelled-topic");
        _sessions.MarkAsAdjourned(cancelledId);

        await _dispatcher.ReplayAsync("client-1", "request-1", 0, 100, CancellationToken.None);

        Assert.HasCount(2, _sent);
        using var batchDoc = JsonDocument.Parse(_sent[0]);
        using var completedDoc = JsonDocument.Parse(_sent[1]);
        var batch = batchDoc.RootElement;
        var completed = completedDoc.RootElement;

        Assert.AreEqual(CortanaWsEndpoints.MeetingTopic, batch.GetProperty("topic").GetString());
        Assert.AreEqual(CortanaWsEndpoints.MeetingHistoryBatchOperation, batch.GetProperty("op").GetString());
        Assert.AreEqual("request-1", batch.GetProperty("requestId").GetString());

        var items = batch.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();
        Assert.HasCount(1, items);
        Assert.AreEqual(completedId, items[0].GetProperty("meetingId").GetString());
        Assert.AreEqual("completed-topic", items[0].GetProperty("topic").GetString());
        Assert.AreEqual("会议最终总结", items[0].GetProperty("finalSummaryMd").GetString());

        Assert.AreEqual(CortanaWsEndpoints.MeetingHistoryCompletedOperation, completed.GetProperty("op").GetString());
        Assert.AreEqual(1, completed.GetProperty("payload").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task ReplayAsync_ReturnsSummaryMessagesFromUnfinishedMeetings()
    {
        var meetingId = CreateMeeting("永续讨论");
        var summary = _messages.AppendSummary(meetingId, "阶段性结论：先完成会议记忆接入。");

        await _dispatcher.ReplayAsync("client-1", "request-2", 0, 100, CancellationToken.None);

        Assert.HasCount(2, _sent);
        using var batchDoc = JsonDocument.Parse(_sent[0]);
        using var completedDoc = JsonDocument.Parse(_sent[1]);
        var items = batchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, items);
        Assert.AreEqual("summary", items[0].GetProperty("recordKind").GetString());
        Assert.AreEqual(meetingId, items[0].GetProperty("meetingId").GetString());
        Assert.AreEqual(summary.Id, items[0].GetProperty("messageId").GetString());
        Assert.AreEqual("summary", items[0].GetProperty("messageRole").GetString());
        Assert.AreEqual("阶段性结论：先完成会议记忆接入。", items[0].GetProperty("contentMd").GetString());
        Assert.AreEqual(1, completedDoc.RootElement.GetProperty("payload").GetProperty("total").GetInt32());
    }

    [TestMethod]
    public async Task ReplayAsync_DoesNotSkipRecordsWithSameUpdatedAtAcrossBatches()
    {
        const long timestamp = 1_700_000_000_000;
        var completedId = CreateMeeting("同毫秒最终总结");
        _sessions.SetFinalSummary(completedId, "最终总结");
        var summaryMeetingId = CreateMeeting("同毫秒阶段总结");
        var summary = _messages.AppendSummary(summaryMeetingId, "阶段总结");
        ForceMeetingUpdatedAt(completedId, timestamp);
        ForceMessageCreatedAt(summary.Id, timestamp);

        await _dispatcher.ReplayAsync("client-1", "request-same-ms", 0, 1, CancellationToken.None);

        Assert.HasCount(3, _sent);
        using var firstBatchDoc = JsonDocument.Parse(_sent[0]);
        using var secondBatchDoc = JsonDocument.Parse(_sent[1]);
        using var completedDoc = JsonDocument.Parse(_sent[2]);
        var firstItems = firstBatchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();
        var secondItems = secondBatchDoc.RootElement.GetProperty("payload").GetProperty("items").EnumerateArray().ToArray();

        Assert.HasCount(1, firstItems);
        Assert.HasCount(1, secondItems);
        var exportedIds = firstItems.Concat(secondItems)
            .Select(item => item.GetProperty("recordKind").GetString() == "summary"
                ? item.GetProperty("messageId").GetString()
                : item.GetProperty("meetingId").GetString())
            .ToHashSet(StringComparer.Ordinal);
        Assert.IsTrue(exportedIds.Contains(completedId));
        Assert.IsTrue(exportedIds.Contains(summary.Id));
        Assert.AreEqual(2, completedDoc.RootElement.GetProperty("payload").GetProperty("total").GetInt32());
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

    private void ForceMeetingUpdatedAt(string meetingId, long timestamp)
    {
        _db.Execute(
            "UPDATE MeetingSessions SET UpdatedAt = @Timestamp, EndedAt = @Timestamp WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Timestamp", timestamp);
                cmd.Parameters.AddWithValue("@Id", meetingId);
            });
    }

    private void ForceMessageCreatedAt(string messageId, long timestamp)
    {
        _db.Execute(
            "UPDATE MeetingMessages SET CreatedAt = @Timestamp WHERE Id = @Id",
            cmd =>
            {
                cmd.Parameters.AddWithValue("@Timestamp", timestamp);
                cmd.Parameters.AddWithValue("@Id", messageId);
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
