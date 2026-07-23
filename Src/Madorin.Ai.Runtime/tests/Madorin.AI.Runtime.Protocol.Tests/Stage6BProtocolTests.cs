using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class Stage6BProtocolTests
{
    private static readonly DateTimeOffset RequestedAt =
        new(2026, 7, 23, 8, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void MeetingRequest_FullContract_RoundTripsWithDefaults()
    {
        var request = CreateMeetingRequest();

        var json = JsonSerializer.Serialize(
            request,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        var options = actual.Selection.ModeOptions as MeetingModeOptions;
        Assert.IsNotNull(options);
        Assert.AreEqual(MeetingSelectorPolicy.SelectorDriven, options.EffectiveSelectorPolicy.Type);
        Assert.IsTrue(options.EffectiveSelectorPolicy.AllowStandbyActivation);
        Assert.IsTrue(options.EffectiveContextPolicy.PreserveCurrentRound);
        Assert.AreEqual(MeetingSummaryMode.None, options.EffectivePolicy.SummaryMode);
        Assert.AreEqual(1, options.EffectivePolicy.MaxRetriesPerInvocation);
        Assert.AreEqual(
            MeetingSelectorFailurePolicy.DeterministicFallback,
            options.EffectivePolicy.SelectorFailure);
        Assert.HasCount(2, options.Participants);
        Assert.AreEqual("participant-1", options.Participants[0].ParticipantId);
        Assert.AreEqual("agent-participant-1", options.Participants[0].Agent.AgentId);
        StringAssert.Contains(json, "\"participantId\":\"participant-1\"");
        StringAssert.Contains(json, "\"selectorPolicy\":");
        StringAssert.Contains(json, "\"contextPolicy\":");
    }

    [TestMethod]
    public void MeetingRequest_LegacyAgentArray_DeserializesAndPreservesWireShape()
    {
        const string json =
            """
            {
              "sessionIdempotencyKey":"session-key",
              "runIdempotencyKey":"run-key",
              "mode":"Meeting",
              "selection":{
                "selectionVersion":1,
                "mode":"Meeting",
                "defaultSelection":{"providerId":"fake","modelId":"model"},
                "modeOptions":{
                  "mode":"meeting",
                  "participants":[
                    {"agentId":"agent-1","promptTemplateVersion":"v1","systemPrompt":"One"},
                    {"agentId":"agent-2","promptTemplateVersion":"v1","systemPrompt":"Two"}
                  ],
                  "hostAgentId":"agent-1"
                }
              },
              "initialInput":[{"type":"text","text":"Discuss"}]
            }
            """;

        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        var options = actual.Selection.ModeOptions as MeetingModeOptions;
        Assert.IsNotNull(options);
        Assert.HasCount(2, options.Participants);
        Assert.AreEqual("agent-1", options.Participants[0].ParticipantId);
        Assert.AreEqual(
            MeetingSelectorPolicy.SelectorDriven,
            options.EffectiveSelectorPolicy.Type);
        Assert.IsTrue(options.EffectiveContextPolicy.PreserveCurrentRound);

        var roundTrip = JsonSerializer.Serialize(
            actual,
            RuntimeJsonContext.Default.NewSessionRunRequest);
        StringAssert.Contains(roundTrip, "\"agentId\":\"agent-1\"");
        Assert.IsFalse(roundTrip.Contains("\"participantId\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ExpertRequest_LegacyShape_StillDeserializes()
    {
        const string json =
            """
            {
              "sessionIdempotencyKey":"session-key",
              "runIdempotencyKey":"run-key",
              "mode":"Expert",
              "selection":{
                "selectionVersion":1,
                "mode":"Expert",
                "defaultSelection":{"providerId":"fake","modelId":"model"},
                "modeOptions":{
                  "mode":"expert",
                  "agent":{"agentId":"expert","promptTemplateVersion":"v1","systemPrompt":"Help"}
                }
              },
              "initialInput":[{"type":"text","text":"Hello"}]
            }
            """;

        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.NewSessionRunRequest);

        Assert.IsNotNull(actual);
        var options = Assert.IsInstanceOfType<ExpertModeOptions>(
            actual.Selection.ModeOptions);
        Assert.IsNull(options.Agent.AllowedToolIds);
        Assert.IsNull(options.Agent.SkillIds);
    }

    [TestMethod]
    public void MeetingParticipant_AgentToolsAndSkills_RoundTrip()
    {
        var participant = new MeetingParticipant(
            "participant-tools",
            new AgentRef(
                "agent-tools",
                "v1",
                "Use only the assigned capabilities.",
                AllowedToolIds: ["builtin.fs.read", "host.erp.lookup"],
                SkillIds: ["finance", "risk-review"]),
            "Tool participant",
            0);

        var json = JsonSerializer.Serialize(
            participant,
            RuntimeJsonContext.Default.MeetingParticipant);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.MeetingParticipant);

        Assert.IsNotNull(actual);
        CollectionAssert.AreEqual(
            participant.Agent.AllowedToolIds,
            actual.Agent.AllowedToolIds);
        CollectionAssert.AreEqual(
            participant.Agent.SkillIds,
            actual.Agent.SkillIds);
        StringAssert.Contains(json, "\"allowedToolIds\":[");
        StringAssert.Contains(json, "\"skillIds\":[");
    }

    [TestMethod]
    public void SelectionUpdate_PatchOperations_RoundTripPolymorphically()
    {
        MeetingParticipantPatch[] patches =
        [
            new AddParticipantPatch(CreateParticipant("participant-3", 2)),
            new RemoveParticipantPatch("participant-1"),
            new UpdateParticipantPatch(CreateParticipant("participant-2", 1)),
            new UpdateParticipantStatusPatch("participant-2", ParticipantStatus.Standby),
            new ReorderParticipantsPatch(
            [
                new ParticipantJoinOrder("participant-2", 0),
                new ParticipantJoinOrder("participant-3", 1)
            ])
        ];
        var update = new SessionSelectionUpdateParameters(
            "session-1",
            7,
            MeetingPatches: patches);

        var json = JsonSerializer.Serialize(
            update,
            RuntimeJsonContext.Default.SessionSelectionUpdateParameters);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.SessionSelectionUpdateParameters);

        Assert.IsNotNull(actual);
        Assert.IsNull(actual.Selection);
        Assert.IsNotNull(actual.MeetingPatches);
        Assert.HasCount(5, actual.MeetingPatches);
        Assert.IsInstanceOfType<AddParticipantPatch>(actual.MeetingPatches[0]);
        Assert.IsInstanceOfType<RemoveParticipantPatch>(actual.MeetingPatches[1]);
        Assert.IsInstanceOfType<UpdateParticipantPatch>(actual.MeetingPatches[2]);
        Assert.IsInstanceOfType<UpdateParticipantStatusPatch>(actual.MeetingPatches[3]);
        Assert.IsInstanceOfType<ReorderParticipantsPatch>(actual.MeetingPatches[4]);
        StringAssert.Contains(json, "\"op\":\"add\"");
        StringAssert.Contains(json, "\"op\":\"reorder\"");
    }

    [TestMethod]
    public void MeetingHitl_Actions_RoundTripWithoutToolIdentifiers()
    {
        var request = new MeetingHitlRequest(
            "approval-1",
            "session-1",
            "run-1",
            2,
            "host-invocation-1",
            "Approve the recommendation?",
            [new TextContentBlock("Meeting context")],
            RequestedAt,
            RequestedAt.AddMinutes(5));
        var requestJson = JsonSerializer.Serialize(
            request,
            RuntimeJsonContext.Default.MeetingHitlRequest);

        Assert.IsFalse(requestJson.Contains("toolId", StringComparison.Ordinal));
        Assert.IsFalse(requestJson.Contains("callId", StringComparison.Ordinal));

        foreach (var action in Enum.GetValues<MeetingHitlAction>())
        {
            var response = new MeetingHitlResponse(
                request.ApprovalRequestId,
                action,
                action is MeetingHitlAction.Supplement ? "Add cost analysis." : null,
                RespondedAt: RequestedAt.AddMinutes(1));
            var json = JsonSerializer.Serialize(
                response,
                RuntimeJsonContext.Default.MeetingHitlResponse);
            var actual = JsonSerializer.Deserialize(
                json,
                RuntimeJsonContext.Default.MeetingHitlResponse);

            Assert.IsNotNull(actual);
            Assert.AreEqual(action, actual.Action);
        }
    }

    [TestMethod]
    public void SessionResume_MeetingState_RoundTripsScheduledAndInterruptedStates()
    {
        var meetingState = new MeetingResumeState(
            MeetingSessionStatus.WaitingForApproval,
            3,
            [CreateParticipant("participant-1", 0), CreateParticipant("participant-2", 1)],
            9,
            new MeetingScheduledInvocation(
                "invocation-next",
                3,
                MeetingInvocationRole.Participant,
                "participant-2",
                "agent-participant-2",
                9,
                MeetingInvocationStatus.Scheduled),
            new MeetingHitlRequest(
                "approval-1",
                "session-1",
                "run-1",
                3,
                "host-invocation-1",
                "Continue?",
                null,
                RequestedAt,
                null),
            new MeetingSummary(
                "summary-message-1",
                2,
                42,
                "policy-hash",
                "summarizer-agent",
                "summarizer-invocation"));
        var result = new SessionResumeResult(
            "session-1",
            RuntimeMode.Meeting,
            9,
            null,
            [],
            null,
            100,
            "checkpoint-1",
            true,
            [],
            MeetingState: meetingState);

        var json = JsonSerializer.Serialize(
            result,
            RuntimeJsonContext.Default.SessionResumeResult);
        var actual = JsonSerializer.Deserialize(
            json,
            RuntimeJsonContext.Default.SessionResumeResult);

        Assert.IsNotNull(actual?.MeetingState);
        Assert.AreEqual(
            MeetingInvocationStatus.Scheduled,
            actual.MeetingState.NextScheduledInvocation?.Status);
        Assert.AreEqual(42L, actual.MeetingState.RecentSummary?.SummarizesThroughSeq);
        StringAssert.Contains(json, "\"summarizesThroughSeq\":42");

        var interrupted = meetingState with
        {
            NextScheduledInvocation = meetingState.NextScheduledInvocation! with
            {
                Status = MeetingInvocationStatus.Interrupted
            }
        };
        Assert.AreEqual(
            MeetingInvocationStatus.Interrupted,
            interrupted.NextScheduledInvocation?.Status);
    }

    [TestMethod]
    public void RuntimeJsonContext_ContainsMeetingMetadata()
    {
        Assert.IsNotNull(RuntimeJsonContext.Default.MeetingModeOptions);
        Assert.IsNotNull(RuntimeJsonContext.Default.MeetingParticipantPatch);
        Assert.IsNotNull(RuntimeJsonContext.Default.MeetingHitlRequest);
        Assert.IsNotNull(RuntimeJsonContext.Default.MeetingResumeState);
        Assert.IsNotNull(RuntimeJsonContext.Default.MeetingSummary);
    }

    private static NewSessionRunRequest CreateMeetingRequest()
    {
        var options = new MeetingModeOptions(
            [CreateParticipant("participant-1", 0), CreateParticipant("participant-2", 1)],
            selectorPolicy: new MeetingSelectorPolicyOptions(),
            policy: new MeetingPolicy(),
            contextPolicy: new MeetingContextPolicy());
        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Meeting,
            new NextTurnSelection(
                1,
                RuntimeMode.Meeting,
                new DefaultSelection("fake", "model"),
                options),
            [new TextContentBlock("Discuss")]);
    }

    private static MeetingParticipant CreateParticipant(string participantId, int joinOrder) =>
        new(
            participantId,
            new AgentRef($"agent-{participantId}", "v1", $"Prompt for {participantId}"),
            participantId,
            joinOrder);
}
