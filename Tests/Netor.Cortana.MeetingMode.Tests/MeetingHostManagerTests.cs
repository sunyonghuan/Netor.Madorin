using System.Reflection;
using System.Text.Json;

using Microsoft.Agents.AI;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

using Netor.Cortana.AI.MeetingMode;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.MeetingMode.Tests;

[TestClass]
public sealed class MeetingHostManagerTests
{
    private string _dbPath = null!;
    private CortanaDbContext _db = null!;
    private MeetingSessionService _sessions = null!;
    private MeetingMessageService _messages = null!;
    private MeetingPendingInputService _pending = null!;
    private MeetingHistoryProvider _history = null!;
    private ServiceProvider _services = null!;
    private IPublisher _publisher = null!;
    private ISubscriber _subscriber = null!;

    [TestInitialize]
    public void Setup()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"cortana-meeting-host-{Guid.NewGuid():N}.db");
        _db = new CortanaDbContext(_dbPath);
        _sessions = new MeetingSessionService(_db);
        _messages = new MeetingMessageService(_db);
        _pending = new MeetingPendingInputService(_db);
        _history = new MeetingHistoryProvider(
            _messages,
            new MeetingCompactionSegmentService(_db),
            new SystemSettingsService(_db),
            _db);
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
    public async Task SelectNextAgentAsync_WhenHostReturnsInvalidId_FallsBackToFirstParticipant()
    {
        const string meetingId = "meeting-host-invalid-id";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var agentB = new ScriptedAgent("agent-b", "专家B");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("not-a-real-agent");
        var manager = CreateManager(meetingId, [agentA, agentB], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentA, picked);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenOnlyMeetingContext_SelectsHostBeforeParticipants()
    {
        const string meetingId = "meeting-host-opening-gate";
        CreateMeeting(meetingId);
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [agentA], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenUserRepliedAfterHostInquiry_SelectsHostAgain()
    {
        const string meetingId = "meeting-host-after-user-reply";
        CreateMeeting(meetingId);
        AppendHostInquiry(meetingId, "需要老板补充成本核算范围。");
        AppendUserReply(meetingId, "按新品首批 1000 件核算。");
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [agentA], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenUserInterruptsAfterParticipantSpeech_SelectsHostForLocalRecovery()
    {
        const string meetingId = "meeting-host-user-interrupt";
        CreateMeeting(meetingId, "新品上市评审");
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-product",
            SpeakerName = "产品部",
            ContentMd = "产品部已经给出了完整的上市版本、规格范围、约束条件和后续需要运营承接的信息。",
            MessageRole = "normal",
        });
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "user",
            SpeakerId = "user",
            SpeakerName = "老板",
            ContentMd = "刚才产品规格说错了，应该按 B 方案继续评审。",
            MessageRole = "interrupt",
        });
        var product = new ScriptedAgent("agent-product", "产品部");
        var finance = new ScriptedAgent("agent-finance", "财务");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var selector = new ScriptedAgent("system-meeting-selector", "会议调度器");
        selector.EnqueueText("agent-finance");
        var manager = CreateManager(meetingId, [product, finance], host, selector);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
        Assert.AreEqual(0, selector.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenPickedParticipantAlreadySpoke_KeepsStructuredDecision()
    {
        const string meetingId = "meeting-host-balance";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            Id = "message-agent-a",
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-a",
            SpeakerName = "专家A",
            ContentMd = "我已经给出了完整的业务判断、关键依据和后续需要补充的风险点。",
            MessageRole = "normal",
        });
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var agentB = new ScriptedAgent("agent-b", "专家B");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("""
            {
              "phase": "follow_up",
              "need": "需要专家A继续补充刚才未完成的信息",
              "nextSpeakerId": "agent-a",
              "reason": "当前信息缺口仍属于专家A职责范围",
              "prerequisites": ["已有专家A上一轮观点"],
              "readyForSpeaker": true
            }
            """);
        var manager = CreateManager(meetingId, [agentA, agentB], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentA, picked);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenParticipantSpokeAndAnotherUnspoken_StillCallsHostSelection()
    {
        const string meetingId = "meeting-host-balance-no-llm";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            Id = "message-agent-a",
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-a",
            SpeakerName = "专家A",
            ContentMd = "我已经给出了完整的业务判断、关键依据和后续需要补充的风险点。",
            MessageRole = "normal",
        });
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var agentB = new ScriptedAgent("agent-b", "专家B");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("agent-a");
        var manager = CreateManager(meetingId, [agentA, agentB], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentA, picked);
        Assert.AreEqual(1, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_UsesSelectorAgentInsteadOfHostForInternalDecision()
    {
        const string meetingId = "meeting-host-selector-channel";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var selector = new ScriptedAgent("system-meeting-selector", "会议调度器");
        selector.EnqueueText("agent-a");
        var manager = CreateManager(meetingId, [agentA], host, selector);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentA, picked);
        Assert.AreEqual(0, host.RunCount);
        Assert.AreEqual(1, selector.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenPickedParticipantHasNotSpoken_KeepsHostDecision()
    {
        const string meetingId = "meeting-host-balance-keeps";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            Id = "message-agent-a",
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-a",
            SpeakerName = "专家A",
            ContentMd = "我已经给出了完整的业务判断、关键依据和后续需要补充的风险点。",
            MessageRole = "normal",
        });
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var agentB = new ScriptedAgent("agent-b", "专家B");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("agent-b");
        var manager = CreateManager(meetingId, [agentA, agentB], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentB, picked);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenDecisionHasDynamicPlan_UsesCurrentPhaseAgent()
    {
        const string meetingId = "meeting-host-dynamic-plan-current-phase";
        CreateMeeting(meetingId, "评审新品上市准备情况");
        AppendHostOpening(meetingId);
        var product = new ScriptedAgent("agent-product", "产品部");
        var operations = new ScriptedAgent("agent-operations", "运营部");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("""
            {
              "executionPlan": {
                "topic": "评审新品上市准备情况",
                "assumptions": ["会议目标是判断上市前是否具备基本条件"],
                "phases": [
                  {
                    "id": "product_readiness",
                    "name": "产品准备度",
                    "goal": "先明确产品范围、版本状态和上市阻塞项",
                    "allowedAgentIds": ["agent-product"],
                    "prerequisites": [],
                    "completionCriteria": "产品部给出产品准备度和关键缺口"
                  },
                  {
                    "id": "operations_plan",
                    "name": "运营承接",
                    "goal": "在产品准备度明确后评估运营承接计划",
                    "allowedAgentIds": ["agent-operations"],
                    "prerequisites": ["产品准备度"],
                    "completionCriteria": "运营部给出上市运营安排"
                  }
                ]
              },
              "currentPhaseId": "product_readiness",
              "phase": "产品准备度",
              "need": "需要先明确产品范围和上市阻塞项",
              "nextSpeakerId": "agent-product",
              "reason": "当前阶段只能由产品部补齐上游信息",
              "prerequisites": [],
              "readyForSpeaker": true
            }
            """);
        var manager = CreateManager(meetingId, [product, operations], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(product, picked);
        Assert.AreEqual(1, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenHostPickedButCurrentPhaseHasAgent_ForcesRealParticipant()
    {
        const string meetingId = "meeting-host-forces-real-participant";
        CreateMeeting(meetingId, "招聘候选人复盘");
        AppendHostOpening(meetingId);
        var hr = new ScriptedAgent("agent-hr", "人力资源");
        var tech = new ScriptedAgent("agent-tech", "技术面试官");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("""
            {
              "executionPlan": {
                "topic": "招聘候选人复盘",
                "assumptions": ["会议目标是形成是否推进候选人的建议"],
                "phases": [
                  {
                    "id": "hr_context",
                    "name": "候选人背景与岗位匹配",
                    "goal": "先由人力资源说明候选人背景、岗位要求和流程状态",
                    "allowedAgentIds": ["agent-hr"],
                    "prerequisites": [],
                    "completionCriteria": "人力资源给出背景和岗位匹配信息"
                  },
                  {
                    "id": "technical_assessment",
                    "name": "技术评估",
                    "goal": "在岗位背景明确后评估技术能力",
                    "allowedAgentIds": ["agent-tech"],
                    "prerequisites": ["候选人背景与岗位要求"],
                    "completionCriteria": "技术面试官给出技术判断"
                  }
                ]
              },
              "currentPhaseId": "hr_context",
              "phase": "候选人背景与岗位匹配",
              "need": "需要人力资源先给出候选人背景",
              "nextSpeakerId": "HOST",
              "reason": "错误地尝试由主持人代述人力资源信息",
              "prerequisites": [],
              "readyForSpeaker": true
            }
            """);
        var manager = CreateManager(meetingId, [hr, tech], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(hr, picked);
        Assert.AreEqual(1, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenHostSummarizesDepartment_DoesNotCountAsPhaseSpeech()
    {
        const string meetingId = "meeting-host-summary-not-agent-speech";
        CreateMeeting(meetingId, "客户投诉复盘");
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            Id = "message-host-summary",
            MeetingId = meetingId,
            SpeakerKind = "host",
            SpeakerId = "system-meeting-host",
            SpeakerName = "主持人",
            ContentMd = "客服部观点如下：客户投诉主要集中在响应时效，需要后续优化。",
            MessageRole = "normal",
        });
        var support = new ScriptedAgent("agent-support", "客服部");
        var operations = new ScriptedAgent("agent-operations", "运营部");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("""
            {
              "executionPlan": {
                "topic": "客户投诉复盘",
                "assumptions": ["会议目标是找出投诉原因和改进责任"],
                "phases": [
                  {
                    "id": "support_facts",
                    "name": "投诉事实确认",
                    "goal": "先由客服部给出真实投诉事实和客户反馈",
                    "allowedAgentIds": ["agent-support"],
                    "prerequisites": [],
                    "completionCriteria": "客服部给出投诉事实"
                  },
                  {
                    "id": "operations_improvement",
                    "name": "运营改进",
                    "goal": "在投诉事实明确后制定运营改进",
                    "allowedAgentIds": ["agent-operations"],
                    "prerequisites": ["投诉事实"],
                    "completionCriteria": "运营部给出改进方案"
                  }
                ]
              },
              "phase": "运营改进",
              "need": "主持人误以为客服部已经给出事实，准备进入运营改进",
              "nextSpeakerId": "agent-operations",
              "reason": "错误地把主持人代述当作客服部发言",
              "prerequisites": ["投诉事实"],
              "readyForSpeaker": true
            }
            """);
        var manager = CreateManager(meetingId, [support, operations], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(support, picked);
        Assert.AreEqual(1, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenLatestParticipantSpeechIsPlaceholder_SelectsHostForRecovery()
    {
        const string meetingId = "meeting-host-placeholder-recovery";
        CreateMeeting(meetingId, "核算新品成本");
        AppendHostOpening(meetingId);
        _messages.Append(new MeetingMessageEntity
        {
            Id = "message-product-placeholder",
            MeetingId = meetingId,
            SpeakerKind = "agent",
            SpeakerId = "agent-product",
            SpeakerName = "产品部",
            ContentMd = "我先查看附件，稍后再给出产品规格和数量建议。",
            MessageRole = "normal",
        });
        var product = new ScriptedAgent("agent-product", "产品部");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [product], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenDecisionIsNotReady_ReturnsHostToClarify()
    {
        const string meetingId = "meeting-host-not-ready";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        var finance = new ScriptedAgent("agent-finance", "财务");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("""
            {
              "phase": "scope_clarification",
              "need": "需要老板确认成本核算范围",
              "nextSpeakerId": "agent-finance",
              "reason": "财务有效测算前需要先明确范围",
              "prerequisites": ["成本核算范围"],
              "readyForSpeaker": false
            }
            """);
        var manager = CreateManager(meetingId, [finance], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenMalformedStructuredDecision_DoesNotFuzzyMatchParticipant()
    {
        const string meetingId = "meeting-host-malformed-structured-json";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        var finance = new ScriptedAgent("agent-finance", "财务");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var selector = new ScriptedAgent("system-meeting-selector", "会议调度器");
        selector.EnqueueText("""{ "nextSpeakerId": "agent-finance", "readyForSpeaker": true """);
        var manager = CreateManager(meetingId, [finance], host, selector);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
        Assert.AreEqual(1, selector.RunCount);
    }


    [TestMethod]
    public async Task SelectNextAgentAsync_WhenPendingRequestTerminates_CanReturnHost()
    {
        const string meetingId = "meeting-host-pending-terminate";
        CreateMeeting(meetingId);
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [agentA], host);

        var selectTask = SelectNextAgentAsync(manager);
        await Task.Delay(100);
        manager.SetTerminationFlag();
        var picked = await selectTask.ConfigureAwait(false);

        Assert.AreSame(host, picked);
        Assert.AreEqual(0, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenNetworkErrorOccurs_RetriesAndPublishesRetryEvent()
    {
        const string meetingId = "meeting-host-retry";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueError(new TimeoutException("selector timeout"));
        host.EnqueueText("agent-a");
        var retrying = new List<MeetingLlmRetryingArgs>();
        _subscriber.Subscribe<MeetingLlmRetryingArgs>(
            Events.OnMeetingLlmRetrying,
            (_, args) =>
            {
                retrying.Add(args);
                return Task.FromResult(false);
            });
        var manager = CreateManager(meetingId, [agentA], host);

        var picked = await SelectNextAgentAsync(manager);

        Assert.AreSame(agentA, picked);
        Assert.HasCount(1, retrying);
        Assert.AreEqual(meetingId, retrying[0].MeetingId);
        Assert.AreEqual("SelectNextAgentAsync", retrying[0].OperationName);
        Assert.AreEqual(1, retrying[0].Attempt);
        Assert.AreEqual(5, retrying[0].MaxAttempts);
    }

    [TestMethod]
    public async Task ShouldTerminateAsync_WhenTerminationFlagIsSet_ReturnsTrue()
    {
        const string meetingId = "meeting-host-terminate";
        CreateMeeting(meetingId);
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [new ScriptedAgent("agent-a", "专家A")], host);

        manager.SetTerminationFlag();

        Assert.IsTrue(await ShouldTerminateAsync(manager));
    }

    [TestMethod]
    public async Task ShouldTerminateAsync_WhenMeetingAlreadyCompleted_ReturnsTrueWithoutCallingHost()
    {
        const string meetingId = "meeting-host-completed";
        CreateMeeting(meetingId);
        _messages.AppendSummary(meetingId, "最终总结");
        _sessions.SetFinalSummary(meetingId, "最终总结");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        var manager = CreateManager(meetingId, [new ScriptedAgent("agent-a", "专家A")], host);

        var shouldTerminate = await ShouldTerminateAsync(manager);

        Assert.IsTrue(shouldTerminate);
        Assert.AreEqual(0, host.RunCount);
    }

    [TestMethod]
    public async Task SelectNextAgentAsync_WhenPendingRequestExists_WaitsForUserReplyBeforeCallingHost()
    {
        const string meetingId = "meeting-host-wait";
        CreateMeeting(meetingId);
        AppendHostOpening(meetingId);
        _sessions.SetPending(meetingId, "request-1", "ask_user", "{}");
        var agentA = new ScriptedAgent("agent-a", "专家A");
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueText("agent-a");
        var manager = CreateManager(meetingId, [agentA], host);

        var selectTask = SelectNextAgentAsync(manager);
        await Task.Delay(100);

        Assert.IsFalse(selectTask.IsCompleted, "存在 PendingRequestId 时，主持人选人应阻塞等待用户回复。");
        Assert.AreEqual(0, host.RunCount, "阻塞期间不应提前调用主持人 LLM。");

        manager.OnUserReplied();
        var picked = await selectTask.ConfigureAwait(false);

        Assert.AreSame(agentA, picked);
        Assert.AreEqual(1, host.RunCount);
    }

    [TestMethod]
    public async Task ShouldTerminateAsync_WhenMaximumIterationReached_ForcesSummaryAndSetsFinalSummary()
    {
        const string meetingId = "meeting-host-max-iteration";
        CreateMeeting(meetingId);
        var host = new ScriptedAgent("system-meeting-host", "主持人");
        host.EnqueueCallback(() => _messages.AppendSummary(meetingId, "最终总结"));
        var manager = CreateManager(meetingId, [new ScriptedAgent("agent-a", "专家A")], host);
        SetIterationCount(manager, 40);

        var shouldTerminate = await ShouldTerminateAsync(manager);

        Assert.IsTrue(shouldTerminate);
        Assert.AreEqual(1, host.RunCount);
        Assert.AreEqual("最终总结", _sessions.GetById(meetingId)?.FinalSummaryMd);
    }

    private MeetingHostManager CreateManager(
        string meetingId,
        IReadOnlyList<AIAgent> participants,
        AIAgent host,
        AIAgent? selector = null)
    {
        var manager = new MeetingHostManager(
            participants,
            meetingId,
            _sessions,
            _pending,
            _messages,
            _history,
            _publisher,
            NullLogger<MeetingHostManager>.Instance);
        manager.SetHostAgent(host);
        manager.SetSelectorAgent(selector ?? host);
        return manager;
    }

    private static async Task<AIAgent> SelectNextAgentAsync(MeetingHostManager manager)
    {
        var method = typeof(MeetingHostManager).GetMethod(
            "SelectNextAgentAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MeetingHostManager), "SelectNextAgentAsync");

        var task = (ValueTask<AIAgent>)method.Invoke(
            manager,
            [Array.Empty<ChatMessage>(), CancellationToken.None])!;
        return await task.ConfigureAwait(false);
    }

    private static async Task<bool> ShouldTerminateAsync(MeetingHostManager manager)
    {
        var method = typeof(MeetingHostManager).GetMethod(
            "ShouldTerminateAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new MissingMethodException(nameof(MeetingHostManager), "ShouldTerminateAsync");

        var task = (ValueTask<bool>)method.Invoke(
            manager,
            [Array.Empty<ChatMessage>(), CancellationToken.None])!;
        return await task.ConfigureAwait(false);
    }

    private static void SetIterationCount(MeetingHostManager manager, int iterationCount)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        for (var type = manager.GetType(); type is not null; type = type.BaseType)
        {
            var property = type.GetProperty("IterationCount", flags);
            if (property?.SetMethod is not null)
            {
                property.SetValue(manager, iterationCount);
                return;
            }

            foreach (var field in type.GetFields(flags))
            {
                if (field.FieldType == typeof(int) &&
                    field.Name.Contains("IterationCount", StringComparison.OrdinalIgnoreCase))
                {
                    field.SetValue(manager, iterationCount);
                    return;
                }
            }
        }

        throw new MissingMemberException("无法定位 GroupChatManager.IterationCount。");
    }

    private void AppendHostOpening(string meetingId)
    {
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "host",
            SpeakerId = "system-meeting-host",
            SpeakerName = "主持人",
            ContentMd = "本次会议目标清晰：先确认业务边界，再按前置依赖推进讨论。",
            MessageRole = "normal",
        });
    }

    private void AppendHostInquiry(string meetingId, string content)
    {
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "host",
            SpeakerId = "system-meeting-host",
            SpeakerName = "主持人",
            ContentMd = content,
            MessageRole = "inquiry",
            AwaitingUserReply = true,
        });
    }

    private void AppendUserReply(string meetingId, string content)
    {
        _messages.Append(new MeetingMessageEntity
        {
            MeetingId = meetingId,
            SpeakerKind = "user",
            SpeakerId = "user",
            SpeakerName = "老板",
            ContentMd = content,
            MessageRole = "normal",
        });
    }

    private void CreateMeeting(string meetingId, string topic = "主持人决策测试")
    {
        const string sessionId = "session-host";
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
            WorkspaceId = "workspace-host",
            Topic = topic,
            ParticipantsJson = "[]",
            Provider = "provider-test",
            Model = "model-test",
        });
    }

    private sealed class ScriptedAgent(string id, string name) : AIAgent
    {
        private readonly Queue<object> _responses = new();

        protected override string IdCore => id;

        public override string Name => name;

        public override string Description => string.Empty;

        public int RunCount { get; private set; }

        public void EnqueueText(string text)
        {
            _responses.Enqueue(text);
        }

        public void EnqueueError(Exception exception)
        {
            _responses.Enqueue(exception);
        }

        public void EnqueueCallback(Action callback)
        {
            _responses.Enqueue(callback);
        }

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
            cancellationToken.ThrowIfCancellationRequested();
            RunCount++;
            if (!_responses.TryDequeue(out var response))
            {
                throw new InvalidOperationException($"测试 Agent {id} 没有可用响应。");
            }

            if (response is Exception exception)
            {
                throw exception;
            }

            if (response is Action callback)
            {
                callback();
                return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, string.Empty))
                {
                    AgentId = id,
                    ResponseId = Guid.NewGuid().ToString("N"),
                });
            }

            return Task.FromResult(new AgentResponse(new ChatMessage(ChatRole.Assistant, (string)response))
            {
                AgentId = id,
                ResponseId = Guid.NewGuid().ToString("N"),
            });
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
