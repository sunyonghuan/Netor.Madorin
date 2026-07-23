using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Meeting;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class MeetingModeOrchestratorTests
{
    private static readonly string[] ExpectedParticipantIds = ["participant-b", "participant-a"];
    private static readonly string[] ExpectedFirstParticipantId = ["p-a"];
    private static readonly string[] ExpectedTerminatedMeetingAgentIds =
        ["p-a", "meeting.summarizer"];
    private static readonly string[] ExpectedDynamicParticipantIds = ["p-a", "p-c"];
    private static readonly string[] ExpectedPerRoundSummaryAgentIds =
    [
        "p-a", "meeting.summarizer",
        "p-b", "meeting.summarizer",
        "p-a", "meeting.summarizer"
    ];
    private static readonly string[] ExpectedFinalOnlySummaryAgentIds =
        ["p-a", "p-b", "meeting.summarizer"];
    private static readonly string[] ExpectedHostConcludedSummaryAgentIds =
        ["meeting.host", "meeting.summarizer"];
    private static readonly string[] ExpectedTailWindowFallbackAgentIds =
        ["p-a", "meeting.summarizer", "meeting.summarizer", "p-b", "meeting.summarizer"];
    private static readonly string[] ExpectedPersistedRoleAgentIds =
        ["p-b", "meeting.selector", "meeting.host", "meeting.summarizer"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunAsync_TwoParticipants_RunsInJoinOrderAndEmitsLifecycleEvents()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using (var connection = new SqliteConnection(connectionString))
            {
                await SqliteSchema.EnsureCreatedAsync(connection, ct);
                using var outbox = new SqliteEventOutbox(connection);
                var provider = new FakeProviderAdapter();
                var memoryFiles = new MemoryFileService(
                    Path.Join(dataDirectory, "home"),
                    Path.Join(dataDirectory, "workspace"));
                await memoryFiles.ReplaceAsync(
                    MemoryScope.Global,
                    "# Memory\n\n- Shared global rule.\n",
                    ct);
                await memoryFiles.ReplaceAsync(
                    MemoryScope.Project,
                    "# Memory\n\n- Shared project rule.\n",
                    ct);
                var store = new ConversationStore(dataDirectory);
                var sessionRepo = new SqliteSessionRepository(connection);
                var meetingRepo = new SqliteMeetingRepository(connection);
                var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);
                var orchestrator = new MeetingModeOrchestrator(
                    _ => provider,
                    store,
                    sessionRepo,
                    meetingRepo,
                    outbox,
                    new AgentContextComposer(memoryFiles));
                var participants = new[]
                {
                    new AgentRef("participant-b", "v1", "You are participant B.", "fake", "model-b"),
                    new AgentRef("participant-a", "v1", "You are participant A.", "fake", "model-a")
                };
                var request = new NewSessionRunRequest(
                    "session-key",
                    "run-key",
                    RuntimeMode.Meeting,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Meeting,
                        new DefaultSelection("fake", "test-model"),
                        new MeetingModeOptions(participants)
                        {
                            SelectorPolicy = new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin)
                        },
                        ToolCatalogVersion: "tools-v1"),
                    [new TextContentBlock("Discuss the release plan.")]);
                var events = new List<RuntimeEventEnvelope>();

                await foreach (var envelope in orchestrator.RunAsync(
                    runId,
                    sessionId,
                    request,
                    "runtime-1",
                    ct))
                {
                    events.Add(envelope);
                }

                Assert.AreEqual(MessageTypes.RunAccepted, events[0].MessageType);
                Assert.AreEqual(MessageTypes.RunCompleted, events.Last().MessageType);
                CollectionAssert.AreEqual(
                    ExpectedParticipantIds,
                    provider.InvokedAgentIds.ToArray());
                Assert.HasCount(2, provider.CapturedRequests);
                foreach (var providerRequest in provider.CapturedRequests)
                {
                    var instructions = ((TextContentBlock)providerRequest.Messages[0].Content[0]).Text;
                    StringAssert.Contains(instructions, "Shared global rule.");
                    StringAssert.Contains(instructions, "Shared project rule.");
                    Assert.IsLessThan(
                        instructions.IndexOf("Shared project rule.", StringComparison.Ordinal),
                        instructions.IndexOf("Shared global rule.", StringComparison.Ordinal));
                }

                var started = events
                    .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
                    .Select(static item => item.Payload.Deserialize(
                        RuntimeJsonContext.Default.InvocationStartedEvent)
                        ?? throw new InvalidDataException("The invocation.started payload was empty."))
                    .ToArray();
                var completed = events
                    .Where(static item => item.MessageType == MessageTypes.InvocationCompleted)
                    .Select(static item => item.Payload.Deserialize(
                        RuntimeJsonContext.Default.InvocationCompletedEvent)
                        ?? throw new InvalidDataException("The invocation.completed payload was empty."))
                    .ToArray();

                Assert.HasCount(2, started);
                CollectionAssert.AreEqual(
                    ExpectedParticipantIds,
                    started.Select(static item => item.Snapshot.AgentId).ToArray());
                CollectionAssert.AreEqual(
                    started.Select(static item => item.InvocationId).ToArray(),
                    completed.Select(static item => item.InvocationId).ToArray());
                var globalHash = await memoryFiles.GetHashAsync(MemoryScope.Global, ct);
                var projectHash = await memoryFiles.GetHashAsync(MemoryScope.Project, ct);
                Assert.IsTrue(started.All(item =>
                    string.Equals(
                        item.Snapshot.GlobalMemoryHash,
                        globalHash,
                        StringComparison.Ordinal)
                    && string.Equals(
                        item.Snapshot.ProjectMemoryHash,
                        projectHash,
                        StringComparison.Ordinal)));

                var meetingSnapshot = await meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                    ?? throw new InvalidDataException("The meeting snapshot was not persisted.");
                var runSnapshot = await sessionRepo.GetRunSnapshotAsync(runId, ct)
                    ?? throw new InvalidDataException("The Run snapshot was not persisted.");
                var messages = await store.ReadAllAsync(sessionId, ct);

                Assert.AreEqual("Completed", meetingSnapshot.Session.Status);
                Assert.AreEqual(2, meetingSnapshot.Session.CurrentRound);
                Assert.AreEqual(1, meetingSnapshot.Session.SelectionVersion);
                Assert.IsNotNull(meetingSnapshot.CurrentRound);
                Assert.AreEqual("Completed", meetingSnapshot.CurrentRound.Status);
                Assert.IsNull(meetingSnapshot.NextScheduledInvocation);
                Assert.HasCount(2, meetingSnapshot.Participants);

                Assert.AreEqual(RunStatus.Completed, runSnapshot.Status);
                Assert.AreEqual("participant-b\nparticipant-a", runSnapshot.TerminalText);

                Assert.HasCount(3, messages);
                Assert.AreEqual(RuntimeProviderRoles.User, messages[0].Role);
                Assert.AreEqual("meeting.user", messages[0].AgentId);
                var assistantMessages = messages.Skip(1).ToArray();
                Assert.IsTrue(assistantMessages.All(static message =>
                    string.Equals(message.Role, "assistant", StringComparison.Ordinal)));
                CollectionAssert.AreEqual(
                    started.Select(static item => item.InvocationId).ToArray(),
                    assistantMessages.Select(static message => message.InvocationId).ToArray());
                Assert.IsTrue(messages.All(static message =>
                    !string.IsNullOrWhiteSpace(message.MessageId) && message.MessageId.Length == 64));
                Assert.AreEqual(messages.Count, messages.Select(static message => message.MessageId).Distinct().Count());
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_FormalParticipants_SortsActiveAndSkipsStandby()
    {
        var participants = new[]
        {
            CreateParticipant("p-c", joinOrder: 2),
            CreateParticipant("p-a", joinOrder: 0, status: ParticipantStatus.Standby),
            CreateParticipant("p-b", joinOrder: 1),
        };

        var result = await RunMeetingAsync(
            new MeetingModeOptions(participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin)));

        Assert.HasCount(2, result.Provider.InvokedAgentIds);
        Assert.AreEqual("p-b", result.Provider.InvokedAgentIds[0]);
        Assert.AreEqual("p-c", result.Provider.InvokedAgentIds[1]);
        Assert.AreEqual(MessageTypes.RunAccepted, result.Events[0].MessageType);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_MaxRoundsOne_InvokesOnlyFirstSortedActive()
    {
        var participants = new[]
        {
            CreateParticipant("p-b", joinOrder: 1),
            CreateParticipant("p-a", joinOrder: 0),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1));

        var result = await RunMeetingAsync(options);

        Assert.HasCount(1, result.Provider.InvokedAgentIds);
        Assert.AreEqual("p-a", result.Provider.InvokedAgentIds[0]);

        var started = result.Events
            .Where(static e => e.MessageType == MessageTypes.InvocationStarted)
            .ToArray();
        var completed = result.Events
            .Where(static e => e.MessageType == MessageTypes.InvocationCompleted)
            .ToArray();
        Assert.HasCount(1, started);
        Assert.HasCount(1, completed);
    }

    [TestMethod]
    public async Task RunAsync_InvocationTimeout_SkipsParticipantAndCompletesOnce()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            OnRequestCaptured = static async (request, ct) =>
            {
                if (request.AgentId == "p-a")
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                }
            }
        };
        var result = await RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: new MeetingPolicy(
                    InvocationTimeoutSeconds: 1,
                    ParticipantFailure: MeetingParticipantFailurePolicy.Skip)),
            provider);

        Assert.HasCount(1, result.Provider.InvokedAgentIds);
        Assert.AreEqual("p-a", result.Provider.InvokedAgentIds[0]);
        Assert.AreEqual(RunStatus.Completed, result.Run.Status);
        Assert.AreEqual("Completed", result.Snapshot.Session.Status);
        Assert.HasCount(1, result.Events.Where(static item =>
            item.MessageType == MessageTypes.RunCompleted));
        Assert.IsEmpty(result.Events.Where(static item =>
            item.MessageType is MessageTypes.RunFailed or MessageTypes.RunCancelled));
        var failed = Assert.ContainsSingle(result.Events.Where(static item =>
            item.MessageType == MessageTypes.InvocationFailed));
        var payload = failed.Payload.Deserialize(RuntimeJsonContext.Default.InvocationFailedEvent);
        Assert.IsNotNull(payload);
        Assert.AreEqual(RuntimeErrorCodes.ProviderTimeout, payload.Error.Code);
    }

    [TestMethod]
    public async Task RunAsync_MeetingTimeout_StopsAtParticipantSafePoint()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            OnRequestCaptured = static async (request, ct) =>
            {
                if (request.AgentId == "p-a")
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(1_100), ct);
                }
            }
        };
        var result = await RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 2),
                policy: new MeetingPolicy(
                    MeetingTimeoutSeconds: 1,
                    InvocationTimeoutSeconds: 5)),
            provider);

        CollectionAssert.AreEqual(
            ExpectedFirstParticipantId,
            result.Provider.InvokedAgentIds.ToArray());
        Assert.AreEqual(1, result.Snapshot.Session.CurrentRound);
        Assert.AreEqual(RunStatus.Completed, result.Run.Status);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_TerminationCondition_StopsAndRunsFinalSummary()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var result = await RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 2),
                policy: new MeetingPolicy(
                    SummaryMode: MeetingSummaryMode.FinalOnly,
                    TerminationConditions: ["p-a"])));

        CollectionAssert.AreEqual(
            ExpectedTerminatedMeetingAgentIds,
            result.Provider.InvokedAgentIds.ToArray());
        Assert.AreEqual(1, result.Snapshot.Session.CurrentRound);
        Assert.AreEqual(RunStatus.Completed, result.Run.Status);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    [DataRow("invocation-timeout")]
    [DataRow("meeting-timeout")]
    [DataRow("termination-condition")]
    public async Task RunAsync_InvalidTerminationPolicy_RejectsBeforeProviderInvocation(
        string invalidSetting)
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var policy = invalidSetting switch
        {
            "invocation-timeout" => new MeetingPolicy(InvocationTimeoutSeconds: 0),
            "meeting-timeout" => new MeetingPolicy(MeetingTimeoutSeconds: 0),
            "termination-condition" => new MeetingPolicy(TerminationConditions: [" "]),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidSetting))
        };
        var provider = new FakeProviderAdapter();

        await Assert.ThrowsAsync<ArgumentException>(() => RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 1),
                policy: policy),
            provider));

        Assert.IsEmpty(provider.InvokedAgentIds);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(10)]
    [DataRow(20)]
    public async Task RunAsync_ParticipantCountBoundary_AcceptsRequest(int participantCount)
    {
        var participants = Enumerable.Range(0, participantCount)
            .Select(index => CreateParticipant($"p-{index:D2}", index))
            .ToArray();
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1));

        var result = await RunMeetingAsync(options);

        Assert.HasCount(1, result.Provider.InvokedAgentIds);
        Assert.AreEqual("p-00", result.Provider.InvokedAgentIds[0]);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    [DataRow(1)]
    [DataRow(21)]
    public async Task RunAsync_ParticipantCountOutsideHardBounds_RejectsBeforeProviderInvocation(
        int participantCount)
    {
        var participants = Enumerable.Range(0, participantCount)
            .Select(index => CreateParticipant($"p-{index:D2}", index))
            .ToArray();
        var provider = new FakeProviderAdapter();
        var options = new MeetingModeOptions(participants);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await RunMeetingAsync(options, provider));

        StringAssert.Contains(exception.Message, "2 to 20 participants");
        Assert.IsEmpty(provider.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_DuplicateParticipantId_RejectsBeforeProviderInvocation()
    {
        var provider = new FakeProviderAdapter();
        var options = new MeetingModeOptions(
        [
            CreateParticipant("p-duplicate", joinOrder: 0),
            CreateParticipant("p-duplicate", joinOrder: 1)
        ]);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await RunMeetingAsync(options, provider));

        StringAssert.Contains(exception.Message, "p-duplicate");
        StringAssert.Contains(exception.Message, "duplicated");
        Assert.IsEmpty(provider.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_DuplicateJoinOrder_RejectsBeforeProviderInvocation()
    {
        var provider = new FakeProviderAdapter();
        var options = new MeetingModeOptions(
        [
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 0)
        ]);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await RunMeetingAsync(options, provider));

        StringAssert.Contains(exception.Message, "JoinOrder '0'");
        StringAssert.Contains(exception.Message, "duplicated");
        Assert.IsEmpty(provider.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_NoActiveParticipant_RejectsBeforeProviderInvocation()
    {
        var provider = new FakeProviderAdapter();
        var options = new MeetingModeOptions(
        [
            CreateParticipant("p-a", joinOrder: 0, status: ParticipantStatus.Standby),
            CreateParticipant("p-b", joinOrder: 1, status: ParticipantStatus.Standby)
        ]);

        var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
            async () => await RunMeetingAsync(options, provider));

        StringAssert.Contains(exception.Message, "at least one Active participant");
        Assert.IsEmpty(provider.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_SecondRound_ProjectsCanonicalHistoryBeforeCurrentInput()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 2));

        var result = await RunMeetingAsync(options);

        Assert.HasCount(2, result.Provider.CapturedRequests);
        CollectionAssert.AreEqual(
            new[] { RuntimeProviderRoles.System, RuntimeProviderRoles.User },
            result.Provider.CapturedRequests[0].Messages.Select(static message => message.Role).ToArray());

        var secondRequest = result.Provider.CapturedRequests[1];
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeProviderRoles.System,
                RuntimeProviderRoles.User,
                RuntimeProviderRoles.Assistant
            },
            secondRequest.Messages.Select(static message => message.Role).ToArray());
        Assert.AreEqual(
            "Discuss the release plan.",
            secondRequest.Messages[1].Content.OfType<TextContentBlock>().Single().Text);
        Assert.AreEqual(
            "p-a",
            secondRequest.Messages[2].Content.OfType<TextContentBlock>().Single().Text);

        Assert.HasCount(2, result.Provider.CapturedEstimateRequests);
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeProviderRoles.System,
                RuntimeProviderRoles.User,
                RuntimeProviderRoles.Assistant
            },
            result.Provider.CapturedEstimateRequests[1].Messages
                .Select(static message => message.Role)
                .ToArray());
    }

    [TestMethod]
    public async Task RunAsync_TailWindowTrimsWholeRounds_EmitsProjectionAdjusted()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 3),
            contextPolicy: new MeetingContextPolicy(
                Strategy: MeetingContextStrategy.TailWindow,
                TailMessageCount: 1));

        var result = await RunMeetingAsync(options);

        Assert.HasCount(3, result.Provider.CapturedRequests);
        var thirdRequest = result.Provider.CapturedRequests[2];
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeProviderRoles.System,
                RuntimeProviderRoles.Assistant
            },
            thirdRequest.Messages.Select(static message => message.Role).ToArray());
        Assert.AreEqual(
            "p-b",
            thirdRequest.Messages[1].Content.OfType<TextContentBlock>().Single().Text);

        var adjustedEvents = result.Events
            .Where(static envelope =>
                envelope.MessageType == MessageTypes.ContextProjectionAdjusted)
            .Select(static envelope => envelope.Payload.Deserialize(
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent)
                ?? throw new InvalidDataException("Projection event payload was empty."))
            .ToArray();
        Assert.HasCount(2, adjustedEvents);
        var thirdRoundAdjustment = adjustedEvents.Single(
            item => string.Equals(item.InvocationId, thirdRequest.InvocationId, StringComparison.Ordinal));
        Assert.AreEqual(2, thirdRoundAdjustment.DroppedMessageCount);
        Assert.AreEqual(1, thirdRoundAdjustment.IncludedMessageCount);
        Assert.AreEqual("TailWindow", thirdRoundAdjustment.Strategy);
    }

    [TestMethod]
    public async Task RunAsync_PerRoundSummary_AppendsMetadataAndReusesLatestCompressedSummary()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 3),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.PerRound),
            contextPolicy: new MeetingContextPolicy(
                Strategy: MeetingContextStrategy.SummaryCompressed));

        var result = await RunMeetingAsync(options);

        CollectionAssert.AreEqual(
            ExpectedPerRoundSummaryAgentIds,
            result.Provider.InvokedAgentIds.ToArray());
        Assert.HasCount(7, result.History);
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3, 4, 5, 6, 7 },
            result.History.Select(static record => record.Sequence).ToArray());

        var summaries = result.History
            .Where(static record => record.SummaryMetadata is not null)
            .ToArray();
        Assert.HasCount(3, summaries);
        CollectionAssert.AreEqual(
            new long[] { 2, 4, 6 },
            summaries.Select(static record =>
                record.SummaryMetadata!.Value.GetProperty("summarizesThroughSeq").GetInt64())
                .ToArray());

        var participantRequests = result.Provider.CapturedRequests
            .Where(static providerRequest => providerRequest.AgentId is "p-a" or "p-b")
            .ToArray();
        Assert.HasCount(3, participantRequests);
        var thirdParticipantRequest = participantRequests[2];
        CollectionAssert.AreEqual(
            new[]
            {
                RuntimeProviderRoles.System,
                RuntimeProviderRoles.Assistant
            },
            thirdParticipantRequest.Messages.Select(static message => message.Role).ToArray());
        Assert.AreEqual(
            "meeting.summarizer",
            thirdParticipantRequest.Messages[1].Content.OfType<TextContentBlock>().Single().Text);

        Assert.IsNotNull(result.Snapshot.LatestSummary);
        Assert.AreEqual(3, result.Snapshot.LatestSummary.RoundIndex);
        Assert.AreEqual(6, result.Snapshot.LatestSummary.SummarizesThroughSeq);
        Assert.AreEqual(summaries[^1].MessageId, result.Snapshot.LatestSummary.SummaryMessageId);
    }

    [TestMethod]
    public async Task RunAsync_FinalOnlySummary_GeneratesOneSummaryBeforeCompletion()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 2),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.FinalOnly));

        var result = await RunMeetingAsync(options);

        CollectionAssert.AreEqual(
            ExpectedFinalOnlySummaryAgentIds,
            result.Provider.InvokedAgentIds.ToArray());
        Assert.HasCount(4, result.History);
        Assert.IsNull(result.History[0].SummaryMetadata);
        Assert.IsNull(result.History[1].SummaryMetadata);
        Assert.IsNull(result.History[2].SummaryMetadata);
        Assert.IsNotNull(result.History[3].SummaryMetadata);
        Assert.AreEqual(
            3,
            result.History[3].SummaryMetadata!.Value
                .GetProperty("summarizesThroughSeq")
                .GetInt64());
        Assert.IsNotNull(result.Snapshot.LatestSummary);
        Assert.AreEqual(2, result.Snapshot.LatestSummary.RoundIndex);
        Assert.AreEqual(result.History[3].MessageId, result.Snapshot.LatestSummary.SummaryMessageId);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_HostConcludesEarly_FinalOnlySummaryRunsBeforeCompletion()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            HostDecisionJson = """{"conclude":true,"participantId":null}"""
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.HostDriven,
                MaxRounds: 3),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.FinalOnly));

        var result = await RunMeetingAsync(options, provider);

        CollectionAssert.AreEqual(
            ExpectedHostConcludedSummaryAgentIds,
            result.Provider.InvokedAgentIds.ToArray());
        Assert.HasCount(2, result.History);
        Assert.AreEqual(RuntimeProviderRoles.User, result.History[0].Role);
        Assert.IsNotNull(result.History[1].SummaryMetadata);
        Assert.AreEqual(
            1L,
            result.History[1].SummaryMetadata!.Value
                .GetProperty("summarizesThroughSeq")
                .GetInt64());
        Assert.IsNotNull(result.Snapshot.LatestSummary);
        Assert.AreEqual(1, result.Snapshot.LatestSummary.RoundIndex);
        Assert.AreEqual("Completed", result.Snapshot.Session.Status);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_MatchingPersistedSummary_ReusesCacheWithoutProviderInvocation()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.FinalOnly));
        var policyJson = JsonSerializer.Serialize(
            options,
            RuntimeJsonContext.Default.MeetingModeOptions);
        var policyHash = ComputeTestHash(policyJson);
        var cacheSeeded = false;

        var result = await RunMeetingAsync(
            options,
            configure: (setup, _) =>
            {
                setup.Provider.OnEstimateCaptured = async (request, ct) =>
                {
                    if (cacheSeeded || request.AgentId != "meeting.summarizer")
                    {
                        return;
                    }

                    cacheSeeded = true;
                    var history = await setup.Store.ReadAllAsync(setup.SessionId, ct);
                    var summarizesThroughSeq = history[^1].Sequence;
                    var summaryMessageId = CreateTestStableId(
                        "meeting-summary-message-v1",
                        setup.SessionId,
                        "1",
                        summarizesThroughSeq.ToString(CultureInfo.InvariantCulture),
                        policyHash);
                    ContentBlock[] content = [new TextContentBlock("cached summary")];
                    var record = new ConversationRecordV1(
                        summaryMessageId,
                        summarizesThroughSeq + 1,
                        request.InvocationId,
                        request.AgentId,
                        RuntimeProviderRoles.Assistant,
                        JsonSerializer.SerializeToElement(
                            content,
                            RuntimeJsonContext.Default.ContentBlockArray),
                        DateTimeOffset.UtcNow,
                        SummaryMetadata: CreateTestSummaryMetadata(
                            roundIndex: 1,
                            summarizesThroughSeq,
                            policyHash,
                            request.AgentId,
                            request.InvocationId));
                    await setup.Store.AppendMessageAsync(
                        setup.SessionId,
                        RuntimeMode.Meeting.ToString(),
                        record,
                        ct);
                    await setup.MeetingRepository.SaveRoundSummaryAsync(
                        setup.SessionId,
                        roundIndex: 1,
                        summarizesThroughSeq,
                        policyHash,
                        summaryMessageId,
                        request.AgentId,
                        request.InvocationId,
                        ct);
                };
                return Task.CompletedTask;
            });

        Assert.IsTrue(cacheSeeded);
        Assert.HasCount(1, result.Provider.InvokedAgentIds);
        Assert.AreEqual("p-a", result.Provider.InvokedAgentIds[0]);
        Assert.HasCount(3, result.History);
        Assert.AreEqual("cached summary", result.History[2].Content
            .Deserialize(RuntimeJsonContext.Default.ContentBlockArray)!
            .OfType<TextContentBlock>()
            .Single()
            .Text);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_SummaryFailureFallbackTailWindow_ContinuesAndEmitsAdjustment()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            FailingAgentId = "meeting.summarizer",
            FailuresRemaining = 2
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 2),
            policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.PerRound),
            contextPolicy: new MeetingContextPolicy(
                Strategy: MeetingContextStrategy.SummaryCompressed,
                TailMessageCount: 1));

        var result = await RunMeetingAsync(options, provider);

        CollectionAssert.AreEqual(
            ExpectedTailWindowFallbackAgentIds,
            result.Provider.InvokedAgentIds.ToArray());
        var failureAdjustments = result.Events
            .Where(static envelope => envelope.MessageType == MessageTypes.ContextProjectionAdjusted)
            .Select(static envelope => envelope.Payload.Deserialize(
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent)
                ?? throw new InvalidDataException("Projection event payload was empty."))
            .Where(static payload => payload.EstimateSource == "runtime.summary-failed")
            .ToArray();
        Assert.HasCount(1, failureAdjustments);
        Assert.AreEqual("TailWindow", failureAdjustments[0].Strategy);
        Assert.HasCount(4, result.History);
        Assert.IsNotNull(result.History[^1].SummaryMetadata);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_SummaryFailureFallbackFull_CompletesWithoutSummary()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            FailingAgentId = "meeting.summarizer",
            FailuresRemaining = 1
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1),
            policy: new MeetingPolicy(
                SummaryMode: MeetingSummaryMode.FinalOnly,
                MaxRetriesPerInvocation: 0,
                SummarizerFailure: MeetingSummarizerFailurePolicy.FallbackFull));

        var result = await RunMeetingAsync(options, provider);

        var failureAdjustment = result.Events
            .Where(static envelope => envelope.MessageType == MessageTypes.ContextProjectionAdjusted)
            .Select(static envelope => envelope.Payload.Deserialize(
                RuntimeJsonContext.Default.ContextProjectionAdjustedEvent)
                ?? throw new InvalidDataException("Projection event payload was empty."))
            .Single(static payload => payload.EstimateSource == "runtime.summary-failed");
        Assert.AreEqual("Full", failureAdjustment.Strategy);
        Assert.HasCount(2, result.History);
        Assert.IsTrue(result.History.All(static record => record.SummaryMetadata is null));
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_SummaryFailureFail_StopsAfterConfiguredRetries()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter
        {
            FailingAgentId = "meeting.summarizer",
            FailuresRemaining = int.MaxValue
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1),
            policy: new MeetingPolicy(
                SummaryMode: MeetingSummaryMode.FinalOnly,
                MaxRetriesPerInvocation: 2,
                SummarizerFailure: MeetingSummarizerFailurePolicy.Fail));

        InvalidOperationException? caught = null;
        try
        {
            await RunMeetingAsync(options, provider);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.IsNotNull(caught);
        StringAssert.Contains(caught.Message, "Summarizer invocation failed");
        Assert.AreEqual(
            3,
            provider.InvokedAgentIds.Count(static agentId => agentId == "meeting.summarizer"));
    }

    [TestMethod]
    public async Task RunAsync_HostFailurePause_PersistsPausedMeetingWithoutTerminalRunEvent()
    {
        var provider = new FakeProviderAdapter
        {
            FailingAgentId = "meeting.host",
            FailuresRemaining = 1
        };
        var options = new MeetingModeOptions(
            [
                CreateParticipant("p-a", joinOrder: 0),
                CreateParticipant("p-b", joinOrder: 1)
            ],
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.HostDriven,
                MaxRounds: 1),
            policy: new MeetingPolicy(
                MaxRetriesPerInvocation: 0,
                HostFailure: MeetingHostFailurePolicy.Pause));

        var result = await RunMeetingAsync(options, provider);

        Assert.AreEqual(MessageTypes.InvocationFailed, result.Events[^1].MessageType);
        Assert.IsFalse(result.Events.Any(static envelope =>
            envelope.MessageType is MessageTypes.RunFailed or MessageTypes.RunCompleted));
        Assert.AreEqual("Paused", result.Snapshot.Session.Status);
        Assert.IsNotNull(result.Snapshot.NextScheduledInvocation);
        Assert.AreEqual("Interrupted", result.Snapshot.NextScheduledInvocation.Status);
        Assert.AreEqual("host", result.Snapshot.NextScheduledInvocation.Role);
        Assert.AreEqual(RunStatus.WaitingForApproval, result.Run.Status);
    }

    [TestMethod]
    public async Task RunAsync_ParticipantFailurePause_PersistsPausedMeetingWithoutTerminalRunEvent()
    {
        var provider = new FakeProviderAdapter
        {
            FailingAgentId = "p-a",
            FailuresRemaining = 1
        };
        var options = new MeetingModeOptions(
            [
                CreateParticipant("p-a", joinOrder: 0),
                CreateParticipant("p-b", joinOrder: 1)
            ],
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.RoundRobin,
                MaxRounds: 1),
            policy: new MeetingPolicy(
                MaxRetriesPerInvocation: 0,
                ParticipantFailure: MeetingParticipantFailurePolicy.Pause));

        var result = await RunMeetingAsync(options, provider);

        Assert.AreEqual(MessageTypes.InvocationFailed, result.Events[^1].MessageType);
        Assert.IsFalse(result.Events.Any(static envelope =>
            envelope.MessageType is MessageTypes.RunFailed or MessageTypes.RunCompleted));
        Assert.AreEqual("Paused", result.Snapshot.Session.Status);
        Assert.IsNotNull(result.Snapshot.NextScheduledInvocation);
        Assert.AreEqual("Interrupted", result.Snapshot.NextScheduledInvocation.Status);
        Assert.AreEqual("participant", result.Snapshot.NextScheduledInvocation.Role);
        Assert.AreEqual(RunStatus.WaitingForApproval, result.Run.Status);
    }

    [TestMethod]
    public async Task RunAsync_EmitFalseStartSeq1_SkipsDuplicateAcceptedAndStartsAtSequence1()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var store = new ConversationStore(dataDirectory);
            var sessionRepo = new SqliteSessionRepository(connection);
            var meetingRepo = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);

            var preAcceptedPayload = JsonSerializer.Serialize(
                new RunAcceptedEvent(sessionId, runId),
                RuntimeJsonContext.Default.RunAcceptedEvent);
            var preGsn = await outbox.AppendAsync(
                runId, 0, MessageTypes.RunAccepted, preAcceptedPayload, ct);

            var provider = new FakeProviderAdapter();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                meetingRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                emitAccepted: false,
                startRunSequence: 1);

            var participants = new[]
            {
                new AgentRef("participant-b", "v1", "You are participant B.", "fake", "model-b"),
                new AgentRef("participant-a", "v1", "You are participant A.", "fake", "model-a")
            };
            var request = new NewSessionRunRequest(
                "session-key",
                "run-key",
                RuntimeMode.Meeting,
                new NextTurnSelection(
                    1,
                    RuntimeMode.Meeting,
                    new DefaultSelection("fake", "test-model"),
                    new MeetingModeOptions(participants),
                    ToolCatalogVersion: "tools-v1"),
                [new TextContentBlock("Discuss the release plan.")]);
            var events = new List<RuntimeEventEnvelope>();

            await foreach (var envelope in orchestrator.RunAsync(
                runId, sessionId, request, "runtime-1", ct))
            {
                events.Add(envelope);
            }

            var acceptedEvents = events
                .Where(static e => e.MessageType == MessageTypes.RunAccepted)
                .ToArray();
            Assert.IsEmpty(acceptedEvents);

            Assert.AreEqual(MessageTypes.InvocationStarted, events[0].MessageType);
            Assert.AreEqual(1L, events[0].RunSequence);

            Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);

            var runSequences = events.Select(static e => e.RunSequence).ToArray();
            for (var i = 0; i < runSequences.Length; i++)
            {
                Assert.AreEqual(1L + i, runSequences[i]);
            }

            Assert.IsTrue(events.All(e => e.Gsn > preGsn));
            for (var i = 1; i < events.Count; i++)
            {
                Assert.AreEqual(events[0].Gsn + i, events[i].Gsn);
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_SelectorDriven_InvokesSelectorAndParticipantPerRound()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter { SelectorParticipantId = "p-b" };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.SelectorDriven,
                MaxRounds: 2));

        var result = await RunMeetingAsync(options, provider);

        var expectedAgentIds = new[] { "meeting.selector", "p-b", "meeting.selector", "p-b" };
        CollectionAssert.AreEqual(expectedAgentIds, result.Provider.InvokedAgentIds.ToArray());

        var started = result.Events
            .Where(static e => e.MessageType == MessageTypes.InvocationStarted)
            .ToArray();
        Assert.HasCount(4, started);

        var selectorRequests = result.Provider.CapturedRequests
            .Where(static r => r.AgentId == "meeting.selector")
            .ToArray();
        foreach (var selectorRequest in selectorRequests)
        {
            Assert.IsNotNull(selectorRequest.StructuredOutput);
            Assert.IsTrue(
                !string.IsNullOrEmpty(selectorRequest.StructuredOutput!.Name)
                || selectorRequest.StructuredOutput!.Schema.ValueKind == JsonValueKind.Object);
            Assert.IsNull(selectorRequest.Tools);
        }

        var participantRequests = result.Provider.CapturedRequests
            .Where(static r => r.AgentId != "meeting.selector")
            .ToArray();
        foreach (var participantRequest in participantRequests)
        {
            Assert.IsNull(participantRequest.StructuredOutput);
        }

        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_PersistsInvocationSnapshotsForAllMeetingRoles()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var selectorResult = await RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.SelectorDriven,
                    MaxRounds: 1)),
            new FakeProviderAdapter { SelectorParticipantId = "p-b" });
        var hostResult = await RunMeetingAsync(
            new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.HostDriven,
                    MaxRounds: 1),
                policy: new MeetingPolicy(SummaryMode: MeetingSummaryMode.FinalOnly)),
            new FakeProviderAdapter
            {
                HostDecisionJson = """{"conclude":true,"participantId":null}"""
            });
        var results = new[] { selectorResult, hostResult };
        var started = results
            .SelectMany(static result => result.Events)
            .Where(static envelope => envelope.MessageType == MessageTypes.InvocationStarted)
            .Select(static envelope => envelope.Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent)
                ?? throw new InvalidDataException("The invocation.started payload was empty."))
            .ToArray();
        var persisted = results
            .SelectMany(static result => result.InvocationSnapshots)
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);

        Assert.HasCount(started.Length, persisted);
        foreach (var invocation in started)
        {
            Assert.IsTrue(persisted.TryGetValue(invocation.InvocationId, out var snapshot));
            Assert.AreEqual(invocation.Snapshot, snapshot);
        }

        CollectionAssert.AreEquivalent(
            ExpectedPersistedRoleAgentIds,
            persisted.Values
                .Select(static snapshot => snapshot.AgentId)
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    [TestMethod]
    public async Task RunAsync_SelectorDriven_InvalidOutput_UsesDeterministicFallback()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var provider = new FakeProviderAdapter();
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(
                Type: MeetingSelectorPolicy.SelectorDriven,
                MaxRounds: 2));

        var result = await RunMeetingAsync(options, provider);

        var expectedAgentIds = new[] { "meeting.selector", "p-a", "meeting.selector", "p-b" };
        CollectionAssert.AreEqual(expectedAgentIds, result.Provider.InvokedAgentIds.ToArray());
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_HitlApprove_InvokesProviderAfterOneStableRequest()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin, MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true));

        var callbackRequests = new List<MeetingHitlRequest>();
        MeetingHitlResponse? capturedResponse = null;

        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>> handler = (req, ct) =>
        {
            callbackRequests.Add(req);
            capturedResponse = new MeetingHitlResponse(req.ApprovalRequestId, MeetingHitlAction.Approve);
            return Task.FromResult(capturedResponse);
        };

        var result = await RunMeetingAsync(options, hitlHandler: handler);

        Assert.HasCount(1, callbackRequests);
        Assert.IsFalse(string.IsNullOrEmpty(callbackRequests[0].ApprovalRequestId));
        Assert.IsNotNull(capturedResponse);
        Assert.AreEqual(callbackRequests[0].ApprovalRequestId, capturedResponse!.ApprovalRequestId);
        Assert.IsNotEmpty(result.Provider.InvokedAgentIds);
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_HitlSupplement_AppendsSupplementToInitialInput()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin, MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true));

        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>> handler = (req, ct) =>
        {
            return Task.FromResult(new MeetingHitlResponse(
                req.ApprovalRequestId,
                MeetingHitlAction.Supplement,
                Supplement: "Please also discuss the budget."));
        };

        var result = await RunMeetingAsync(options, hitlHandler: handler);

        Assert.IsNotEmpty(result.Provider.CapturedRequests);
        var lastRequest = result.Provider.CapturedRequests[^1];
        var allText = lastRequest.Messages
            .SelectMany(m => m.Content)
            .OfType<TextContentBlock>()
            .Select(t => t.Text)
            .ToArray();
        Assert.AreEqual(
            1,
            allText.Count(static text => text == "Discuss the release plan."));
        Assert.AreEqual(
            1,
            allText.Count(static text => text == "Please also discuss the budget."));
        Assert.AreEqual(MessageTypes.RunCompleted, result.Events[^1].MessageType);
    }

    [TestMethod]
    public async Task RunAsync_HitlReject_DoesNotInvokeProviderAndEmitsOneCancelled()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin, MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true));

        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>> handler = (req, ct) =>
        {
            return Task.FromResult(new MeetingHitlResponse(
                req.ApprovalRequestId,
                MeetingHitlAction.Reject,
                Reason: "Not approved."));
        };

        var result = await RunMeetingAsync(options, hitlHandler: handler);

        Assert.HasCount(0, result.Provider.InvokedAgentIds);
        var cancelledEvents = result.Events
            .Where(static e => e.MessageType == MessageTypes.RunCancelled)
            .ToArray();
        Assert.HasCount(1, cancelledEvents);
        var completedEvents = result.Events
            .Where(static e => e.MessageType == MessageTypes.RunCompleted)
            .ToArray();
        Assert.HasCount(0, completedEvents);
    }

    [TestMethod]
    public async Task RunAsync_HitlEnabledWithoutHandler_FailsBeforeProvider()
    {
        var provider = new FakeProviderAdapter();
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin, MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true));

        Exception? caught = null;
        try
        {
            await RunMeetingAsync(options, provider);
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }
        Assert.IsNotNull(caught);

        Assert.HasCount(0, provider.InvokedAgentIds);
    }

    [TestMethod]
    public async Task RunAsync_HitlHandlerIsCalledOnceWithDeterministicRequest()
    {
        var participants = new[]
        {
            CreateParticipant("p-a", joinOrder: 0),
            CreateParticipant("p-b", joinOrder: 1),
        };
        var options = new MeetingModeOptions(
            participants,
            selectorPolicy: new MeetingSelectorPolicyOptions(Type: MeetingSelectorPolicy.RoundRobin, MaxRounds: 1),
            policy: new MeetingPolicy(HitlEnabled: true));

        var callbackRequests = new List<MeetingHitlRequest>();
        MeetingHitlResponse? capturedResponse = null;

        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>> handler = (req, ct) =>
        {
            callbackRequests.Add(req);
            capturedResponse = new MeetingHitlResponse(req.ApprovalRequestId, MeetingHitlAction.Approve);
            return Task.FromResult(capturedResponse);
        };

        var result = await RunMeetingAsync(options, hitlHandler: handler);

        Assert.HasCount(1, callbackRequests);
        var hitlReq = callbackRequests[0];
        Assert.AreEqual(1, hitlReq.RoundIndex);
        Assert.IsFalse(string.IsNullOrWhiteSpace(hitlReq.Prompt));
        Assert.IsNotNull(hitlReq.Context);
        Assert.IsNotEmpty(hitlReq.Context);
        Assert.IsFalse(string.IsNullOrEmpty(hitlReq.ApprovalRequestId));
        Assert.IsNotNull(capturedResponse);
        Assert.AreEqual(hitlReq.ApprovalRequestId, capturedResponse!.ApprovalRequestId);
    }

    [TestMethod]
    public async Task RunAsync_RoundRobin_MidRunParticipantAddRemove_UsesNewSnapshotAtSafePoint()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var provider = new FakeProviderAdapter();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var store = new ConversationStore(dataDirectory);
            var sessionRepo = new SqliteSessionRepository(connection);
            var meetingRepo = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);

            var callbackFired = false;
            provider.OnRequestCaptured = async (req, token) =>
            {
                if (callbackFired || req.AgentId != "p-a")
                {
                    return;
                }

                callbackFired = true;
                var now = DateTimeOffset.UtcNow.ToString("O");

                await using var deleteCmd = connection.CreateCommand();
                deleteCmd.CommandText =
                    """
                    UPDATE meeting_participants
                    SET status = 'removed',
                        removed_selection_version = 2,
                        removed_at = $now,
                        updated_at = $now
                    WHERE session_id = $sessionId AND participant_id = 'p-b'
                    """;
                deleteCmd.Parameters.AddWithValue("$now", now);
                deleteCmd.Parameters.AddWithValue("$sessionId", sessionId);
                await deleteCmd.ExecuteNonQueryAsync(token);

                var agentRef = new AgentRef("p-c", "v1", "System prompt.", "fake", "test-model");
                var agentRefJson = JsonSerializer.Serialize(
                    agentRef,
                    RuntimeJsonContext.Default.AgentRef);

                await using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText =
                    """
                    INSERT INTO meeting_participants(
                        session_id, participant_id, agent_id, agent_ref_json,
                        display_name, join_order, status, joined_selection_version,
                        joined_at, updated_at)
                    VALUES(
                        $sessionId, 'p-c', 'p-c', $agentRefJson,
                        'p-c', 1, 'active', 2,
                        $now, $now)
                    """;
                insertCmd.Parameters.AddWithValue("$sessionId", sessionId);
                insertCmd.Parameters.AddWithValue("$agentRefJson", agentRefJson);
                insertCmd.Parameters.AddWithValue("$now", now);
                await insertCmd.ExecuteNonQueryAsync(token);

                await using var versionCmd = connection.CreateCommand();
                versionCmd.CommandText =
                    """
                    UPDATE meeting_sessions
                    SET selection_version = 2,
                        updated_at = $now
                    WHERE session_id = $sessionId
                    """;
                versionCmd.Parameters.AddWithValue("$now", now);
                versionCmd.Parameters.AddWithValue("$sessionId", sessionId);
                await versionCmd.ExecuteNonQueryAsync(token);
            };

            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                meetingRepo,
                outbox,
                new AgentContextComposer(memoryFiles));
            var participants = new[]
            {
                CreateParticipant("p-a", joinOrder: 0),
                CreateParticipant("p-b", joinOrder: 1),
            };
            var request = CreateRequest(new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 2)));
            var events = new List<RuntimeEventEnvelope>();

            await foreach (var envelope in orchestrator.RunAsync(
                runId, sessionId, request, "runtime-1", ct))
            {
                events.Add(envelope);
            }

            Assert.IsTrue(callbackFired, "The provider callback should have fired for p-a.");
            CollectionAssert.AreEqual(
                ExpectedDynamicParticipantIds,
                provider.InvokedAgentIds.ToArray());

            await using var queryCmd = connection.CreateCommand();
            queryCmd.CommandText =
                """
                SELECT selection_version
                FROM meeting_invocations
                WHERE session_id = $sessionId
                  AND participant_id = 'p-c'
                """;
            queryCmd.Parameters.AddWithValue("$sessionId", sessionId);
            var versionResult = await queryCmd.ExecuteScalarAsync(ct);
            Assert.IsNotNull(versionResult);
            Assert.AreEqual(2, Convert.ToInt32(versionResult, CultureInfo.InvariantCulture));

            var meetingSnapshot = await meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                ?? throw new InvalidDataException("The meeting snapshot was not persisted.");
            Assert.AreEqual("Completed", meetingSnapshot.Session.Status);
            Assert.IsNull(meetingSnapshot.NextScheduledInvocation);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_RoundRobin_UnknownDurableStatus_ThrowsAtSafePointAndDoesNotInvokeCorruptParticipant()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var provider = new FakeProviderAdapter();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var store = new ConversationStore(dataDirectory);
            var sessionRepo = new SqliteSessionRepository(connection);
            var meetingRepo = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);

            var callbackFired = false;
            provider.OnRequestCaptured = async (req, token) =>
            {
                if (callbackFired || req.AgentId != "p-a")
                {
                    return;
                }

                callbackFired = true;
                var now = DateTimeOffset.UtcNow.ToString("O");

                await using var updateCmd = connection.CreateCommand();
                updateCmd.CommandText =
                    """
                    UPDATE meeting_participants
                    SET status = 'corrupted',
                        updated_at = $now
                    WHERE session_id = $sessionId AND participant_id = 'p-b'
                    """;
                updateCmd.Parameters.AddWithValue("$now", now);
                updateCmd.Parameters.AddWithValue("$sessionId", sessionId);
                await updateCmd.ExecuteNonQueryAsync(token);
            };

            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                meetingRepo,
                outbox,
                new AgentContextComposer(memoryFiles));
            var participants = new[]
            {
                CreateParticipant("p-a", joinOrder: 0),
                CreateParticipant("p-b", joinOrder: 1),
            };
            var request = CreateRequest(new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 2)));

            InvalidOperationException? caught = null;
            try
            {
                await foreach (var envelope in orchestrator.RunAsync(
                    runId, sessionId, request, "runtime-1", ct))
                {
                    _ = envelope;
                }
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }

            Assert.IsTrue(callbackFired, "The provider callback should have fired for p-a.");
            Assert.IsNotNull(caught, "Expected InvalidOperationException for unknown status.");
            StringAssert.Contains(caught!.Message, "p-b");
            StringAssert.Contains(caught!.Message, "corrupted");
            CollectionAssert.DoesNotContain(
                provider.InvokedAgentIds.ToArray(),
                "p-b",
                "p-b must not be invoked after its status was corrupted.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_RoundRobin_IncompleteDynamicAgentRefWithoutFallback_ThrowsAndDoesNotInvoke()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var provider = new FakeProviderAdapter();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var store = new ConversationStore(dataDirectory);
            var sessionRepo = new SqliteSessionRepository(connection);
            var meetingRepo = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);

            var callbackFired = false;
            provider.OnRequestCaptured = async (req, token) =>
            {
                if (callbackFired || req.AgentId != "p-a")
                {
                    return;
                }

                callbackFired = true;
                var now = DateTimeOffset.UtcNow.ToString("O");

                var incompleteRef = new AgentRef("p-corrupt", "", "", "fake", "test-model");
                var agentRefJson = JsonSerializer.Serialize(
                    incompleteRef,
                    RuntimeJsonContext.Default.AgentRef);

                await using var insertCmd = connection.CreateCommand();
                insertCmd.CommandText =
                    """
                    INSERT INTO meeting_participants(
                        session_id, participant_id, agent_id, agent_ref_json,
                        display_name, join_order, status, joined_selection_version,
                        joined_at, updated_at)
                    VALUES(
                        $sessionId, 'p-corrupt', 'p-corrupt', $agentRefJson,
                        'p-corrupt', 2, 'active', 2,
                        $now, $now)
                    """;
                insertCmd.Parameters.AddWithValue("$sessionId", sessionId);
                insertCmd.Parameters.AddWithValue("$agentRefJson", agentRefJson);
                insertCmd.Parameters.AddWithValue("$now", now);
                await insertCmd.ExecuteNonQueryAsync(token);

                await using var versionCmd = connection.CreateCommand();
                versionCmd.CommandText =
                    """
                    UPDATE meeting_sessions
                    SET selection_version = 2,
                        updated_at = $now
                    WHERE session_id = $sessionId
                    """;
                versionCmd.Parameters.AddWithValue("$now", now);
                versionCmd.Parameters.AddWithValue("$sessionId", sessionId);
                await versionCmd.ExecuteNonQueryAsync(token);
            };

            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                meetingRepo,
                outbox,
                new AgentContextComposer(memoryFiles));
            var participants = new[]
            {
                CreateParticipant("p-a", joinOrder: 0),
                CreateParticipant("p-b", joinOrder: 1),
            };
            var request = CreateRequest(new MeetingModeOptions(
                participants,
                selectorPolicy: new MeetingSelectorPolicyOptions(
                    Type: MeetingSelectorPolicy.RoundRobin,
                    MaxRounds: 2)));

            InvalidOperationException? caught = null;
            try
            {
                await foreach (var envelope in orchestrator.RunAsync(
                    runId, sessionId, request, "runtime-1", ct))
                {
                    _ = envelope;
                }
            }
            catch (InvalidOperationException ex)
            {
                caught = ex;
            }

            Assert.IsTrue(callbackFired, "The provider callback should have fired for p-a.");
            Assert.IsNotNull(caught, "Expected InvalidOperationException for incomplete AgentRef.");
            StringAssert.Contains(caught!.Message, "p-corrupt");
            Assert.DoesNotContain(
                "p-corrupt",
                provider.InvokedAgentIds,
                "The corrupt participant must not be invoked.");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task<(string SessionId, string RunId)> CreateRunningRunAsync(
        SqliteSessionRepository sessionRepo,
        CancellationToken ct)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sessionId = await sessionRepo.CreateSessionAsync(
            RuntimeMode.Meeting,
            $"meeting-session-{suffix}",
            TimeSpan.FromHours(1),
            ct);
        var runId = await sessionRepo.CreateRunAsync(
            sessionId,
            $"meeting-run-{suffix}",
            TimeSpan.FromHours(1),
            ct);
        await sessionRepo.TransitionRunStatusAsync(
            runId, RunStatus.Accepted, RunStatus.Preparing, ct);
        await sessionRepo.TransitionRunStatusAsync(
            runId, RunStatus.Preparing, RunStatus.Running, ct);
        return (sessionId, runId);
    }

    private static MeetingParticipant CreateParticipant(
        string participantId,
        int joinOrder,
        ParticipantStatus status = ParticipantStatus.Active) =>
        new(
            participantId,
            new AgentRef(participantId, "v1", "System prompt.", "fake", "test-model"),
            participantId,
            joinOrder,
            status);

    private static NewSessionRunRequest CreateRequest(MeetingModeOptions options) =>
        new(
            "session-key",
            "run-key",
            RuntimeMode.Meeting,
            new NextTurnSelection(
                1,
                RuntimeMode.Meeting,
                new DefaultSelection("fake", "test-model"),
                options,
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock("Discuss the release plan.")]);

    private static string ComputeTestHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static string CreateTestStableId(params string[] components) =>
        ComputeTestHash(string.Join("\n", components));

    private static JsonElement CreateTestSummaryMetadata(
        int roundIndex,
        long summarizesThroughSeq,
        string policyHash,
        string summarizerAgentId,
        string summarizerInvocationId)
    {
        var metadataJson = $$"""
            {
              "roundIndex": {{roundIndex}},
              "summarizesThroughSeq": {{summarizesThroughSeq}},
              "policyHash": "{{policyHash}}",
              "summarizerAgentId": "{{summarizerAgentId}}",
              "summarizerInvocationId": "{{summarizerInvocationId}}"
            }
            """;
        using var document = JsonDocument.Parse(metadataJson);
        return document.RootElement.Clone();
    }

    private async Task<MeetingRunResult> RunMeetingAsync(
        MeetingModeOptions options,
        FakeProviderAdapter? provider = null,
        Func<MeetingHitlRequest, CancellationToken, Task<MeetingHitlResponse>>? hitlHandler = null,
        Func<MeetingRunSetup, CancellationToken, Task>? configure = null)
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"meeting-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);

        try
        {
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Combine(dataDirectory, "state.db"),
                Pooling = false
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await SqliteSchema.EnsureCreatedAsync(connection, ct);
            using var outbox = new SqliteEventOutbox(connection);
            var store = new ConversationStore(dataDirectory);
            var sessionRepo = new SqliteSessionRepository(connection);
            var meetingRepo = new SqliteMeetingRepository(connection);
            var (sessionId, runId) = await CreateRunningRunAsync(sessionRepo, ct);
            provider ??= new FakeProviderAdapter();
            if (configure is not null)
            {
                await configure(
                    new MeetingRunSetup(sessionId, store, meetingRepo, provider),
                    ct);
            }
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var orchestrator = new MeetingModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                meetingRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                hitlHandler: hitlHandler);
            var request = CreateRequest(options);
            var events = new List<RuntimeEventEnvelope>();

            await foreach (var envelope in orchestrator.RunAsync(
                runId, sessionId, request, "runtime-1", ct))
            {
                events.Add(envelope);
            }

            var history = await store.ReadAllAsync(sessionId, ct);
            var snapshot = await meetingRepo.GetMeetingSnapshotAsync(sessionId, ct)
                ?? throw new InvalidDataException("Meeting snapshot was not persisted.");
            var run = await sessionRepo.GetRunSnapshotAsync(runId, ct)
                ?? throw new InvalidDataException("The Run snapshot was not persisted.");
            var invocationSnapshots = new Dictionary<string, InvocationSnapshot>(StringComparer.Ordinal);
            foreach (var envelope in events.Where(static item =>
                item.MessageType == MessageTypes.InvocationStarted))
            {
                var started = envelope.Payload.Deserialize(
                    RuntimeJsonContext.Default.InvocationStartedEvent)
                    ?? throw new InvalidDataException("The invocation.started payload was empty.");
                var snapshotJson = await sessionRepo.GetInvocationSnapshotJsonAsync(
                    started.InvocationId,
                    ct) ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was not persisted.");
                var persistedSnapshot = JsonSerializer.Deserialize(
                    snapshotJson,
                    RuntimeJsonContext.Default.InvocationSnapshot)
                    ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was empty.");
                invocationSnapshots.Add(started.InvocationId, persistedSnapshot);
            }

            return new MeetingRunResult(
                [.. events],
                provider,
                [.. history],
                snapshot,
                run,
                invocationSnapshots);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private sealed record MeetingRunResult(
        RuntimeEventEnvelope[] Events,
        FakeProviderAdapter Provider,
        ConversationRecordV1[] History,
        MeetingSnapshot Snapshot,
        RunSnapshot Run,
        IReadOnlyDictionary<string, InvocationSnapshot> InvocationSnapshots);

    private sealed record MeetingRunSetup(
        string SessionId,
        ConversationStore Store,
        SqliteMeetingRepository MeetingRepository,
        FakeProviderAdapter Provider);

    private sealed class FakeProviderAdapter : IRuntimeProviderAdapter
    {
        private readonly List<string> _invokedAgentIds = [];
        private readonly List<RuntimeProviderRequest> _capturedRequests = [];
        private readonly List<RuntimeProviderRequest> _capturedEstimateRequests = [];

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public List<string> InvokedAgentIds => _invokedAgentIds;

        public List<RuntimeProviderRequest> CapturedRequests => _capturedRequests;

        public List<RuntimeProviderRequest> CapturedEstimateRequests => _capturedEstimateRequests;

        public string? SelectorParticipantId { get; set; }

        public string? HostDecisionJson { get; set; }

        public string? FailingAgentId { get; set; }

        public int FailuresRemaining { get; set; }

        public Func<RuntimeProviderRequest, CancellationToken, Task>? OnRequestCaptured { get; set; }

        public Func<RuntimeProviderRequest, CancellationToken, Task>? OnEstimateCaptured { get; set; }

        public Task ValidateCapabilitiesAsync(ContentBlock[] input, CancellationToken ct = default) =>
            Task.CompletedTask;

        public async ValueTask<ProviderTokenEstimate> EstimateTokensAsync(
            RuntimeProviderRequest request,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _capturedEstimateRequests.Add(request);
            if (OnEstimateCaptured is not null)
            {
                await OnEstimateCaptured(request, ct);
            }

            return ProviderTokenEstimator.Estimate(request);
        }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _invokedAgentIds.Add(request.AgentId);
            _capturedRequests.Add(request);
            if (OnRequestCaptured is not null)
            {
                await OnRequestCaptured(request, ct);
            }
            await Task.Yield();
            if (FailuresRemaining > 0
                && string.Equals(request.AgentId, FailingAgentId, StringComparison.Ordinal))
            {
                FailuresRemaining--;
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        "configured_failure",
                        "provider",
                        $"Configured failure for agent '{request.AgentId}'.",
                        IsRetryable: true,
                        ProviderDetails: null,
                        Guid.NewGuid().ToString("N")));
                yield break;
            }

            if (request.AgentId == "meeting.host" && !string.IsNullOrEmpty(HostDecisionJson))
            {
                yield return new TextDeltaProviderEvent(request.InvocationId, HostDecisionJson);
            }
            else if (request.AgentId == "meeting.selector")
            {
                if (!string.IsNullOrEmpty(SelectorParticipantId))
                {
                    yield return new TextDeltaProviderEvent(
                        request.InvocationId,
                        $$"""{"participantId":"{{SelectorParticipantId}}"}""");
                }
                else
                {
                    yield return new TextDeltaProviderEvent(
                        request.InvocationId,
                        "meeting.selector");
                }
            }
            else
            {
                yield return new TextDeltaProviderEvent(request.InvocationId, request.AgentId);
            }
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }
    }
}
