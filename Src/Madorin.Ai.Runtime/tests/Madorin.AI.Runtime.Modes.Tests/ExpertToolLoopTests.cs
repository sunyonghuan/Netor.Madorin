using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Madorin.AI.Runtime.Tools.Builtin;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class ExpertToolLoopTests
{
    private static readonly string[] ExpectedSecondRequestRoles =
    [
        RuntimeProviderRoles.System,
        RuntimeProviderRoles.User,
        RuntimeProviderRoles.Assistant,
        RuntimeProviderRoles.Tool
    ];
    private static readonly string[] ExpectedPersistedRoles =
        ["user", "assistant", "tool", "assistant"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task RunAsync_ToolCall_ExecutesOnceAndContinuesSameProvider()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "tool-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "tool-run",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Preparing,
                RunStatus.Running,
                TestContext.CancellationToken);

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
            var provider = new ToolLoopProvider();
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateRequest(),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, executor.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(item =>
                item.MessageType == MessageTypes.RunCompleted));
            Assert.IsNotNull(provider.SecondRequest);
            CollectionAssert.AreEqual(
                ExpectedSecondRequestRoles,
                provider.SecondRequest.Messages.Select(static message => message.Role).ToArray());
            var result = (ToolResultContentBlock)provider.SecondRequest.Messages[^1].Content[0];
            Assert.IsTrue(result.Success);
            Assert.AreEqual("call-1", result.CallId);
            StringAssert.Contains(((TextContentBlock)result.Content[0]).Text, "42");

            var history = await new ConversationStore(dataDirectory).ReadAllAsync(
                sessionId,
                TestContext.CancellationToken);
            CollectionAssert.AreEqual(
                ExpectedPersistedRoles,
                history.Select(static record => record.Role).ToArray());
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
    public async Task RunAsync_MemoryRead_ReturnsSnapshotToSameProvider()
    {
        var dataDirectory = Path.Combine(
            Path.GetTempPath(),
            $"madorin-expert-memory-tool-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dataDirectory);
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "memory-tool-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "memory-tool-run",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                runId,
                RunStatus.Preparing,
                RunStatus.Running,
                TestContext.CancellationToken);

            var userHome = Path.Combine(dataDirectory, "home");
            var workspace = Path.Combine(dataDirectory, "workspace");
            var memoryFiles = new MemoryFileService(userHome, workspace);
            const string expectedMemory = "# Memory\n\n- Preserve exact memory text.\n";
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                expectedMemory,
                TestContext.CancellationToken);

            using var outbox = new SqliteEventOutbox(connection);
            using var toolState = new SqliteToolIntentRepository(connection);
            var registry = new BuiltinToolRegistry();
            var validator = new JsonSchemaToolValidator();
            var catalogStore = new ToolCatalogStore(registry, validator);
            var catalog = catalogStore.CaptureSnapshot();
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(workspace));
            var memorySnapshots = new MemoryInvocationSnapshotStore();
            using var gateway = new ToolGateway(
                catalogStore,
                validator,
                authorization,
                [new BuiltinMemoryToolExecutor(memoryFiles, memorySnapshots)],
                toolState,
                outbox);
            var provider = new MemoryToolLoopProvider(expectedMemory);
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                agentContextComposer: new AgentContextComposer(memoryFiles),
                toolGateway: gateway,
                toolAuthorizationService: authorization,
                toolCatalogSnapshot: catalog,
                memorySnapshotStore: memorySnapshots);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateMemoryRequest(),
                "runtime-memory",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.ToolCallCompleted));
            Assert.IsTrue(events.Any(static item =>
                item.MessageType == MessageTypes.RunCompleted));
        }
        finally
        {
            if (Directory.Exists(dataDirectory))
            {
                Directory.Delete(dataDirectory, recursive: true);
            }
        }
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "tool-session-key",
            "tool-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("tool-loop", "tool-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Use tools when needed."))),
            [new TextContentBlock("Calculate the answer.")]);

    private static NewSessionRunRequest CreateMemoryRequest() =>
        new(
            "memory-tool-session-key",
            "memory-tool-run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("memory-tool-loop", "memory-tool-model"),
                new ExpertModeOptions(
                    new AgentRef("memory-agent", "v1", "Use memory when needed."))),
            [new TextContentBlock("Read the project memory.")]);

    private sealed class ToolLoopProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("tool-model", "Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public RuntimeProviderRequest? SecondRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == TestToolRegistry.ToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "call-1",
                    TestToolRegistry.ToolId,
                    "calculate",
                    "{\"value\":21}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            SecondRequest = request;
            var toolResult = (ToolResultContentBlock)request.Messages[^1].Content[0];
            Assert.AreEqual("call-1", toolResult.CallId);
            yield return new TextDeltaProviderEvent(request.InvocationId, "The answer is 42.");
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

    private sealed class MemoryToolLoopProvider(string expectedMemory)
        : IRuntimeProviderAdapter
    {
        public string ProviderId => "memory-tool-loop";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("memory-tool-model", "Memory Tool Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CallCount++;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (CallCount == 1)
            {
                Assert.IsNotNull(request.Tools);
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.MemoryReadToolId));
                Assert.IsTrue(request.Tools.Any(static tool =>
                    tool.ToolId == BuiltinToolRegistry.MemoryAppendToolId));
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "memory-call-1",
                    BuiltinToolRegistry.MemoryReadToolId,
                    "read_memory",
                    "{\"scope\":\"project\"}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var result = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "memory-call-1");
            Assert.IsTrue(result.Success);
            Assert.AreEqual(BuiltinToolRegistry.MemoryReadToolId, result.ToolId);
            var resultText = Assert.IsInstanceOfType<TextContentBlock>(
                Assert.ContainsSingle(result.Content)).Text;
            using var resultJson = JsonDocument.Parse(resultText);
            Assert.AreEqual(
                expectedMemory,
                resultJson.RootElement.GetProperty("content").GetString());
            Assert.IsFalse(string.IsNullOrWhiteSpace(
                resultJson.RootElement.GetProperty("hash").GetString()));

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Memory result applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
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
