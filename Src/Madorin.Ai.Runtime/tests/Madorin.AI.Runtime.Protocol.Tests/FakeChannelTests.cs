using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;

namespace Madorin.AI.Runtime.Protocol.Tests;

[TestClass]
public sealed class FakeChannelTests(TestContext testContext)
{
    private static readonly string[] ExpectedClosureMessageTypes =
    [
        MessageTypes.RunAccepted,
        MessageTypes.RunStatusChanged,
        MessageTypes.InvocationStarted,
        MessageTypes.TextDelta,
        MessageTypes.RunStatusChanged,
        MessageTypes.RunStatusChanged,
        MessageTypes.InvocationCompleted,
        MessageTypes.RunStatusChanged,
        MessageTypes.RunCompleted
    ];

    [TestMethod]
    [DataRow(RuntimeMode.Expert)]
    [DataRow(RuntimeMode.Meeting)]
    [DataRow(RuntimeMode.Work)]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task FakeHostAndRuntime_ModeFlow_CompletesAndReleasesAcknowledgedEvents(
        RuntimeMode mode)
    {
        await using var runtime = new FakeRuntime("runtime-1");
        var host = new FakeHost("host-1");
        host.Connect(runtime);

        var initialized = await host.InitializeAsync(
            runtime.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);
        var request = CreateNewSessionRunRequest(mode, "mode-flow");
        var started = await host.StartNewSessionRunAsync(
            runtime.RuntimeInstanceId,
            request,
            duplicateDelivery: true,
            testContext.CancellationToken);

        Assert.IsNull(initialized.Error);
        Assert.IsNotNull(initialized.Value);
        Assert.AreEqual(ProtocolVersions.Current, initialized.Value.SelectedProtocolVersion);
        Assert.IsNull(started.Error);
        Assert.IsNotNull(started.Value);

        var received = await host.ReceiveRunAsync(
            runtime.RuntimeInstanceId,
            started.Value,
            expectedDeliveriesPerEvent: 2,
            testContext.CancellationToken);

        Assert.IsNull(received.Error);
        Assert.AreEqual(ExpectedClosureMessageTypes.Length * 2, received.DeliveryCount);
        Assert.HasCount(ExpectedClosureMessageTypes.Length, received.Events);
        CollectionAssert.AreEqual(
            ExpectedClosureMessageTypes,
            received.Events.Select(runtimeEvent => runtimeEvent.MessageType).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(1, ExpectedClosureMessageTypes.Length)
                .Select(value => (long)value)
                .ToArray(),
            received.Events.Select(runtimeEvent => runtimeEvent.RunSequence).ToArray());

        var toolWaitingEvents = received.Events
            .Where(runtimeEvent =>
                runtimeEvent.MessageType == MessageTypes.RunStatusChanged &&
                runtimeEvent.Payload.GetProperty("status").GetString() ==
                RunStatus.WaitingForTool.ToString())
            .ToArray();
        Assert.HasCount(1, toolWaitingEvents);

        var terminalEvents = received.Events
            .Where(runtimeEvent => runtimeEvent.MessageType is
                MessageTypes.RunCompleted or
                MessageTypes.RunFailed or
                MessageTypes.RunCancelled)
            .ToArray();
        Assert.HasCount(1, terminalEvents);
        Assert.AreEqual(MessageTypes.RunCompleted, terminalEvents[0].MessageType);

        var state = runtime.GetRunState(started.Value.RunId);
        Assert.AreEqual(mode, state.Mode);
        Assert.AreEqual(RunStatus.Completed, state.Status);
        Assert.AreEqual(1, state.TerminalEventCount);
        Assert.AreEqual(ExpectedClosureMessageTypes.Length, runtime.BufferedEventCount);

        var acknowledged = await host.AcknowledgeAsync(
            runtime.RuntimeInstanceId,
            received.LastConfirmedGsn,
            cancellationToken: testContext.CancellationToken);

        Assert.IsNull(acknowledged.Error);
        Assert.IsNotNull(acknowledged.Value);
        Assert.AreEqual(received.LastConfirmedGsn, acknowledged.Value.LastConfirmedGsn);
        Assert.AreEqual(ExpectedClosureMessageTypes.Length, acknowledged.Value.ReleasedEventCount);
        Assert.AreEqual(0, runtime.BufferedEventCount);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task FakeRuntime_RetriesAndConflicts_PreserveIdempotentState()
    {
        await using var runtime = new FakeRuntime("runtime-1");
        var host = new FakeHost("host-1");
        host.Connect(runtime);
        await host.InitializeAsync(
            runtime.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);

        var request = CreateNewSessionRunRequest(RuntimeMode.Expert, "retry");
        var firstNewRun = await host.StartNewSessionRunAsync(
            runtime.RuntimeInstanceId,
            request,
            cancellationToken: testContext.CancellationToken);
        var duplicateNewRun = await host.StartNewSessionRunAsync(
            runtime.RuntimeInstanceId,
            request,
            cancellationToken: testContext.CancellationToken);

        Assert.IsNotNull(firstNewRun.Value);
        Assert.IsNotNull(duplicateNewRun.Value);
        Assert.AreEqual(firstNewRun.Value.SessionId, duplicateNewRun.Value.SessionId);
        Assert.AreEqual(firstNewRun.Value.RunId, duplicateNewRun.Value.RunId);
        Assert.IsTrue(duplicateNewRun.Value.IsDuplicate);

        var firstReceived = await host.ReceiveRunAsync(
            runtime.RuntimeInstanceId,
            firstNewRun.Value,
            cancellationToken: testContext.CancellationToken);

        var existingRequest = new ExistingSessionRunRequest(
            firstNewRun.Value.SessionId,
            "existing-run-key",
            InputOverride: [new TextContentBlock("continue")]);
        var firstExistingRun = await host.StartExistingSessionRunAsync(
            runtime.RuntimeInstanceId,
            existingRequest,
            cancellationToken: testContext.CancellationToken);
        var duplicateExistingRun = await host.StartExistingSessionRunAsync(
            runtime.RuntimeInstanceId,
            existingRequest,
            cancellationToken: testContext.CancellationToken);

        Assert.IsNotNull(firstExistingRun.Value);
        Assert.IsNotNull(duplicateExistingRun.Value);
        Assert.AreEqual(firstExistingRun.Value.RunId, duplicateExistingRun.Value.RunId);
        Assert.IsTrue(duplicateExistingRun.Value.IsDuplicate);

        var secondReceived = await host.ReceiveRunAsync(
            runtime.RuntimeInstanceId,
            firstExistingRun.Value,
            cancellationToken: testContext.CancellationToken);

        var conflictingSelection = request.Selection with { SelectionVersion = 2 };
        var selectionConflict = await host.UpdateSelectionAsync(
            runtime.RuntimeInstanceId,
            new FakeSelectionUpdate(
                firstNewRun.Value.SessionId,
                ExpectedSelectionVersion: 99,
                conflictingSelection),
            testContext.CancellationToken);
        var expert = Assert.IsInstanceOfType<ExpertModeOptions>(
            request.Selection.ModeOptions);
        var rehydrateMismatch = await host.RehydrateAsync(
            runtime.RuntimeInstanceId,
            new FakeRehydrateRequest(
                firstNewRun.Value.SessionId,
                expert.Agent.AgentId,
                "incorrect-prompt-hash"),
            testContext.CancellationToken);

        Assert.AreEqual(RuntimeErrorCodes.SelectionConflict, selectionConflict.Error?.Code);
        Assert.AreEqual(RuntimeErrorCodes.RehydrateHashMismatch, rehydrateMismatch.Error?.Code);
        Assert.AreEqual(1, runtime.SessionCount);
        Assert.AreEqual(2, runtime.RunCount);

        var acknowledged = await host.AcknowledgeAsync(
            runtime.RuntimeInstanceId,
            secondReceived.LastConfirmedGsn,
            cancellationToken: testContext.CancellationToken);

        Assert.IsNull(firstReceived.Error);
        Assert.IsNull(secondReceived.Error);
        Assert.IsNull(acknowledged.Error);
        Assert.AreEqual(0, runtime.BufferedEventCount);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task FakeRuntime_SelectionUpdatedAfterSnapshot_OnlyAffectsNextInvocation()
    {
        await using var runtime = new FakeRuntime("runtime-1");
        var host = new FakeHost("host-1");
        host.Connect(runtime);
        await host.InitializeAsync(
            runtime.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);

        var request = CreateNewSessionRunRequest(RuntimeMode.Expert, "snapshot-selection");
        var updatedSelection = request.Selection with
        {
            SelectionVersion = 2,
            DefaultSelection = new DefaultSelection("provider-2", "model-2"),
            ModeOptions = new ExpertModeOptions(
                new AgentRef(
                    "expert-agent",
                    "v2",
                    "Updated expert system prompt")),
            ToolCatalogVersion = "catalog-v2"
        };
        var snapshotCount = 0;
        runtime.SelectionUpdateAfterSnapshotCreated = (sessionId, _) =>
            ++snapshotCount == 1
                ? new FakeSelectionUpdate(
                    sessionId,
                    ExpectedSelectionVersion: 1,
                    updatedSelection)
                : null;

        var firstRun = await host.StartNewSessionRunAsync(
            runtime.RuntimeInstanceId,
            request,
            cancellationToken: testContext.CancellationToken);
        Assert.IsNotNull(firstRun.Value);
        var firstReceived = await host.ReceiveRunAsync(
            runtime.RuntimeInstanceId,
            firstRun.Value,
            cancellationToken: testContext.CancellationToken);

        var secondRun = await host.StartExistingSessionRunAsync(
            runtime.RuntimeInstanceId,
            new ExistingSessionRunRequest(
                firstRun.Value.SessionId,
                "snapshot-selection-next-run",
                InputOverride: [new TextContentBlock("continue")]),
            cancellationToken: testContext.CancellationToken);
        Assert.IsNotNull(secondRun.Value);
        var secondReceived = await host.ReceiveRunAsync(
            runtime.RuntimeInstanceId,
            secondRun.Value,
            cancellationToken: testContext.CancellationToken);

        var firstSnapshot = ReadInvocationSnapshot(firstReceived);
        var secondSnapshot = ReadInvocationSnapshot(secondReceived);

        Assert.AreEqual(2, snapshotCount);
        Assert.AreEqual("provider-1", firstSnapshot.ProviderId);
        Assert.AreEqual("model-1", firstSnapshot.ModelId);
        Assert.AreEqual("catalog-v1", firstSnapshot.ToolCatalogVersion);
        Assert.AreEqual("provider-2", secondSnapshot.ProviderId);
        Assert.AreEqual("model-2", secondSnapshot.ModelId);
        Assert.AreEqual("catalog-v2", secondSnapshot.ToolCatalogVersion);
        Assert.AreNotEqual(firstSnapshot.PromptHash, secondSnapshot.PromptHash);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task FakeHost_TwoRuntimesWithSameIds_KeepsRoutesAndCursorsIsolated()
    {
        await using var runtimeA = new FakeRuntime("runtime-a");
        await using var runtimeB = new FakeRuntime("runtime-b");
        var host = new FakeHost("host-1");
        host.Connect(runtimeA);
        host.Connect(runtimeB);
        await host.InitializeAsync(
            runtimeA.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);
        await host.InitializeAsync(
            runtimeB.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);

        var startedA = await host.StartNewSessionRunAsync(
            runtimeA.RuntimeInstanceId,
            CreateNewSessionRunRequest(RuntimeMode.Expert, "runtime-a"),
            cancellationToken: testContext.CancellationToken);
        var startedB = await host.StartNewSessionRunAsync(
            runtimeB.RuntimeInstanceId,
            CreateNewSessionRunRequest(RuntimeMode.Meeting, "runtime-b"),
            cancellationToken: testContext.CancellationToken);

        Assert.IsNotNull(startedA.Value);
        Assert.IsNotNull(startedB.Value);
        Assert.AreEqual(startedA.Value.RunId, startedB.Value.RunId);
        Assert.AreEqual(startedA.Value.FirstGsn, startedB.Value.FirstGsn);
        Assert.AreEqual(startedA.Value.LastGsn, startedB.Value.LastGsn);

        var receivedA = await host.ReceiveRunAsync(
            runtimeA.RuntimeInstanceId,
            startedA.Value,
            cancellationToken: testContext.CancellationToken);
        var receivedB = await host.ReceiveRunAsync(
            runtimeB.RuntimeInstanceId,
            startedB.Value,
            cancellationToken: testContext.CancellationToken);

        Assert.IsNull(receivedA.Error);
        Assert.IsNull(receivedB.Error);
        Assert.HasCount(
            ExpectedClosureMessageTypes.Length,
            host.GetRunEvents(runtimeA.RuntimeInstanceId, startedA.Value.RunId));
        Assert.HasCount(
            ExpectedClosureMessageTypes.Length,
            host.GetRunEvents(runtimeB.RuntimeInstanceId, startedB.Value.RunId));
        Assert.AreEqual(2, host.KnownRunCount);

        await host.AcknowledgeAsync(
            runtimeA.RuntimeInstanceId,
            receivedA.LastConfirmedGsn,
            cancellationToken: testContext.CancellationToken);
        await host.AcknowledgeAsync(
            runtimeB.RuntimeInstanceId,
            receivedB.LastConfirmedGsn,
            cancellationToken: testContext.CancellationToken);

        Assert.AreEqual(receivedA.LastConfirmedGsn, runtimeA.LastConfirmedGsn);
        Assert.AreEqual(receivedB.LastConfirmedGsn, runtimeB.LastConfirmedGsn);
        Assert.AreEqual(0, runtimeA.BufferedEventCount);
        Assert.AreEqual(0, runtimeB.BufferedEventCount);
    }

    [TestMethod]
    [Timeout(5000, CooperativeCancellation = true)]
    public async Task FakeRuntime_WrongInstanceOperations_AreRejectedWithoutStateChanges()
    {
        await using var runtimeA = new FakeRuntime("runtime-a");
        await using var runtimeB = new FakeRuntime("runtime-b");
        var host = new FakeHost("host-1");
        host.Connect(runtimeA);
        host.Connect(runtimeB);
        await host.InitializeAsync(
            runtimeA.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);
        await host.InitializeAsync(
            runtimeB.RuntimeInstanceId,
            cancellationToken: testContext.CancellationToken);

        var startedA = await host.StartNewSessionRunAsync(
            runtimeA.RuntimeInstanceId,
            CreateNewSessionRunRequest(RuntimeMode.Expert, "runtime-a"),
            cancellationToken: testContext.CancellationToken);
        var startedB = await host.StartNewSessionRunAsync(
            runtimeB.RuntimeInstanceId,
            CreateNewSessionRunRequest(RuntimeMode.Work, "runtime-b"),
            cancellationToken: testContext.CancellationToken);
        Assert.IsNotNull(startedA.Value);
        Assert.IsNotNull(startedB.Value);

        var receivedA = await host.ReceiveRunAsync(
            runtimeA.RuntimeInstanceId,
            startedA.Value,
            cancellationToken: testContext.CancellationToken);
        await host.ReceiveRunAsync(
            runtimeB.RuntimeInstanceId,
            startedB.Value,
            cancellationToken: testContext.CancellationToken);

        var wrongAcknowledge = await host.AcknowledgeAsync(
            runtimeA.RuntimeInstanceId,
            receivedA.LastConfirmedGsn,
            targetRuntimeInstanceId: runtimeB.RuntimeInstanceId,
            testContext.CancellationToken);
        var wrongQuery = await host.QueryRunAsync(
            runtimeA.RuntimeInstanceId,
            startedA.Value.RunId,
            targetRuntimeInstanceId: runtimeB.RuntimeInstanceId,
            testContext.CancellationToken);
        var wrongCancel = await host.CancelRunAsync(
            runtimeA.RuntimeInstanceId,
            startedA.Value.RunId,
            targetRuntimeInstanceId: runtimeB.RuntimeInstanceId,
            testContext.CancellationToken);

        Assert.AreEqual(RuntimeErrorCodes.InstanceMismatch, wrongAcknowledge.Error?.Code);
        Assert.AreEqual(RuntimeErrorCodes.InstanceMismatch, wrongQuery.Error?.Code);
        Assert.AreEqual(RuntimeErrorCodes.InstanceMismatch, wrongCancel.Error?.Code);
        Assert.AreEqual(0, runtimeA.LastConfirmedGsn);
        Assert.AreEqual(ExpectedClosureMessageTypes.Length, runtimeA.BufferedEventCount);
        Assert.AreEqual(RunStatus.Completed, runtimeA.GetRunState(startedA.Value.RunId).Status);
        Assert.AreEqual(RunStatus.Completed, runtimeB.GetRunState(startedB.Value.RunId).Status);

        using var payloadDocument = JsonDocument.Parse("{}");
        await runtimeA.InjectEventAsync(
            new RuntimeEventEnvelope(
                runtimeB.RuntimeInstanceId,
                100,
                startedA.Value.RunId,
                100,
                MessageTypes.TextDelta,
                DateTimeOffset.UnixEpoch,
                payloadDocument.RootElement.Clone()),
            testContext.CancellationToken);
        var wrongEvent = await host.ReceiveSingleAsync(
            runtimeA.RuntimeInstanceId,
            testContext.CancellationToken);

        Assert.AreEqual(RuntimeErrorCodes.InstanceMismatch, wrongEvent.Error?.Code);
        Assert.HasCount(0, wrongEvent.Events);
        Assert.AreEqual(2, host.KnownRunCount);
        Assert.AreEqual(ExpectedClosureMessageTypes.Length, runtimeB.BufferedEventCount);
    }

    private static NewSessionRunRequest CreateNewSessionRunRequest(
        RuntimeMode mode,
        string keySuffix)
    {
        var primaryAgent = new AgentRef(
            $"{mode.ToString().ToLowerInvariant()}-agent",
            "v1",
            $"{mode} system prompt");
        ModeOptions modeOptions = mode switch
        {
            RuntimeMode.Expert => new ExpertModeOptions(primaryAgent),
            RuntimeMode.Meeting => new MeetingModeOptions(
                [
                    primaryAgent,
                    new AgentRef("meeting-reviewer", "v1", "Review the meeting")
                ],
                primaryAgent.AgentId),
            RuntimeMode.Work => new WorkModeOptions(
                primaryAgent,
                [new AgentRef("work-specialist", "v1", "Execute one work step")]),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };

        return new NewSessionRunRequest(
            $"session-key-{keySuffix}",
            $"run-key-{keySuffix}",
            mode,
            new NextTurnSelection(
                1,
                mode,
                new DefaultSelection("provider-1", "model-1"),
                modeOptions,
                ToolCatalogVersion: "catalog-v1"),
            [new TextContentBlock("Start")]);
    }

    private static InvocationSnapshot ReadInvocationSnapshot(
        FakeHostReceiveResult received)
    {
        Assert.IsNull(received.Error);
        var invocationEvent = received.Events.Single(runtimeEvent =>
            runtimeEvent.MessageType == MessageTypes.InvocationStarted);
        var started = JsonSerializer.Deserialize(
            invocationEvent.Payload,
            RuntimeJsonContext.Default.InvocationStartedEvent);
        Assert.IsNotNull(started);
        return started.Snapshot;
    }
}
