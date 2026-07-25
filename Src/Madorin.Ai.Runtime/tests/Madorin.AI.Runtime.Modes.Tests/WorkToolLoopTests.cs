using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Work;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class WorkToolLoopTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunAsync_WorkerToolCall_ExecutesWithStepLineageAndCompletesStep()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-tool-{Guid.NewGuid():N}");
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
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            await SaveDelegatedWorkerGrantAsync(authorization, dataDirectory, ct);
            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var provider = new WorkToolLoopProvider();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var sessionRepo = new SqliteSessionRepository(connection);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                toolStateStore: toolState,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog,
                workRepo: new SqliteWorkRepository(connection),
                connection: connection);

            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                "run-1",
                "session-1",
                CreateRequest(),
                "runtime-1",
                ct))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(RunStatus.Completed, await sessionRepo.GetRunStatusAsync("run-1", ct));
            Assert.AreEqual(1, executor.CallCount);
            Assert.IsNotNull(executor.LastInvocation);
            Assert.AreEqual("worker", executor.LastInvocation.AgentId);
            Assert.AreEqual("manager", executor.LastInvocation.ParentAgentId);
            Assert.AreEqual("run-1:step-1", executor.LastInvocation.WorkStepId);
            Assert.AreEqual("1", executor.LastInvocation.PlanVersion);
            Assert.IsFalse(string.IsNullOrWhiteSpace(executor.LastInvocation.ParentInvocationId));
            Assert.AreEqual(2, provider.WorkerCallCount);
            Assert.IsNotNull(provider.WorkerContinuationRequest);
            var toolResult = (ToolResultContentBlock)provider.WorkerContinuationRequest.Messages[^1].Content[0];
            Assert.IsTrue(toolResult.Success);
            Assert.AreEqual("call-work-1", toolResult.CallId);

            var intent = await toolState.GetIntentAsync("call-work-1", ct);
            Assert.IsNotNull(intent);
            Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
            Assert.AreEqual("worker", intent.AgentId);
            Assert.AreEqual("manager", intent.ParentAgentId);
            Assert.AreEqual("run-1:step-1", intent.WorkStepId);
            Assert.AreEqual("1", intent.PlanVersion);

            var completed = await sessionRepo.GetCompletedWorkStepResultAsync(
                "run-1:step-1",
                "1",
                ComputeHash([new TextContentBlock("Prepare the release notes.")]),
                ct);
            Assert.IsNotNull(completed);
            StringAssert.Contains(completed, "tool-result:worker");
            Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_WorkerToolCallWithoutDelegatedGrant_RequiresPermissionAndPersistsLineage()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-tool-denied-{Guid.NewGuid():N}");
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
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var provider = new WorkToolLoopProvider();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var sessionRepo = new SqliteSessionRepository(connection);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                toolStateStore: toolState,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog,
                workRepo: new SqliteWorkRepository(connection),
                connection: connection);

            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await CollectAsync(orchestrator.RunAsync(
                    "run-1",
                    "session-1",
                    CreateRequest(),
                    "runtime-1",
                    ct), ct));
            StringAssert.Contains(exception.Message, "No effective Grant permits");

            Assert.AreEqual(0, executor.CallCount);
            Assert.IsNull(executor.LastInvocation);
            Assert.AreEqual(1, provider.WorkerCallCount);
            Assert.IsNull(provider.WorkerContinuationRequest);

            var intent = await toolState.GetIntentAsync("call-work-1", ct);
            Assert.IsNotNull(intent);
            Assert.AreEqual(ToolIntentStatus.Pending, intent.Status);
            Assert.AreEqual("worker", intent.AgentId);
            Assert.AreEqual("manager", intent.ParentAgentId);
            Assert.AreEqual("run-1:step-1", intent.WorkStepId);
            Assert.AreEqual("1", intent.PlanVersion);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task RunAsync_WorkerPermissionWait_PersistsWaitingStateAndResumesAfterGrant()
    {
        var ct = TestContext.CancellationToken;
        var dataDirectory = Path.Combine(
            TestContext.TestRunDirectory ?? Path.GetTempPath(),
            $"work-tool-wait-{Guid.NewGuid():N}");
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
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new TestToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(dataDirectory));
            var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
            await SaveManagerDelegationRootGrantAsync(authorization, dataDirectory, expiresAt, ct);
            var executor = new TestToolExecutor();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [executor],
                toolState,
                outbox);
            var permissionReceived =
                new TaskCompletionSource<ToolPermissionRequest>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePermission =
                new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var consent = new ToolConsentCoordinator(
                gateway,
                authorization,
                catalog,
                toolPermissionHandler: async (permissionRequest, token) =>
                {
                    permissionReceived.TrySetResult(permissionRequest);
                    await releasePermission.Task.WaitAsync(token).ConfigureAwait(false);
                    return new ToolPermissionResponse(
                        permissionRequest.CorrelationId ?? string.Empty,
                        permissionRequest.CallId,
                        ToolAuthorizationDecision.Granted,
                        CreateWorkerGrant(
                            "grant-worker-approved",
                            dataDirectory,
                            permissionRequest.CallId,
                            expiresAt,
                            permissionRequest.ApprovalRequestId));
                },
                toolStateStore: toolState);
            var provider = new WorkToolLoopProvider();
            var memoryFiles = new MemoryFileService(
                Path.Join(dataDirectory, "home"),
                Path.Join(dataDirectory, "workspace"));
            var sessionRepo = new SqliteSessionRepository(connection);
            var workRepo = new SqliteWorkRepository(connection);
            var orchestrator = new WorkModeOrchestrator(
                _ => provider,
                new ConversationStore(dataDirectory),
                sessionRepo,
                outbox,
                new AgentContextComposer(memoryFiles),
                toolStateStore: toolState,
                toolGateway: gateway,
                toolCatalogSnapshot: catalog,
                toolConsentCoordinator: consent,
                workRepo: workRepo,
                connection: connection);

            await EnsureSessionRunAsync(connection, "session-1", "run-1", ct);

            var runTask = CollectAsync(orchestrator.RunAsync(
                "run-1",
                "session-1",
                CreateRequest(),
                "runtime-1",
                ct), ct);
            var permissionRequest = await permissionReceived.Task.WaitAsync(ct)
                .ConfigureAwait(false);
            var resumeWhileWaiting = await workRepo.GetResumeStateAsync("session-1", ct)
                .ConfigureAwait(false);

            Assert.IsNotNull(resumeWhileWaiting);
            Assert.AreEqual(WorkSessionStatus.WaitingForApproval, resumeWhileWaiting.Status);
            Assert.AreEqual(permissionRequest.ApprovalRequestId, resumeWhileWaiting.PendingApprovalRequestId);
            Assert.IsNotNull(resumeWhileWaiting.CurrentStep);
            Assert.AreEqual(WorkStepLifecycleStatus.WaitingForApproval, resumeWhileWaiting.CurrentStep.Status);
            Assert.AreEqual("run-1:step-1", resumeWhileWaiting.CurrentStep.StepId);
            Assert.AreEqual(0, executor.CallCount);

            releasePermission.SetResult();
            var events = await runTask.ConfigureAwait(false);
            var resumeAfterGrant = await workRepo.GetResumeStateAsync("session-1", ct)
                .ConfigureAwait(false);

            Assert.AreEqual(MessageTypes.RunCompleted, events[^1].MessageType);
            Assert.AreEqual(RunStatus.Completed, await sessionRepo.GetRunStatusAsync("run-1", ct));
            Assert.AreEqual(1, executor.CallCount);
            Assert.IsNotNull(resumeAfterGrant);
            Assert.AreEqual(WorkSessionStatus.Completed, resumeAfterGrant.Status);
            Assert.IsNull(resumeAfterGrant.PendingApprovalRequestId);
            var completedStep = Assert.ContainsSingle(resumeAfterGrant.Steps);
            Assert.AreEqual(WorkStepLifecycleStatus.Completed, completedStep.Status);
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static async Task<List<RuntimeEventEnvelope>> CollectAsync(
        IAsyncEnumerable<RuntimeEventEnvelope> events,
        CancellationToken ct)
    {
        var collected = new List<RuntimeEventEnvelope>();
        await foreach (var envelope in events.WithCancellation(ct))
        {
            collected.Add(envelope);
        }

        return collected;
    }

    private static async Task SaveDelegatedWorkerGrantAsync(
        ToolAuthorizationService authorization,
        string workspaceRoot,
        CancellationToken ct)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        await authorization.SaveGrantAsync(
            CreateManagerGrant(workspaceRoot, expiresAt),
            ct: ct);
        await authorization.SaveGrantAsync(
            CreateWorkerGrant(
                "grant-worker",
                workspaceRoot,
                "call-work-1",
                expiresAt,
                approvalRequestId: null),
            ct: ct);
    }

    private static Task SaveManagerDelegationRootGrantAsync(
        ToolAuthorizationService authorization,
        string workspaceRoot,
        DateTimeOffset expiresAt,
        CancellationToken ct) =>
        authorization.SaveGrantAsync(
            CreateManagerGrant(workspaceRoot, expiresAt),
            ct: ct);

    private static ToolGrant CreateManagerGrant(string workspaceRoot, DateTimeOffset expiresAt) =>
        new(
            "grant-manager",
            "run-1",
            workspaceRoot,
            [],
            [],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            expiresAt,
            AllowDelegation: true,
            DelegatedAgentIds: ["worker"],
            AllowedToolIds: [TestToolRegistry.ToolId],
            MaximumRisk: ToolRiskLevel.Low,
            AgentId: "manager");

    private static ToolGrant CreateWorkerGrant(
        string grantId,
        string workspaceRoot,
        string callId,
        DateTimeOffset expiresAt,
        string? approvalRequestId) =>
        new(
            grantId,
            "run-1",
            workspaceRoot,
            [],
            [],
            AllowOverwrite: false,
            AllowMove: false,
            AllowDelete: false,
            AllowedExecutables: [],
            AllowPowerShell: false,
            new NetworkPolicy(),
            expiresAt,
            AllowDelegation: false,
            DelegatedAgentIds: [],
            AllowedToolIds: [TestToolRegistry.ToolId],
            AllowedCallIds: [callId],
            MaximumRisk: ToolRiskLevel.Low,
            AgentId: "worker",
            ParentGrantId: "grant-manager",
            RootGrantId: "grant-manager",
            DelegationChain: ["grant-manager"],
            ApprovalRequestId: approvalRequestId);

    private static NewSessionRunRequest CreateRequest()
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Create a one-step plan.",
            "tool-loop",
            "tool-model");
        var worker = new AgentRef(
            "worker",
            "v1",
            "Execute the assigned step.",
            "tool-loop",
            "tool-model",
            AllowedToolIds: [TestToolRegistry.ToolId]);
        return new NewSessionRunRequest(
            "session-key",
            "run-key",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("tool-loop", "tool-model"),
                new WorkModeOptions(
                    manager,
                    [worker],
                    WorkflowPolicy.Default),
                ToolCatalogVersion: "tools-v1"),
            [new TextContentBlock("Prepare the release notes.")]);
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

    private static string ComputeHash(ContentBlock[] input)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            input,
            RuntimeJsonContext.Default.ContentBlockArray);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private sealed class WorkToolLoopProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int WorkerCallCount { get; private set; }

        public RuntimeProviderRequest? WorkerContinuationRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (string.Equals(request.AgentId, "manager", StringComparison.Ordinal))
            {
                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    "result:manager");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            WorkerCallCount++;
            if (WorkerCallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == TestToolRegistry.ToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "call-work-1",
                    TestToolRegistry.ToolId,
                    "calculate",
                    "{\"value\":21}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            WorkerContinuationRequest = request;
            var toolResult = (ToolResultContentBlock)request.Messages[^1].Content[0];
            Assert.AreEqual("call-work-1", toolResult.CallId);
            Assert.IsTrue(toolResult.Success);
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "tool-result:worker");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class TestToolRegistry : IBuiltinToolRegistry
    {
        public const string ToolId = "builtin.fs.calculate";

        private static readonly ToolDescriptor Descriptor = new(
            ToolId,
            "builtin",
            "Calculate",
            "Doubles an integer.",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}",
            "{\"type\":\"object\",\"properties\":{\"value\":{\"type\":\"integer\"}},\"required\":[\"value\"],\"additionalProperties\":false}");

        public string CatalogVersion => "test-v1";

        public IReadOnlyList<ToolDescriptor> GetTools() => [Descriptor];

        public IReadOnlyList<ToolDescriptor> GetTools(string[] toolIds) =>
            toolIds.Contains(ToolId, StringComparer.Ordinal) ? [Descriptor] : [];
    }

    private sealed class TestToolExecutor : IToolExecutor
    {
        public int CallCount { get; private set; }

        public ToolInvocation? LastInvocation { get; private set; }

        public bool CanExecute(string toolId) =>
            string.Equals(toolId, TestToolRegistry.ToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            LastInvocation = invocation;
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                "{\"value\":42}"));
        }
    }
}
