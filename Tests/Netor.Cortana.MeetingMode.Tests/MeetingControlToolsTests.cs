using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.AI.MeetingMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingControlToolsTests
{
    private string _root = null!;
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private MeetingPendingInputService _pending = null!;
    private MeetingAttachmentService _attachments = null!;
    private TestTerminationSignal _terminationSignal = null!;
    private TestAppPaths _paths = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private ISubscriber _subscriber = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"cortana-meeting-tools-{Guid.NewGuid():N}");
        _dbPath = Path.Combine(_root, "cortana.db");
        Directory.CreateDirectory(_root);

        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _pending = new MeetingPendingInputService(_db);
        _attachments = new MeetingAttachmentService(_db);
        _terminationSignal = new TestTerminationSignal();
        _paths = new TestAppPaths(_root);
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

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [TestMethod]
    public async Task OutputSummary_WritesSummaryMessage_AndPublishesEvents()
    {
        const string meetingId = "meeting-summary";
        CreateMeeting(meetingId);
        var drafts = new List<MeetingSummaryDraftArgs>();
        var completed = new List<MeetingMessageCompletedArgs>();
        _subscriber.Subscribe<MeetingSummaryDraftArgs>(
            Events.OnMeetingSummaryDraft,
            (_, args) =>
            {
                drafts.Add(args);
                return Task.FromResult(false);
            });
        _subscriber.Subscribe<MeetingMessageCompletedArgs>(
            Events.OnMeetingMessageCompleted,
            (_, args) =>
            {
                completed.Add(args);
                return Task.FromResult(false);
            });

        var result = await CreateTools(meetingId)
            .CreateOutputSummaryTool()
            .InvokeAsync(new AIFunctionArguments { ["content"] = "  # 最终结论  " });

        var message = _messages.GetLatestSummary(meetingId);
        StringAssert.Contains(result?.ToString(), "会议总结已记录");
        StringAssert.Contains(result?.ToString(), "不要再选择参会者继续讨论原始话题");
        Assert.IsNotNull(message);
        Assert.AreEqual("# 最终结论", message.ContentMd);
        Assert.AreEqual("summary", message.MessageRole);
        Assert.HasCount(1, drafts);
        Assert.AreEqual("# 最终结论", drafts[0].SummaryMarkdown);
        Assert.HasCount(1, completed);
        Assert.AreEqual(message.Id, completed[0].MessageId);
        Assert.AreEqual("summary", completed[0].MessageRole);
    }

    [TestMethod]
    public async Task OutputSummary_WhenContentIsEmptyJson_DoesNotWriteSummary()
    {
        const string meetingId = "meeting-summary-empty-json";
        CreateMeeting(meetingId);
        var drafts = new List<MeetingSummaryDraftArgs>();
        _subscriber.Subscribe<MeetingSummaryDraftArgs>(
            Events.OnMeetingSummaryDraft,
            (_, args) =>
            {
                drafts.Add(args);
                return Task.FromResult(false);
            });

        var result = await CreateTools(meetingId)
            .CreateOutputSummaryTool()
            .InvokeAsync(new AIFunctionArguments { ["content"] = "{}" });

        StringAssert.Contains(result?.ToString(), "内部调度 JSON");
        Assert.IsNull(_messages.GetLatestSummary(meetingId));
        Assert.HasCount(0, drafts);
    }

    [TestMethod]
    public async Task OutputSummary_WhenContentIsSelectorDecisionJson_DoesNotWriteSummary()
    {
        const string meetingId = "meeting-summary-selector-json";
        CreateMeeting(meetingId);

        var result = await CreateTools(meetingId)
            .CreateOutputSummaryTool()
            .InvokeAsync(new AIFunctionArguments
            {
                ["content"] = """
                {
                  "executionPlan": {
                    "topic": "新品上市评审",
                    "phases": []
                  },
                  "currentPhaseId": "product_readiness",
                  "nextSpeakerId": "agent-product",
                  "readyForSpeaker": true
                }
                """
            });

        StringAssert.Contains(result?.ToString(), "内部调度 JSON");
        Assert.IsNull(_messages.GetLatestSummary(meetingId));
    }

    [TestMethod]
    public async Task ConfirmMeetingEnd_UsesLatestSummary_AndSetsTerminationSignal()
    {
        const string meetingId = "meeting-confirm-end";
        CreateMeeting(meetingId);
        _messages.AppendSummary(meetingId, "旧版总结");
        _messages.AppendSummary(meetingId, "新版总结");

        var result = await CreateTools(meetingId)
            .CreateConfirmMeetingEndTool()
            .InvokeAsync(new AIFunctionArguments());

        var meeting = _sessions.GetById(meetingId);
        StringAssert.Contains(result?.ToString(), "会议已正式结束");
        StringAssert.Contains(result?.ToString(), "不要重新开始原始话题");
        Assert.IsNotNull(meeting);
        Assert.AreEqual(2, meeting.Status);
        Assert.AreEqual("新版总结", meeting.FinalSummaryMd);
        Assert.IsTrue(_terminationSignal.IsSet);
    }

    [TestMethod]
    public async Task CheckPendingUserInput_ReportsWithoutConsumingQueue()
    {
        const string meetingId = "meeting-check-pending";
        CreateMeeting(meetingId);
        _pending.Enqueue(meetingId, "请补充风险说明");

        var result = await CreateTools(meetingId)
            .CreateCheckPendingUserInputTool()
            .InvokeAsync(new AIFunctionArguments());

        StringAssert.Contains(result?.ToString(), "有 1 条用户插话尚未处理");
        StringAssert.Contains(result?.ToString(), "请补充风险说明");
        Assert.HasCount(1, _pending.PeekUnconsumed(meetingId));
    }

    [TestMethod]
    public async Task ListMeetingAttachments_ExposesAbsoluteResourcePath()
    {
        const string meetingId = "meeting-list-attachments";
        CreateMeeting(meetingId);
        var relativePath = Path.Combine("meetings", meetingId, "attachments", "需求.md");
        _attachments.Add(new MeetingAttachmentEntity
        {
            MeetingId = meetingId,
            FileName = "需求.md",
            StoredPath = relativePath,
            MimeType = "text/markdown",
            SizeBytes = 42,
        });

        var result = await CreateTools(meetingId)
            .CreateListAttachmentsTool()
            .InvokeAsync(new AIFunctionArguments());

        StringAssert.Contains(result?.ToString(), Path.Combine(_paths.WorkspaceResourcesDirectory, relativePath));
        StringAssert.Contains(result?.ToString(), "需求.md");
    }

    private MeetingControlTools CreateTools(string meetingId)
    {
        return new MeetingControlTools(
            _terminationSignal,
            _sessions,
            _messages,
            _pending,
            _attachments,
            _paths,
            _publisher,
            meetingId);
    }

    private void CreateMeeting(string meetingId)
    {
        const string sessionId = "session-tools";
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
            WorkspaceId = "workspace-tools",
            Topic = "主持人工具测试",
            ParticipantsJson = "[]",
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private sealed class TestTerminationSignal : IMeetingTerminationSignal
    {
        public bool IsSet { get; private set; }

        public void SetTerminationFlag()
        {
            IsSet = true;
        }
    }

    private sealed class TestAppPaths(string root) : IAppPaths
    {
        public string WorkspaceDirectory { get; } = Path.Combine(root, "workspace");

        public string UserDataDirectory { get; } = Path.Combine(root, "user");

        public string WorkspaceSkillsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "skills");

        public string WorkspacePluginsDirectory => Path.Combine(WorkspaceDirectory, ".cortana", "plugins");

        public string UserSkillsDirectory => Path.Combine(UserDataDirectory, "skills");

        public string UserPluginsDirectory => Path.Combine(UserDataDirectory, "plugins");

        public string UserAgentsDirectory => Path.Combine(UserDataDirectory, "agents");

        public string UserSolutionsDirectory => Path.Combine(UserDataDirectory, "solutions");

        public string PluginDirectory => UserPluginsDirectory;

        public string WorkspaceResourcesDirectory { get; } = Path.Combine(root, "workspace", ".cortana", "resources");

        public string HistoryResourcesDirectory => Path.Combine(WorkspaceResourcesDirectory, "histories");

        public string PromptsDirectory => Path.Combine(UserDataDirectory, "prompts");
    }
}
