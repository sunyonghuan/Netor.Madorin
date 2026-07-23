using System.Runtime.CompilerServices;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Modes.Expert;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.Modes.Tests;

[TestClass]
public sealed class RuntimeModeTests
{
    private static readonly string[] ExpectedModes = ["Expert", "Meeting", "Work"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void RuntimeMode_DefinesTheThreeV1Modes()
    {
        CollectionAssert.AreEquivalent(
            ExpectedModes,
            Enum.GetNames<RuntimeMode>());
    }

    [TestMethod]
    public async Task ExpertMode_RunAsync_StreamsAndPersistsCompletedConversation()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "expert-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "expert-run",
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
            var store = new ConversationStore(dataDirectory);
            var provider = new FakeProviderAdapter();
            var memoryFiles = new MemoryFileService(
                Path.Combine(dataDirectory, "home"),
                Path.Combine(dataDirectory, "workspace"));
            await memoryFiles.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Global rule.\n",
                TestContext.CancellationToken);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Project rule.\n",
                TestContext.CancellationToken);
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                store,
                repository,
                outbox,
                new AgentContextComposer(memoryFiles));
            var request = CreateExpertRequest("What is the answer?");
            var events = new List<RuntimeEventEnvelope>();

            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                request,
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    MessageTypes.InvocationStarted,
                    MessageTypes.TextDelta,
                    MessageTypes.TextDelta,
                    MessageTypes.InvocationCompleted,
                    MessageTypes.RunCompleted
                },
                events.Select(static item => item.MessageType).ToArray());
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
            var history = await store.ReadAllAsync(sessionId, TestContext.CancellationToken);
            Assert.HasCount(2, history);
            Assert.AreEqual("user", history[0].Role);
            Assert.AreEqual("assistant", history[1].Role);
            var assistantContent = history[1].Content.Deserialize(
                RuntimeJsonContext.Default.ContentBlockArray);
            Assert.IsNotNull(assistantContent);
            Assert.HasCount(1, assistantContent);
            Assert.AreEqual("The answer is 42.", ((TextContentBlock)assistantContent[0]).Text);
            var runSnapshot = await repository.GetRunSnapshotAsync(
                runId,
                TestContext.CancellationToken);
            Assert.IsNotNull(runSnapshot);
            Assert.AreEqual("The answer is 42.", runSnapshot.TerminalText);
            Assert.IsNotNull(provider.CapturedRequest);
            Assert.HasCount(2, provider.CapturedRequest.Messages);
            Assert.AreEqual(
                RuntimeProviderRoles.System,
                provider.CapturedRequest.Messages[0].Role);
            Assert.IsTrue(
                ((TextContentBlock)provider.CapturedRequest.Messages[0].Content[0]).Text
                    .StartsWith("You are an expert.", StringComparison.Ordinal));
            var instructions =
                ((TextContentBlock)provider.CapturedRequest.Messages[0].Content[0]).Text;
            StringAssert.Contains(instructions, "Global rule.");
            StringAssert.Contains(instructions, "Project rule.");
            Assert.IsLessThan(
                instructions.IndexOf("Project rule.", StringComparison.Ordinal),
                instructions.IndexOf("Global rule.", StringComparison.Ordinal));
            Assert.AreEqual(
                RuntimeProviderRoles.User,
                provider.CapturedRequest.Messages[1].Role);
            Assert.AreEqual(
                "What is the answer?",
                ((TextContentBlock)provider.CapturedRequest.Messages[1].Content[0]).Text);
            var invocationStarted = events[0].Payload.Deserialize(
                RuntimeJsonContext.Default.InvocationStartedEvent);
            Assert.IsNotNull(invocationStarted);
            Assert.AreEqual(
                await memoryFiles.GetHashAsync(
                    MemoryScope.Global,
                    TestContext.CancellationToken),
                invocationStarted.Snapshot.GlobalMemoryHash);
            Assert.AreEqual(
                await memoryFiles.GetHashAsync(
                    MemoryScope.Project,
                    TestContext.CancellationToken),
                invocationStarted.Snapshot.ProjectMemoryHash);

            var secondRunId = await repository.CreateRunAsync(
                sessionId,
                "expert-run-2",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                secondRunId,
                RunStatus.Accepted,
                RunStatus.Preparing,
                TestContext.CancellationToken);
            await repository.TransitionRunStatusAsync(
                secondRunId,
                RunStatus.Preparing,
                RunStatus.Running,
                TestContext.CancellationToken);
            await foreach (var _ in orchestrator.RunAsync(
                secondRunId,
                sessionId,
                CreateExpertRequest("Continue."),
                "runtime-1",
                TestContext.CancellationToken))
            {
            }

            Assert.IsNotNull(provider.CapturedRequest);
            CollectionAssert.AreEqual(
                new[]
                {
                    RuntimeProviderRoles.System,
                    RuntimeProviderRoles.User,
                    RuntimeProviderRoles.Assistant,
                    RuntimeProviderRoles.User
                },
                provider.CapturedRequest.Messages
                    .Select(static message => message.Role)
                    .ToArray());
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ExpertMode_RunAsync_WhenDeltaReplayIsFull_EmitsDeltaDropped()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "drop-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "drop-run",
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

            var replayDirectory = Path.Combine(dataDirectory, "replay");
            using var outbox = new SqliteEventOutbox(
                connection,
                replayDirectory,
                maxReplayBytes: 4096);
            var orchestrator = new ExpertModeOrchestrator(
                new LargeDeltaProviderAdapter(),
                new ConversationStore(dataDirectory),
                repository,
                outbox);

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateExpertRequest("drop me"),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    MessageTypes.InvocationStarted,
                    MessageTypes.DeltaDropped,
                    MessageTypes.InvocationCompleted,
                    MessageTypes.RunCompleted
                },
                events.Select(static item => item.MessageType).ToArray());
            var dropped = events[1].Payload.Deserialize(
                RuntimeJsonContext.Default.DeltaDroppedEvent);
            Assert.IsNotNull(dropped);
            Assert.AreEqual(runId, dropped.RunId);
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ExpertMode_RunAsync_WhenProviderReturns401_WaitsAndRetriesExactlyOnce()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            await using var connection = await DataDirectoryInitializer.InitializeAsync(
                dataDirectory,
                TestContext.CancellationToken);
            var repository = new SqliteSessionRepository(connection);
            var sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "credential-session",
                TimeSpan.FromDays(1),
                TestContext.CancellationToken);
            var runId = await repository.CreateRunAsync(
                sessionId,
                "credential-run",
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
            var provider = new CredentialRetryProviderAdapter();
            var refreshCount = 0;
            var orchestrator = new ExpertModeOrchestrator(
                provider,
                new ConversationStore(dataDirectory),
                repository,
                outbox,
                credentialRefreshHandler: async (request, ct) =>
                {
                    refreshCount++;
                    await provider.UpdateCredentialsAsync(
                        new CredentialsUpdateParameters(
                            request.RunId,
                            request.ProviderId,
                            "refreshed-secret"),
                        ct);
                    return true;
                });

            var events = new List<RuntimeEventEnvelope>();
            await foreach (var envelope in orchestrator.RunAsync(
                runId,
                sessionId,
                CreateExpertRequest("credential test"),
                "runtime-1",
                TestContext.CancellationToken))
            {
                events.Add(envelope);
            }

            CollectionAssert.AreEqual(
                new[]
                {
                    MessageTypes.InvocationStarted,
                    MessageTypes.RunStatusChanged,
                    MessageTypes.RunStatusChanged,
                    MessageTypes.TextDelta,
                    MessageTypes.InvocationCompleted,
                    MessageTypes.RunCompleted
                },
                events.Select(static item => item.MessageType).ToArray());
            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, refreshCount);
            Assert.AreEqual("refreshed-secret", provider.Credential);
            Assert.HasCount(2, provider.AttemptNumbers);
            Assert.AreEqual(0, provider.AttemptNumbers[0]);
            Assert.AreEqual(1, provider.AttemptNumbers[1]);
            Assert.AreEqual(2, provider.InternalRequestIds.Distinct(StringComparer.Ordinal).Count());
            Assert.IsTrue(provider.InternalRequestIds.All(static requestId =>
                !string.IsNullOrWhiteSpace(requestId)));
            Assert.AreEqual(1, provider.InvocationIds.Distinct(StringComparer.Ordinal).Count());
            Assert.AreEqual(RunStatus.Completed, await repository.GetRunStatusAsync(runId));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ConversationStore_AppendAndReadAll_RoundTripsJsonlRecord()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            var expected = CreateConversationRecord(1, "hello");

            await store.AppendMessageAsync(
                "session-1",
                expected,
                TestContext.CancellationToken);
            var actual = await store.ReadAllAsync(
                "session-1",
                TestContext.CancellationToken);

            Assert.HasCount(1, actual);
            Assert.AreEqual(expected.MessageId, actual[0].MessageId);
            Assert.AreEqual(expected.Sequence, actual[0].Sequence);
            Assert.AreEqual(expected.Content.GetRawText(), actual[0].Content.GetRawText());
            var lines = await File.ReadAllLinesAsync(
                Path.Combine(dataDirectory, "messages", "session-1.jsonl"),
                TestContext.CancellationToken);
            Assert.HasCount(2, lines);
            using var header = JsonDocument.Parse(lines[0]);
            Assert.AreEqual("madorin.conversation.v1", header.RootElement.GetProperty("schema").GetString());
            Assert.AreEqual("session-1", header.RootElement.GetProperty("sessionId").GetString());
            Assert.AreEqual("Expert", header.RootElement.GetProperty("mode").GetString());
            using var message = JsonDocument.Parse(lines[1]);
            Assert.AreEqual(JsonValueKind.Array, message.RootElement.GetProperty("content").ValueKind);
            Assert.IsFalse(message.RootElement.TryGetProperty("contentJson", out _));
            Assert.AreEqual(1, await store.GetLastSequenceAsync("session-1"));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ConversationStore_RepairIfNeeded_TruncatesOnlyDamagedLastLineAndKeepsBackup()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                "session-repair",
                CreateConversationRecord(1, "complete"),
                TestContext.CancellationToken);
            var path = Path.Combine(dataDirectory, "messages", "session-repair.jsonl");
            await File.AppendAllTextAsync(
                path,
                "{\"messageId\":\"partial\"",
                TestContext.CancellationToken);

            var repaired = await store.RepairIfNeededAsync(
                "session-repair",
                TestContext.CancellationToken);

            Assert.IsTrue(repaired);
            var history = await store.ReadAllAsync(
                "session-repair",
                TestContext.CancellationToken);
            Assert.HasCount(1, history);
            Assert.AreEqual("complete", GetText(history[0]));
            Assert.HasCount(1, Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                "session-repair.jsonl.*.bak"));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    [TestMethod]
    public async Task ConversationStore_RepairIfNeeded_DamagedMiddleLineLeavesFileUnchanged()
    {
        var dataDirectory = CreateTemporaryDirectory();
        try
        {
            var store = new ConversationStore(dataDirectory);
            await store.AppendMessageAsync(
                "session-middle-damage",
                CreateConversationRecord(1, "first"),
                TestContext.CancellationToken);
            await store.AppendMessageAsync(
                "session-middle-damage",
                CreateConversationRecord(2, "second"),
                TestContext.CancellationToken);
            var path = Path.Combine(
                dataDirectory,
                "messages",
                "session-middle-damage.jsonl");
            var lines = await File.ReadAllLinesAsync(path, TestContext.CancellationToken);
            lines[1] = "{\"messageId\":";
            await File.WriteAllLinesAsync(path, lines, TestContext.CancellationToken);
            var before = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);

            await Assert.ThrowsExactlyAsync<InvalidDataException>(
                () => store.RepairIfNeededAsync(
                    "session-middle-damage",
                    TestContext.CancellationToken));

            var after = await File.ReadAllBytesAsync(path, TestContext.CancellationToken);
            CollectionAssert.AreEqual(before, after);
            Assert.IsEmpty(Directory.GetFiles(
                Path.GetDirectoryName(path)!,
                "session-middle-damage.jsonl.*.bak"));
        }
        finally
        {
            DeleteTemporaryDirectory(dataDirectory);
        }
    }

    private static NewSessionRunRequest CreateExpertRequest(string input) =>
        new(
            "session-key",
            "run-key",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("fake", "fake-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "You are an expert."))),
            [new TextContentBlock(input)]);

    private static ConversationRecordV1 CreateConversationRecord(long sequence, string text)
    {
        var content = JsonSerializer.SerializeToElement(
            new ContentBlock[] { new TextContentBlock(text) },
            RuntimeJsonContext.Default.ContentBlockArray);
        return new ConversationRecordV1(
            $"message-{sequence}",
            sequence,
            "invocation-1",
            "agent-1",
            "user",
            content,
            DateTimeOffset.UnixEpoch.AddSeconds(sequence));
    }

    private static string GetText(ConversationRecordV1 record)
    {
        var content = record.Content.Deserialize(RuntimeJsonContext.Default.ContentBlockArray);
        return ((TextContentBlock)content![0]).Text;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class FakeProviderAdapter : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public RuntimeProviderRequest? CapturedRequest { get; private set; }

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            CapturedRequest = request;
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "The answer is ");
            yield return new TextDeltaProviderEvent(request.InvocationId, "42.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class LargeDeltaProviderAdapter : IRuntimeProviderAdapter
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, new string('x', 8000));
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CredentialRetryProviderAdapter :
        IRuntimeProviderAdapter,
        IRuntimeProviderCredentialUpdater
    {
        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public int CallCount { get; private set; }

        public string? Credential { get; private set; }

        public List<int> AttemptNumbers { get; } = [];

        public List<string?> InternalRequestIds { get; } = [];

        public List<string> InvocationIds { get; } = [];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            CallCount++;
            AttemptNumbers.Add(request.AttemptNumber);
            InternalRequestIds.Add(request.InternalRequestId);
            InvocationIds.Add(request.InvocationId);
            if (!string.Equals(Credential, "refreshed-secret", StringComparison.Ordinal))
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        RuntimeErrorCodes.AuthenticationFailed,
                        "authentication",
                        "The fake credential was rejected.",
                        IsRetryable: false,
                        ProviderDetails: null,
                        DiagnosticId: "fake-401"));
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "authenticated");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct = default)
        {
            Credential = parameters.Credential;
            return Task.CompletedTask;
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
