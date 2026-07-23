using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class MeetingResumeMapperTests
{
    private static readonly DateTimeOffset Timestamp = new(2026, 7, 23, 0, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void BuildMeetingResumeState_CompleteSnapshot_MapsDurableMeetingState()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");
        var agent2 = new AgentRef("agent-2", "v1", "You are agent 2");
        var agent3 = new AgentRef("agent-3", "v1", "You are agent 3");

        var participants = new[]
        {
            CreateParticipant("p1", agent1, "Agent One", 1, ParticipantStatus.Active),
            CreateParticipant("p2", agent2, "Agent Two", 2, ParticipantStatus.Standby),
            CreateParticipant("p3", agent3, "Agent Three", 3, ParticipantStatus.Removed),
        };

        var invocation = new MeetingInvocationRecord(
            "inv-1", "session-1", "run-1", 2, 1, "p1", "agent-1", "Participant",
            "Scheduled", 7, 0, null, null,
            Timestamp, null, null, null, null);

        var hitl = new MeetingHitlRequest(
            "approval-1", "session-1", "run-1", 2, "inv-1", "Please approve",
            null, Timestamp, null);
        var hitlJson = JsonSerializer.Serialize(hitl, RuntimeJsonContext.Default.MeetingHitlRequest);

        var summary = new MeetingRoundRecord(
            "session-1", 1, "run-1", "Completed", "inv-0", "msg-summary",
            10L, "hash-summary", "agent-1", "inv-summary",
            Timestamp, Timestamp, Timestamp);

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null,
            invocation,
            hitlJson,
            "Pending",
            summary);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1, agent2, agent3);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNotNull(result);

        var mappedParticipants = result.Participants;
        Assert.HasCount(3, mappedParticipants);

        Assert.AreEqual("p1", mappedParticipants[0].ParticipantId);
        Assert.AreEqual("agent-1", mappedParticipants[0].Agent.AgentId);
        Assert.AreEqual(string.Empty, mappedParticipants[0].Agent.SystemPrompt);

        Assert.AreEqual("p2", mappedParticipants[1].ParticipantId);
        Assert.AreEqual("agent-2", mappedParticipants[1].Agent.AgentId);
        Assert.AreEqual(string.Empty, mappedParticipants[1].Agent.SystemPrompt);

        Assert.AreEqual("p3", mappedParticipants[2].ParticipantId);
        Assert.AreEqual("agent-3", mappedParticipants[2].Agent.AgentId);
        Assert.AreEqual(string.Empty, mappedParticipants[2].Agent.SystemPrompt);

        Assert.IsNotNull(result.NextScheduledInvocation);
        Assert.AreEqual("inv-1", result.NextScheduledInvocation.InvocationId);

        Assert.IsNotNull(result.PendingApproval);
        Assert.AreEqual("approval-1", result.PendingApproval.ApprovalRequestId);

        Assert.IsNotNull(result.RecentSummary);
        Assert.AreEqual("msg-summary", result.RecentSummary.SummaryMessageId);

        Assert.IsEmpty(diagnostics);
    }

    [TestMethod]
    public void BuildMeetingResumeState_InvalidAgentJson_UsesSelectionFallbackWithoutDiagnostic()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");
        var agent2 = new AgentRef("agent-2", "v1", "You are agent 2");

        var participants = new[]
        {
            CreateParticipant("p1", agent1, "Agent One", 1, ParticipantStatus.Active),
            new MeetingParticipantRecord(
                "session-1", "p2", "agent-2", "{{invalid json",
                "Agent Two", 2, "Active", 7, null,
                Timestamp, null, Timestamp),
        };

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null, null, null, null, null);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1, agent2);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNotNull(result);

        var p2 = result.Participants[1];
        Assert.AreEqual("p2", p2.ParticipantId);
        Assert.AreEqual("agent-2", p2.Agent.AgentId);
        Assert.AreEqual(string.Empty, p2.Agent.SystemPrompt);

        Assert.IsEmpty(diagnostics);
    }

    [TestMethod]
    public void BuildMeetingResumeState_InvalidParticipantStatus_ReturnsNullWithDiagnostic()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");

        var participants = new[]
        {
            new MeetingParticipantRecord(
                "session-1", "p1", "agent-1",
                JsonSerializer.Serialize(agent1, RuntimeJsonContext.Default.AgentRef),
                "Agent One", 1, "UnknownStatus", 7, null,
                Timestamp, null, Timestamp),
        };

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null, null, null, null, null);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNull(result);
        Assert.IsNotEmpty(diagnostics);
        StringAssert.Contains(diagnostics[0], "p1");
        StringAssert.Contains(diagnostics[0], "UnknownStatus");
    }

    [TestMethod]
    public void BuildMeetingResumeState_InvalidInvocationRole_ReturnsMeetingWithoutNextInvocation()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");

        var participants = new[]
        {
            CreateParticipant("p1", agent1, "Agent One", 1, ParticipantStatus.Active),
        };

        var invocation = new MeetingInvocationRecord(
            "inv-1", "session-1", "run-1", 2, 1, "p1", "agent-1", "UnknownRole",
            "Scheduled", 7, 0, null, null,
            Timestamp, null, null, null, null);

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null,
            invocation,
            null, null, null);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNotNull(result);
        Assert.IsNull(result.NextScheduledInvocation);
        Assert.IsNotEmpty(diagnostics);
    }

    [TestMethod]
    public void BuildMeetingResumeState_IncompleteSummary_OmitsSummaryWithDiagnostic()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");

        var participants = new[]
        {
            CreateParticipant("p1", agent1, "Agent One", 1, ParticipantStatus.Active),
        };

        var summary = new MeetingRoundRecord(
            "session-1", 1, "run-1", "Completed", "inv-0", "msg-summary",
            10L, "hash-summary", "agent-1", null,
            Timestamp, Timestamp, Timestamp);

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null, null, null, null, summary);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNotNull(result);
        Assert.IsNull(result.RecentSummary);
        Assert.IsNotEmpty(diagnostics);
    }

    [TestMethod]
    public void BuildMeetingResumeState_PendingApprovalWithoutJson_OmitsApprovalWithDiagnostic()
    {
        var agent1 = new AgentRef("agent-1", "v1", "You are agent 1");

        var participants = new[]
        {
            CreateParticipant("p1", agent1, "Agent One", 1, ParticipantStatus.Active),
        };

        var meetingSnapshot = new MeetingSnapshot(
            CreateDefaultSessionRecord(),
            participants,
            null, null, null, "Pending", null);

        var sessionSnapshot = CreateDefaultSessionSnapshot();
        var selection = CreateDefaultSelection(agent1);
        var diagnostics = new List<string>();

        var result = RuntimeServer.BuildMeetingResumeState(meetingSnapshot, sessionSnapshot, selection, diagnostics);

        Assert.IsNotNull(result);
        Assert.IsNull(result.PendingApproval);
        Assert.IsNotEmpty(diagnostics);
    }

    [TestMethod]
    public void CreateNeededDefinitions_MeetingSelection_IncludesEveryConfiguredRole()
    {
        var active = new AgentRef("agent-active", "v1", "Active prompt");
        var standby = new AgentRef("agent-standby", "v1", "Standby prompt");
        var host = new AgentRef("agent-host", "v1", "Host prompt");
        var selector = new AgentRef("agent-selector", "v1", "Selector prompt");
        var summarizer = new AgentRef("agent-summarizer", "v1", "Summarizer prompt");
        var selection = new NextTurnSelection(
            7,
            RuntimeMode.Meeting,
            new DefaultSelection("provider-1", "model-1"),
            new MeetingModeOptions(
            [
                new MeetingParticipant("p-active", active, "Active", 0),
                new MeetingParticipant(
                    "p-standby",
                    standby,
                    "Standby",
                    1,
                    ParticipantStatus.Standby)
            ],
            hostAgent: host,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                MeetingSelectorPolicy.SelectorDriven,
                selector),
            policy: new MeetingPolicy(
                SummaryMode: MeetingSummaryMode.PerRound,
                Summarizer: summarizer)),
            ToolCatalogVersion: "tools-v1");
        var snapshot = new PersistedSessionSnapshot(
            "session-1",
            RuntimeMode.Meeting,
            SessionStatus.Active,
            Timestamp,
            Timestamp,
            selection.SelectionVersion,
            SelectionJson: null,
            LatestRun: null,
            RuntimeRunMetadata.CreateAgentSnapshots(selection),
            LastGsn: 0);

        var result = RuntimeServer.CreateNeededDefinitions(snapshot, selection);

        Assert.HasCount(5, result);
        AssertDefinition(result, active, "MeetingParticipant", "p-active");
        AssertDefinition(result, standby, "MeetingParticipant", "p-standby");
        AssertDefinition(result, host, "MeetingHost", participantId: null);
        AssertDefinition(result, selector, "MeetingSelector", participantId: null);
        AssertDefinition(result, summarizer, "MeetingSummarizer", participantId: null);
    }

    private static MeetingParticipantRecord CreateParticipant(
        string participantId, AgentRef agent, string displayName,
        int joinOrder, ParticipantStatus status)
    {
        return new MeetingParticipantRecord(
            "session-1", participantId, agent.AgentId,
            JsonSerializer.Serialize(agent, RuntimeJsonContext.Default.AgentRef),
            displayName, joinOrder, status.ToString(), 7, null,
            Timestamp, null, Timestamp);
    }

    private static void AssertDefinition(
        IEnumerable<NeededAgentDefinition> definitions,
        AgentRef agent,
        string definitionType,
        string? participantId)
    {
        var definition = definitions.Single(item =>
            string.Equals(item.AgentId, agent.AgentId, StringComparison.Ordinal));
        Assert.AreEqual(definitionType, definition.DefinitionType);
        Assert.AreEqual(participantId, definition.ParticipantId);
        Assert.AreEqual(
            RuntimeRunMetadata.ComputePromptHash(agent.SystemPrompt),
            definition.ResolvedPromptHash);
    }

    private static MeetingSessionRecord CreateDefaultSessionRecord()
    {
        return new MeetingSessionRecord(
            "session-1", "run-1", "Running", 2, null,
            "{}", "policy-hash", 7, null, null, null,
            Timestamp, Timestamp);
    }

    private static PersistedSessionSnapshot CreateDefaultSessionSnapshot()
    {
        var selection = CreateDefaultSelection(new AgentRef("agent-1", "v1", "You are agent 1"));
        return new PersistedSessionSnapshot(
            "session-1", RuntimeMode.Meeting, SessionStatus.Active,
            Timestamp, Timestamp, 7,
            JsonSerializer.Serialize(selection, RuntimeJsonContext.Default.NextTurnSelection),
            null, Array.Empty<AgentSnapshot>(), 0L);
    }

    private static NextTurnSelection CreateDefaultSelection(params AgentRef[] agents)
    {
        var participants = agents.Select((a, i) =>
            new MeetingParticipant(
                $"p{i + 1}", a, $"Agent {i + 1}", i + 1, ParticipantStatus.Active)).ToArray();
        return new NextTurnSelection(
            7, RuntimeMode.Meeting,
            new DefaultSelection("provider-1", "model-1"),
            (ModeOptions)new MeetingModeOptions(participants),
            null, "0");
    }
}
