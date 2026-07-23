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
            Assert.AreEqual(2, provider.WorkerCallCount);
            Assert.IsNotNull(provider.WorkerContinuationRequest);
            var toolResult = (ToolResultContentBlock)provider.WorkerContinuationRequest.Messages[^1].Content[0];
            Assert.IsTrue(toolResult.Success);
            Assert.AreEqual("call-work-1", toolResult.CallId);

            var intent = await toolState.GetIntentAsync("call-work-1", ct);
            Assert.IsNotNull(intent);
            Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
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

        public bool CanExecute(string toolId) =>
            string.Equals(toolId, TestToolRegistry.ToolId, StringComparison.Ordinal);

        public Task<ToolResult> ExecuteAsync(
            ToolInvocation invocation,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(new ToolResult(
                invocation.CallId,
                invocation.ToolId,
                true,
                "{\"value\":42}"));
        }
    }
}
