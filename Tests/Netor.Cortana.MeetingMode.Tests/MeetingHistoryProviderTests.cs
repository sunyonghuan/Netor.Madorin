using Microsoft.Extensions.AI;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingHistoryProviderTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingMessageService _messages = null!;
    private MeetingCompactionSegmentService _segments = null!;
    private MeetingHistoryProvider _provider = null!;
    private MeetingSessionService _sessions = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-history-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _messages = new MeetingMessageService(_db);
        _segments = new MeetingCompactionSegmentService(_db);
        _sessions = new MeetingSessionService(_db);
        _provider = new MeetingHistoryProvider(
            _messages,
            _segments,
            new SystemSettingsService(_db),
            _db);
    }

    [TestCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        DeleteIfExists(_dbPath);
        DeleteIfExists($"{_dbPath}-shm");
        DeleteIfExists($"{_dbPath}-wal");
    }

    [TestMethod]
    public async Task BuildLlmHistoryAsync_NoSegments_FiltersPartialMessages()
    {
        const string meetingId = "meeting-no-segments";
        CreateMeeting(meetingId);
        AppendComplete(meetingId, "user", "我", "主题");
        AppendPartial(meetingId, "agent", "专家", "未完成");
        AppendComplete(meetingId, "agent", "专家", "完整观点");

        var history = await _provider.BuildLlmHistoryAsync(meetingId);

        Assert.HasCount(2, history);
        Assert.AreEqual(ChatRole.User, history[0].Role);
        Assert.AreEqual("主题", history[0].Text);
        Assert.AreEqual("完整观点", history[1].Text);
    }

    [TestMethod]
    public async Task BuildLlmHistoryAsync_OpeningMessage_IsMeetingContext()
    {
        const string meetingId = "meeting-opening-context";
        CreateMeeting(meetingId);
        AppendComplete(meetingId, "user", "老板", "# 会议主题\n核算新品成本");
        AppendComplete(meetingId, "agent", "产品部", "先明确候选产品。");

        var history = await _provider.BuildLlmHistoryAsync(meetingId);

        Assert.HasCount(2, history);
        Assert.AreEqual(ChatRole.System, history[0].Role);
        Assert.AreEqual("会议上下文", history[0].AuthorName);
        Assert.AreEqual(ChatRole.Assistant, history[1].Role);
    }

    [TestMethod]
    public async Task BuildLlmHistoryAsync_WithSegment_ReturnsSummaryAndRawTail()
    {
        const string meetingId = "meeting-with-segment";
        CreateMeeting(meetingId);
        AppendComplete(meetingId, "user", "我", "主题");
        AppendComplete(meetingId, "agent", "专家甲", "旧观点");
        AppendComplete(meetingId, "host", "主持人", "尾部总结");
        AppendComplete(meetingId, "agent", "专家乙", "尾部观点");
        _segments.Add(new MeetingCompactionSegmentEntity
        {
            MeetingId = meetingId,
            SegmentIndex = 0,
            StartSequence = 0,
            EndSequence = 1,
            Summary = "压缩后的早期摘要",
            OriginalMessageCount = 2,
            ModelName = "test-model",
        });

        var history = await _provider.BuildLlmHistoryAsync(meetingId);

        Assert.HasCount(3, history);
        Assert.AreEqual(ChatRole.System, history[0].Role);
        StringAssert.Contains(history[0].Text, "压缩后的早期摘要");
        Assert.AreEqual("尾部总结", history[1].Text);
        Assert.AreEqual("主持人", history[1].AuthorName);
        Assert.AreEqual("尾部观点", history[2].Text);
    }

    [TestMethod]
    public async Task BuildLlmHistoryAsync_WhenSegmentsExceedDisplayLimit_ReturnsRecentSegmentsAndLatestTail()
    {
        const string meetingId = "meeting-segment-limit";
        CreateMeeting(meetingId);
        new SystemSettingsService(_db).SetValue("Meeting.Compaction.MaxDisplaySegments", "2");
        AppendComplete(meetingId, "agent", "专家0", "原文0");
        AppendComplete(meetingId, "agent", "专家1", "原文1");
        AppendComplete(meetingId, "agent", "专家2", "原文2");
        AppendComplete(meetingId, "agent", "专家3", "原文3");
        AppendComplete(meetingId, "agent", "专家4", "尾部观点");
        AddSegment(meetingId, 0, 0, "摘要0");
        AddSegment(meetingId, 1, 1, "摘要1");
        AddSegment(meetingId, 2, 3, "摘要2");

        var history = await _provider.BuildLlmHistoryAsync(meetingId);

        Assert.HasCount(3, history);
        Assert.AreEqual(ChatRole.System, history[0].Role);
        Assert.IsFalse(history[0].Text.Contains("摘要0", StringComparison.Ordinal));
        StringAssert.Contains(history[0].Text, "摘要1");
        StringAssert.Contains(history[1].Text, "摘要2");
        Assert.AreEqual("尾部观点", history[2].Text);
    }

    [TestMethod]
    public void DrainPendingInputsAsUserMessages_ConsumesQueueOnce()
    {
        const string meetingId = "meeting-drain-pending";
        CreateMeeting(meetingId);
        var pending = new MeetingPendingInputService(_db);
        pending.Enqueue(meetingId, "第一条插话");
        pending.Enqueue(meetingId, "第二条插话");

        var firstDrain = _provider.DrainPendingInputsAsUserMessages(meetingId, pending);
        var secondDrain = _provider.DrainPendingInputsAsUserMessages(meetingId, pending);

        Assert.HasCount(2, firstDrain);
        Assert.IsTrue(firstDrain.All(message => message.Role == ChatRole.User));
        Assert.IsTrue(firstDrain.All(message => message.AuthorName == "用户"));
        Assert.AreEqual("第一条插话", firstDrain[0].Text);
        Assert.AreEqual("第二条插话", firstDrain[1].Text);
        Assert.HasCount(0, secondDrain);
        Assert.HasCount(0, pending.PeekUnconsumed(meetingId));
    }

    [TestMethod]
    public void DrainPendingInputsAsUserMessages_SkipsAlreadyPersistedUserInterrupts()
    {
        const string meetingId = "meeting-drain-persisted";
        CreateMeeting(meetingId);
        AppendComplete(meetingId, "user", "老板", "已经入库的插话");
        var pending = new MeetingPendingInputService(_db);
        pending.Enqueue(meetingId, "已经入库的插话");
        pending.Enqueue(meetingId, "尚未入库的插话");

        var drained = _provider.DrainPendingInputsAsUserMessages(meetingId, pending);

        Assert.HasCount(1, drained);
        Assert.AreEqual("尚未入库的插话", drained[0].Text);
        Assert.HasCount(0, pending.PeekUnconsumed(meetingId));
    }

    [TestMethod]
    public async Task BuildLlmHistoryAsync_UserInterrupt_IsWrappedAsFlowChangeEvent()
    {
        const string meetingId = "meeting-interrupt-history";
        CreateMeeting(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "user",
            SpeakerId = "user",
            SpeakerName = "老板",
            ContentMd = "刚才产品规格说错了，应该按 B 方案。",
            MessageRole = "interrupt",
        });

        var history = await _provider.BuildLlmHistoryAsync(meetingId);

        Assert.HasCount(1, history);
        Assert.AreEqual(ChatRole.User, history[0].Role);
        StringAssert.Contains(history[0].Text, "[老板插话/流程变更事件]");
        StringAssert.Contains(history[0].Text, "刚才产品规格说错了");
        StringAssert.Contains(history[0].Text, "不能从零重新开会");
    }

    [TestMethod]
    public void ListByMeeting_IncludesPartialMessages_ForUiReplay()
    {
        const string meetingId = "meeting-ui-replay";
        CreateMeeting(meetingId);
        AppendComplete(meetingId, "agent", "专家", "完整观点");
        AppendPartial(meetingId, "agent", "专家", "未完成观点");

        var messages = _messages.ListByMeeting(meetingId);

        Assert.HasCount(2, messages);
        Assert.IsFalse(messages[0].IsPartial);
        Assert.IsTrue(messages[1].IsPartial);
        Assert.AreEqual("网络中断", messages[1].ErrorMessage);
    }

    private void CreateMeeting(string meetingId)
    {
        const string sessionId = "session-test";
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
            WorkspaceId = "workspace-test",
            Topic = "测试会议",
            ParticipantsJson = "[]",
            Status = 0,
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private void AppendComplete(string meetingId, string speakerKind, string speakerName, string content)
    {
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = speakerKind,
            SpeakerId = speakerName,
            SpeakerName = speakerName,
            ContentMd = content,
        });
    }

    private void AppendPartial(string meetingId, string speakerKind, string speakerName, string content)
    {
        _messages.AppendPartial(
            meetingId,
            speakerKind,
            speakerName,
            speakerName,
            content,
            "网络中断");
    }

    private void AddSegment(string meetingId, int index, int endSequence, string summary)
    {
        _segments.Add(new MeetingCompactionSegmentEntity
        {
            MeetingId = meetingId,
            SegmentIndex = index,
            StartSequence = endSequence,
            EndSequence = endSequence,
            Summary = summary,
            OriginalMessageCount = 1,
            ModelName = "test-model",
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
