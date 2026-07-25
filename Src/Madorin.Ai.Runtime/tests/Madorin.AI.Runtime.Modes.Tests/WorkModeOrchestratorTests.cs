using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkModeOrchestratorTests
{
    private static readonly string[] ExpectedAgentIds = ["manager", "worker"];
    private static readonly string[] ExpectedRetryAgentIds = ["manager", "worker", "worker"];
    private static readonly string[] ExpectedCredentialRetryAgentIds = ["manager", "worker", "worker"];
    private static readonly string[] ExpectedReplayDeltas = ["result:worker"];
    private static readonly string?[] ExpectedCanonicalKinds = ["work.plan", "work.step"];
    private const string DuplicateGoalPlanJson =
        "{\"planVersion\":\"1\",\"goal\":\"Ship\",\"steps\":["
        + "{\"stepId\":\"step-a\",\"goal\":\"Repeat target\",\"targetAgentId\":\"worker\"},"
        + "{\"stepId\":\"step-b\",\"goal\":\"Repeat target\",\"targetAgentId\":\"worker\",\"dependsOn\":[\"step-a\"]}]}";

    private readonly TestContext _testContext;

    public WorkModeOrchestratorTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public async Task RunAsync_SingleStep_RunsManagerThenChildAndEmitsLifecycleEvents()
    {
        var result = await RunAsync(repetitionCount: 1);
        var events = Assert.ContainsSingle(result.EventBatches);

        Assert.AreEqual(MessageTypes.RunAccepted, events[0].MessageType);
        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedAgentIds, result.InvokedAgentIds);
        Assert.HasCount(2, result.CapturedRequests);
        Assert.HasCount(2, result.InvocationSnapshots);
        var managerInput = ((TextContentBlock)result.CapturedRequests[0].Messages[1].Content[0]).Text;
        StringAssert.Contains(managerInput, "Prepare the release notes.");
        StringAssert.Contains(managerInput, "Available agents and declared capabilities:");
        StringAssert.Contains(managerInput, "worker");
        StringAssert.Contains(managerInput, "WorkflowPolicy:");
        foreach (var providerRequest in result.CapturedRequests)
        {
            var instructions = ((TextContentBlock)providerRequest.Messages[0].Content[0]).Text;
            StringAssert.Contains(instructions, "Shared global rule.");
            StringAssert.Contains(instructions, "Shared project rule.");
            Assert.IsLessThan(
                instructions.IndexOf("Shared project rule.", StringComparison.Ordinal),
                instructions.IndexOf("Shared global rule.", StringComparison.Ordinal));
        }

        var startedAgentIds = events
            .Where(static item => item.MessageType == MessageTypes.InvocationStarted)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent)
                ?? throw new InvalidDataException("The invocation.started payload was empty."))
            .ToArray();
        CollectionAssert.AreEqual(
            ExpectedAgentIds,
            startedAgentIds.Select(static item => item.Snapshot.AgentId).ToArray());
        Assert.IsNull(startedAgentIds[0].Snapshot.ParentInvocationId);
        Assert.AreEqual(
            startedAgentIds[0].InvocationId,
            startedAgentIds[1].Snapshot.ParentInvocationId);
        Assert.AreEqual("run-1:step-1", startedAgentIds[1].Snapshot.WorkStepId);
        Assert.AreEqual("1", startedAgentIds[1].Snapshot.WorkPlanVersion);
        foreach (var started in startedAgentIds)
        {
            Assert.IsTrue(result.InvocationSnapshots.TryGetValue(started.InvocationId, out var snapshot));
            Assert.AreEqual(
                started.Snapshot with { ContextProjection = null },
                snapshot with { ContextProjection = null });
            if (started.Snapshot.ContextProjection is { } expectedProjection)
            {
                var actualProjection = snapshot.ContextProjection;
                Assert.IsNotNull(actualProjection);
                Assert.AreEqual(
                    expectedProjection with { RetainedItems = [] },
                    actualProjection with { RetainedItems = [] });
                CollectionAssert.AreEqual(
                    expectedProjection.RetainedItems,
                    actualProjection.RetainedItems);
            }
        }

        Assert.HasCount(2, result.CanonicalHistory);
        CollectionAssert.AreEqual(
            ExpectedCanonicalKinds,
            result.CanonicalHistory
                .Select(static message => message.SummaryMetadata?.GetProperty("kind").GetString())
                .ToArray());
        var step = Assert.ContainsSingle(result.WorkSteps);
        Assert.AreEqual(result.CanonicalHistory[0].MessageId, result.PlanRevisions[0].PlanMessageId);
        Assert.AreEqual(result.CanonicalHistory[1].MessageId, step.StepMessageId);
        Assert.IsNotNull(step.CheckpointJson);
        StringAssert.Contains(step.CheckpointJson, startedAgentIds[1].InvocationId);
    }

    [TestMethod]
    public async Task RunAsync_CompletedStepOnSecondCall_ReusesPersistedResult()
    {
        var result = await RunAsync(repetitionCount: 2);

        Assert.HasCount(2, result.EventBatches);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedAgentIds, result.InvokedAgentIds);
        Assert.IsNotNull(result.CompletedStepResultJson);
        StringAssert.Contains(result.CompletedStepResultJson, "result:worker");

        var replayedDeltas = result.EventBatches[1]
            .Where(static item => item.MessageType == MessageTypes.TextDelta)
            .Select(static item => item.Payload.Deserialize(RuntimeJsonContext.Default.TextDeltaEvent)
                ?? throw new InvalidDataException("The output.delta payload was empty."))
            .Select(static item => item.Delta)
            .ToArray();
        CollectionAssert.AreEqual(ExpectedReplayDeltas, replayedDeltas);
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithContinuePolicy_DoesNotMarkStepCompleted()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Continue,
            failWorker: true);
        var events = Assert.ContainsSingle(result.EventBatches);
        var step = Assert.ContainsSingle(result.WorkSteps);

        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Failed, step.Status);
        Assert.IsNull(result.CompletedStepResultJson);
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithRetryPolicy_RetriesWithNewInvocationAndCompletesStep()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Retry,
            workerFailuresBeforeSuccess: 1,
            maxRetriesPerStep: 1,
            retryBackoffMilliseconds: 0);
        var events = Assert.ContainsSingle(result.EventBatches);
        var step = Assert.ContainsSingle(result.WorkSteps);
        var workerRequests = result.CapturedRequests
            .Where(static item => item.AgentId == "worker")
            .ToArray();

        Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, step.Status);
        Assert.AreEqual(2, step.AttemptCount);
        Assert.HasCount(2, workerRequests);
        Assert.AreNotEqual(workerRequests[0].InvocationId, workerRequests[1].InvocationId);
        Assert.IsNotNull(result.CompletedStepResultJson);
        StringAssert.Contains(result.CompletedStepResultJson, "result:worker");
        CollectionAssert.AreEqual(ExpectedRetryAgentIds, result.InvokedAgentIds);
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerAuthenticationFailure_WaitsAndRetriesExactlyOnce()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            requireWorkerCredentialRefresh: true);
        var events = Assert.ContainsSingle(result.EventBatches);
        var statusChanges = events
            .Where(static item => item.MessageType == MessageTypes.RunStatusChanged)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.RunStatusChangedEvent)
                ?? throw new InvalidDataException("The run.statusChanged payload was empty."))
            .ToArray();
        var workerRequests = result.CapturedRequests
            .Where(static request => string.Equals(request.AgentId, "worker", StringComparison.Ordinal))
            .ToArray();
        var refreshRequest = Assert.ContainsSingle(result.CredentialRefreshRequests);
        var waiting = Assert.ContainsSingle(result.CredentialWaitStates);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        CollectionAssert.AreEqual(ExpectedCredentialRetryAgentIds, result.InvokedAgentIds);
        CollectionAssert.AreEqual(
            new[]
            {
                RunStatus.WaitingForCredentials.ToString(),
                RunStatus.Running.ToString()
            },
            statusChanges.Select(static status => status.Status).ToArray());
        Assert.HasCount(2, workerRequests);
        Assert.AreEqual(0, workerRequests[0].AttemptNumber);
        Assert.AreEqual(1, workerRequests[1].AttemptNumber);
        Assert.AreEqual(workerRequests[0].InvocationId, workerRequests[1].InvocationId);
        Assert.AreNotEqual(workerRequests[0].InternalRequestId, workerRequests[1].InternalRequestId);
        Assert.AreEqual("run-1", refreshRequest.RunId);
        Assert.AreEqual("fake", refreshRequest.ProviderId);
        Assert.AreEqual(WorkSessionStatus.WaitingForCredentials, waiting.Status);
        Assert.IsNotNull(waiting.CurrentStep);
        Assert.AreEqual(WorkStepLifecycleStatus.WaitingForCredentials, waiting.CurrentStep.Status);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, Assert.ContainsSingle(result.WorkSteps).Status);
    }

    [TestMethod]
    public async Task RunAsync_CredentialRefreshTimeoutWithContinuePolicy_ResumesRunAndFailsStep()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.Continue,
            requireWorkerCredentialRefresh: true,
            credentialRefreshSucceeds: false);
        var events = Assert.ContainsSingle(result.EventBatches);
        var statusChanges = events
            .Where(static item => item.MessageType == MessageTypes.RunStatusChanged)
            .Select(static item => item.Payload.Deserialize(
                RuntimeJsonContext.Default.RunStatusChangedEvent)
                ?? throw new InvalidDataException("The run.statusChanged payload was empty."))
            .ToArray();

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Failed, Assert.ContainsSingle(result.WorkSteps).Status);
        Assert.HasCount(1, result.CredentialRefreshRequests);
        CollectionAssert.AreEqual(
            new[]
            {
                RunStatus.WaitingForCredentials.ToString(),
                RunStatus.Running.ToString()
            },
            statusChanges.Select(static status => status.Status).ToArray());
        Assert.AreEqual(1, events.Count(static item => item.MessageType == MessageTypes.InvocationFailed));
    }

    [TestMethod]
    public async Task RunAsync_WorkerFailureWithAskHost_PersistsWaitAndRetriesAfterApproval()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            failurePolicy: WorkStepFailurePolicy.AskHost,
            workerFailuresBeforeSuccess: 1,
            workflowApprovalDecision: ToolAuthorizationDecision.Granted);
        var step = Assert.ContainsSingle(result.WorkSteps);
        var approval = Assert.ContainsSingle(result.WorkflowApprovalRequests);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.AreEqual(WorkStepLifecycleStatus.Completed, step.Status);
        Assert.AreEqual(2, step.AttemptCount);
        Assert.AreEqual("run-1:step-1", approval.WorkStepId);
        Assert.AreEqual("1", approval.PlanVersion);
        Assert.AreEqual("manager", approval.ParentAgentId);
        StringAssert.StartsWith(approval.ApprovalRequestId, "work-approval-");
        Assert.IsNull(step.ErrorMessage);
    }

    [TestMethod]
    public async Task RunAsync_StrictLoopDetection_PausesRepeatedGoalUntilHostApproves()
    {
        var result = await RunAsync(
            repetitionCount: 1,
            workflowApprovalDecision: ToolAuthorizationDecision.Granted,
            managerOutput: DuplicateGoalPlanJson);

        Assert.AreEqual(RunStatus.Completed, result.RunStatus);
        Assert.HasCount(2, result.WorkSteps);
        Assert.IsTrue(result.WorkSteps.All(static step =>
            step.Status is WorkStepLifecycleStatus.Completed));
        var approval = Assert.ContainsSingle(result.WorkflowApprovalRequests);
        Assert.AreEqual("step-b", approval.WorkStepId);
        StringAssert.Contains(approval.Reason, "repeats the same target");
    }

    [TestMethod]
    public async Task RunAsync_PendingStepToolIntent_BlocksStepCompletion()
    {
        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await RunAsync(
                repetitionCount: 1,
                createPendingWorkStepToolIntent: true));

        StringAssert.Contains(
            ex.Message,
            "cannot be completed because 1 tool intent(s) are not terminal");
        StringAssert.Contains(ex.Message, "call-pending-step-1:Pending");
    }

    [TestMethod]
    public async Task RunAsync_LongPlanProjection_PersistsDecisionAndEmitsAdjustment()
    {
        var managerOutput = JsonSerializer.Serialize(
            new WorkPlanDraft(
                "1",
                new string('g', 5_000),
                [new WorkPlanStepDraft(
                    "step-long",
                    new string('s', 8_000),
                    "worker")]),
            RuntimeJsonContext.Default.WorkPlanDraft);
        var result = await RunAsync(
            repetitionCount: 1,
            managerOutput: managerOutput,
            contextPolicy: WorkContextPolicy.Default with { MaxTokens = 700 });
        var events = Assert.ContainsSingle(result.EventBatches);
        var adjustmentEnvelope = Assert.ContainsSingle(events.Where(static envelope =>
            envelope.MessageType == MessageTypes.ContextProjectionAdjusted));
        var adjustment = adjustmentEnvelope.Payload.Deserialize(
            RuntimeJsonContext.Default.ContextProjectionAdjustedEvent)
            ?? throw new InvalidDataException("The projection adjustment payload was empty.");
        var workerRequest = Assert.ContainsSingle(result.CapturedRequests.Where(static item =>
            item.AgentId == "worker"));
        var projection = workerRequest.Messages.Length > 0
            ? result.InvocationSnapshots[workerRequest.InvocationId].ContextProjection
            : null;

        Assert.IsNotNull(projection);
        Assert.IsLessThanOrEqualTo(projection.TokenLimit, projection.EstimatedTokens);
        Assert.AreEqual(projection.Strategy, adjustment.Strategy);
        Assert.AreEqual(projection.EstimatedTokens, adjustment.EstimatedTokens);
        Assert.AreEqual(projection.SummarizedUnitCount, adjustment.SummarizedUnitCount);
        CollectionAssert.AreEqual(projection.RetainedItems, adjustment.RetainedItems);
        CollectionAssert.Contains(projection.RetainedItems, "plan:1");
        CollectionAssert.Contains(projection.RetainedItems, "step:step-long");
    }

    private async Task<WorkRunResult> RunAsync(
        int repetitionCount,
        WorkStepFailurePolicy failurePolicy = WorkStepFailurePolicy.Stop,
        bool failWorker = false,
        int workerFailuresBeforeSuccess = 0,
        int? maxRetriesPerStep = null,
        int? retryBackoffMilliseconds = null,
        bool createPendingWorkStepToolIntent = false,
        ToolAuthorizationDecision? workflowApprovalDecision = null,
        string? managerOutput = null,
        WorkContextPolicy? contextPolicy = null,
        bool requireWorkerCredentialRefresh = false,
        bool credentialRefreshSucceeds = true)
    {
        var ct = _testContext.CancellationToken;
        var dataDirectory = Path.Combine(
            _testContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-{Guid.NewGuid():N}");
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
            using var toolStateStore = new SqliteToolIntentRepository(connection);
            var provider = new FakeProviderAdapter(
                failWorker,
                workerFailuresBeforeSuccess,
                managerOutput,
                requireWorkerCredentialRefresh);
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
            var sessionRepo = new SqliteSessionRepository(connection);
            var workRepo = new SqliteWorkRepository(connection);
            var workflowApprovalRequests = new List<ApprovalRequest>();
            var credentialRefreshRequests = new List<CredentialsRefreshRequestedEvent>();
            var credentialWaitStates = new List<WorkResumeState>();
            var store = new ConversationStore(dataDirectory);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                store,
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                toolStateStore: toolStateStore,
                workRepo: workRepo,
                connection: connection,
                workflowApprovalHandler: workflowApprovalDecision is null
                    ? null
                    : (approval, _) =>
                    {
                        workflowApprovalRequests.Add(approval);
                        return Task.FromResult(new ApprovalResponse(
                            approval.CorrelationId,
                            approval.ApprovalRequestId,
                            workflowApprovalDecision.Value));
                    },
                credentialRefreshHandler: requireWorkerCredentialRefresh
                    ? async (refresh, refreshCt) =>
                    {
                        credentialRefreshRequests.Add(refresh);
                        credentialWaitStates.Add(
                            await workRepo.GetResumeStateAsync("session-1", refreshCt)
                                ?? throw new InvalidDataException(
                                    "The Work credential-wait state was not persisted."));
                        if (credentialRefreshSucceeds)
                        {
                            await provider.UpdateCredentialsAsync(
                                new CredentialsUpdateParameters(
                                    refresh.RunId,
                                    refresh.ProviderId,
                                    "refreshed-secret"),
                                refreshCt);
                        }

                        return credentialRefreshSucceeds;
                    }
                    : null);
            var request = CreateRequest(
                failurePolicy,
                maxRetriesPerStep,
                retryBackoffMilliseconds,
                contextPolicy);
            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);
            if (createPendingWorkStepToolIntent)
            {
                await CreatePendingWorkStepToolIntentAsync(toolStateStore, ct);
            }

            var eventBatches = new List<IReadOnlyList<RuntimeEventEnvelope>>();

            for (var index = 0; index < repetitionCount; index++)
            {
                var events = new List<RuntimeEventEnvelope>();
                await foreach (var envelope in orchestrator.RunAsync(
                    "run-1",
                    "session-1",
                    request,
                    "runtime-1",
                    ct))
                {
                    events.Add(envelope);
                }

                eventBatches.Add(events);
            }

            var invocationSnapshots = new Dictionary<string, InvocationSnapshot>(StringComparer.Ordinal);
            foreach (var envelope in eventBatches.SelectMany(static item => item)
                .Where(static item => item.MessageType == MessageTypes.InvocationStarted))
            {
                var started = envelope.Payload.Deserialize(
                    RuntimeJsonContext.Default.InvocationStartedEvent)
                    ?? throw new InvalidDataException("The invocation.started payload was empty.");
                var snapshotJson = await sessionRepo.GetInvocationSnapshotJsonAsync(
                    started.InvocationId,
                    ct).ConfigureAwait(false)
                    ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was not persisted.");
                var snapshot = JsonSerializer.Deserialize(
                    snapshotJson,
                    RuntimeJsonContext.Default.InvocationSnapshot)
                    ?? throw new InvalidDataException(
                        $"Invocation snapshot '{started.InvocationId}' was empty.");
                invocationSnapshots[started.InvocationId] = snapshot;
            }

            var completed = await new SqliteSessionRepository(connection)
                .GetCompletedWorkStepResultAsync("run-1:step-1", "1", ComputeHash(request.InitialInput), ct);
            var runStatus = await new SqliteSessionRepository(connection)
                .GetRunStatusAsync("run-1", ct);
            var workSteps = await new SqliteWorkRepository(connection)
                .ListStepsAsync("session-1", "1", ct);
            var planRevisions = await new SqliteWorkRepository(connection)
                .ListPlanRevisionsAsync("session-1", ct);
            var canonicalHistory = await store.ReadAllAsync("session-1", ct);
            return new WorkRunResult(
                provider.InvokedAgentIds.ToArray(),
                provider.CapturedRequests.ToArray(),
                eventBatches,
                completed,
                runStatus,
                workSteps,
                invocationSnapshots,
                canonicalHistory,
                planRevisions,
                workflowApprovalRequests,
                credentialRefreshRequests,
                credentialWaitStates);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task CreatePendingWorkStepToolIntentAsync(
        SqliteToolIntentRepository toolStateStore,
        CancellationToken ct)
    {
        var createdAt = DateTimeOffset.UtcNow;
        var created = await toolStateStore.TryCreateIntentAsync(
            new ToolIntentState(
                "call-pending-step-1",
                "invocation-tool-1",
                "run-1",
                "session-1",
                "worker",
                ParentAgentId: "manager",
                "host.files.write",
                "tools-v1",
                "ARGS-HASH-1",
                ToolIntentStatus.Pending,
                GrantId: null,
                ApprovalRequestId: null,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                createdAt,
                SentAt: null,
                CompletedAt: null,
                WorkStepId: "run-1:step-1",
                PlanVersion: "1"),
            ct).ConfigureAwait(false);
        if (!created)
        {
            throw new InvalidOperationException("The pending tool intent test fixture was not created.");
        }
    }

    private static async Task EnsureSessionRunAsync(
        SqliteConnection connection,
        string sessionId,
        string runId,
        CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions(session_id, mode, status, created_at, updated_at)
            VALUES($sessionId, 'Work', 'Active', '2026-07-23T00:00:00.000Z', '2026-07-23T00:00:00.000Z');
            INSERT INTO runs(run_id, session_id, status, run_sequence, created_at, updated_at)
            VALUES($runId, $sessionId, 'Running', 1, '2026-07-23T00:00:00.000Z', '2026-07-23T00:00:00.000Z');
            """;
        command.Parameters.AddWithValue("$sessionId", sessionId);
        command.Parameters.AddWithValue("$runId", runId);
        await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
    private static NewSessionRunRequest CreateRequest(
        WorkStepFailurePolicy failurePolicy = WorkStepFailurePolicy.Stop,
        int? maxRetriesPerStep = null,
        int? retryBackoffMilliseconds = null,
        WorkContextPolicy? contextPolicy = null)
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Create a one-step plan.",
            "fake",
            "test-model");
        var worker = new AgentRef(
            "worker",
            "v1",
            "Execute the assigned step.",
            "fake",
            "test-model");
        var policy = WorkflowPolicy.Default with { FailurePolicy = failurePolicy };
        if (maxRetriesPerStep is not null || retryBackoffMilliseconds is not null)
        {
            policy = policy with
            {
                MaxRetriesPerStep = maxRetriesPerStep ?? policy.MaxRetriesPerStep,
                RetryBackoffMilliseconds = retryBackoffMilliseconds ?? policy.RetryBackoffMilliseconds
            };
        }

        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("fake", "test-model"),
                new WorkModeOptions(
                    manager,
                    [worker],
                    policy,
                    contextPolicy),
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock("Prepare the release notes.")]);
    }

    private sealed class FakeProviderAdapter :
        IRuntimeProviderAdapter,
        IRuntimeProviderCredentialUpdater
    {
        private readonly List<string> _invokedAgentIds = [];
        private readonly List<RuntimeProviderRequest> _capturedRequests = [];
        private readonly int _workerFailuresBeforeSuccess;
        private readonly string? _managerOutput;
        private readonly bool _requireWorkerCredentialRefresh;
        private int _workerFailureCount;

        public FakeProviderAdapter(
            bool failWorker = false,
            int workerFailuresBeforeSuccess = 0,
            string? managerOutput = null,
            bool requireWorkerCredentialRefresh = false)
        {
            _workerFailuresBeforeSuccess = failWorker
                ? int.MaxValue
                : workerFailuresBeforeSuccess;
            _managerOutput = managerOutput;
            _requireWorkerCredentialRefresh = requireWorkerCredentialRefresh;
        }

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = [new("test-model", "Test Model")];

        public IReadOnlyList<string> InvokedAgentIds => _invokedAgentIds;

        public IReadOnlyList<RuntimeProviderRequest> CapturedRequests => _capturedRequests;

        public string? Credential { get; private set; }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) =>
            Task.CompletedTask;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _invokedAgentIds.Add(request.AgentId);
            _capturedRequests.Add(request);
            await Task.Yield();
            if (_requireWorkerCredentialRefresh
                && string.Equals(request.AgentId, "worker", StringComparison.Ordinal)
                && !string.Equals(Credential, "refreshed-secret", StringComparison.Ordinal))
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        RuntimeErrorCodes.AuthenticationFailed,
                        "authentication",
                        "The fake credential was rejected.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        DiagnosticId: "diagnostic-worker-401"));
                yield break;
            }

            if (string.Equals(request.AgentId, "worker", StringComparison.Ordinal)
                && _workerFailureCount < _workerFailuresBeforeSuccess)
            {
                _workerFailureCount++;
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        "WorkerFailed",
                        "Provider",
                        "The worker failed.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        DiagnosticId: "diagnostic-worker-failed"));
                yield break;
            }

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                string.Equals(request.AgentId, "manager", StringComparison.Ordinal)
                    && _managerOutput is not null
                    ? _managerOutput
                    : $"result:{request.AgentId}");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Credential = parameters.Credential;
            return Task.CompletedTask;
        }
    }

    private static string ComputeHash(ContentBlock[] input)
    {
        var bytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            input,
            RuntimeJsonContext.Default.ContentBlockArray);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed record WorkRunResult(
        string[] InvokedAgentIds,
        RuntimeProviderRequest[] CapturedRequests,
        IReadOnlyList<IReadOnlyList<RuntimeEventEnvelope>> EventBatches,
        string? CompletedStepResultJson,
        RunStatus RunStatus,
        IReadOnlyList<WorkStepSnapshot> WorkSteps,
        IReadOnlyDictionary<string, InvocationSnapshot> InvocationSnapshots,
        IReadOnlyList<ConversationRecordV1> CanonicalHistory,
        IReadOnlyList<WorkPlanRevisionSnapshot> PlanRevisions,
        IReadOnlyList<ApprovalRequest> WorkflowApprovalRequests,
        IReadOnlyList<CredentialsRefreshRequestedEvent> CredentialRefreshRequests,
        IReadOnlyList<WorkResumeState> CredentialWaitStates);
}
