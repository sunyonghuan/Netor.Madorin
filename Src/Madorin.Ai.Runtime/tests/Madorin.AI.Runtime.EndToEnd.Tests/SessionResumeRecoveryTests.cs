using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class SessionResumeRecoveryTests
{
    private static readonly AgentRef ExpertAgent = new(
        "expert-agent",
        "v1",
        "Answer as the expert.",
        "resume-provider",
        "resume-model");
    private static readonly AgentRef MeetingAgentA = new(
        "meeting-agent-a",
        "v1",
        "Represent viewpoint A.",
        "resume-provider",
        "resume-model");
    private static readonly AgentRef MeetingAgentB = new(
        "meeting-agent-b",
        "v1",
        "Represent viewpoint B.",
        "resume-provider",
        "resume-model");
    private static readonly AgentRef WorkManager = new(
        "work-manager",
        "v1",
        "Create a one-step plan.",
        "resume-provider",
        "resume-model");
    private static readonly AgentRef WorkWorker = new(
        "work-worker",
        "v1",
        "Complete the assigned step.",
        "resume-provider",
        "resume-model");

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task ThreeModes_AfterRestartResumeUnifiedStateAndContinueMonotonicGsn()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var captures = new List<SessionRunCapture>();
        try
        {
            using (var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                       TestContext.CancellationToken))
            {
                var options = CreateOptions(workspace, secret, "three-modes-first");
                await using var server = await RuntimeServer.StartAsync(
                    options,
                    TestContext.CancellationToken);
                var serverTask = server.RunAsync(firstShutdown.Token);
                await using var client = new RuntimeClient(
                    new RuntimeClientOptions(
                        server.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: server.InstanceId,
                        HandshakeSecret: secret));
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(30));
                await client.ConnectAsync(timeout.Token);
                foreach (var mode in new[]
                         {
                             RuntimeMode.Expert,
                             RuntimeMode.Meeting,
                             RuntimeMode.Work
                         })
                {
                    captures.Add(await StartAndCompleteAsync(
                        client,
                        mode,
                        "initial",
                        timeout.Token));
                }

                firstShutdown.Cancel();
                await serverTask;
            }

            using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            var secondOptions = CreateOptions(
                workspace,
                secret,
                "three-modes-second",
                workPlanVersion: 2);
            await using var secondServer = await RuntimeServer.StartAsync(
                secondOptions,
                TestContext.CancellationToken);
            var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
            await using var secondClient = new RuntimeClient(
                new RuntimeClientOptions(
                    secondServer.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: secondServer.InstanceId,
                    HandshakeSecret: secret));
            using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            secondTimeout.CancelAfter(TimeSpan.FromSeconds(30));
            await secondClient.ConnectAsync(secondTimeout.Token);

            var resumeBySession = new Dictionary<string, SessionResumeResult>(StringComparer.Ordinal);
            foreach (var capture in captures)
            {
                var resume = await secondClient.ResumeSessionAsync(
                    capture.SessionId,
                    secondTimeout.Token);
                resumeBySession[capture.SessionId] = resume;
                Assert.AreEqual(capture.Mode, resume.Mode);
                Assert.AreEqual(1, resume.LatestSelectionVersion);
                Assert.IsNotNull(resume.Selection);
                Assert.AreEqual(capture.Mode, resume.Selection.Mode);
                Assert.IsNotNull(resume.LatestRun);
                Assert.AreEqual(capture.RunId, resume.LatestRun.RunId);
                Assert.AreEqual(RunStatus.Completed, resume.LatestRun.Status);
                Assert.IsGreaterThanOrEqualTo(capture.TerminalGsn, resume.LastGsn);
                Assert.IsTrue(resume.IsFullyRecoverable, string.Join(" | ", resume.RecoveryDiagnostics));
                Assert.IsEmpty(resume.RecoveryDiagnostics);
                Assert.IsGreaterThan(0, resume.MessageCount);
                Assert.IsNotNull(resume.LastMessageId);
                var blobIds = resume.BlobIds
                    ?? throw new InvalidDataException("Resume did not return the Blob ID collection.");
                Assert.HasCount(0, blobIds);

                var expectedDefinitionIds = GetDefinitions(capture.Mode)
                    .Select(static definition => definition.AgentRef.AgentId)
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToArray();
                var actualDefinitionIds = resume.NeededDefinitions
                    .Select(static definition => definition.AgentId)
                    .OrderBy(static id => id, StringComparer.Ordinal)
                    .ToArray();
                CollectionAssert.AreEqual(expectedDefinitionIds, actualDefinitionIds);
                if (capture.Mode == RuntimeMode.Meeting)
                {
                    Assert.IsNotNull(resume.MeetingState);
                }
                else if (capture.Mode == RuntimeMode.Work)
                {
                    Assert.IsNotNull(resume.WorkState);
                    Assert.IsNotNull(resume.LastCheckpoint);
                    Assert.IsNotEmpty(resume.LastCheckpoint);
                    var revisions = resume.WorkState.PlanRevisions
                        ?? throw new InvalidDataException("Work Resume did not return plan revisions.");
                    Assert.IsNotEmpty(revisions);
                }

                var blocked = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                    async () => await secondClient.StartExistingSessionRunAsync(
                        new ExistingSessionRunRequest(
                            capture.SessionId,
                            $"resume-{capture.Mode}-before-ready",
                            InputOverride: [new TextContentBlock("blocked")]),
                        secondTimeout.Token));
                StringAssert.Contains(blocked.Message, "session.rehydrate");
                var rehydrated = await secondClient.RehydrateSessionAsync(
                    new SessionRehydrateParameters(
                        capture.SessionId,
                        GetDefinitions(capture.Mode)),
                    secondTimeout.Token);
                Assert.AreEqual("Ready", rehydrated.Status);
                Assert.IsEmpty(rehydrated.Mismatched);
            }

            var continuedGsns = new List<long>();
            foreach (var capture in captures)
            {
                var previousLastGsn = resumeBySession[capture.SessionId].LastGsn;
                var runId = await secondClient.StartExistingSessionRunAsync(
                    new ExistingSessionRunRequest(
                        capture.SessionId,
                        $"resume-{capture.Mode}-continued",
                        InputOverride: [new TextContentBlock("continue")]),
                    secondTimeout.Token);
                var terminal = await ReadTerminalAsync(
                    secondClient,
                    runId,
                    previousLastGsn,
                    secondTimeout.Token);
                Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType, terminal.Payload.GetRawText());
                Assert.IsGreaterThan(previousLastGsn, terminal.Gsn);
                continuedGsns.Add(terminal.Gsn);
                await secondClient.AcknowledgeEventsAsync(terminal.Gsn, secondTimeout.Token);
                Assert.AreEqual(
                    RunStatus.Completed,
                    (await secondClient.QueryRunAsync(runId, secondTimeout.Token)).Status);
            }

            CollectionAssert.AreEqual(
                continuedGsns.OrderBy(static gsn => gsn).ToArray(),
                continuedGsns.ToArray());
            Assert.AreEqual(
                0L,
                await CountPermanentRunningAsync(
                    Path.Combine(workspace, ".madorin", "state.db"),
                    secondTimeout.Token));

            secondShutdown.Cancel();
            await secondServerTask;
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ResumeSession_WithCorruptCanonicalHistoryReturnsExplicitDiagnostic()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            var persisted = await CreatePersistedExpertSessionAsync(
                workspace,
                secret,
                "history-corrupt",
                input: "persist history");
            var historyPath = Directory.EnumerateFiles(
                    Path.Combine(workspace, ".madorin", "messages"),
                    "*.jsonl",
                    SearchOption.AllDirectories)
                .Single();
            var lines = await File.ReadAllLinesAsync(historyPath, TestContext.CancellationToken);
            Assert.IsGreaterThanOrEqualTo(3, lines.Length);
            lines[Math.Clamp(lines.Length / 2, 1, lines.Length - 2)] = "{corrupt-middle-record";
            await File.WriteAllLinesAsync(historyPath, lines, TestContext.CancellationToken);

            var resume = await ResumeAfterRestartAsync(
                workspace,
                secret,
                "history-corrupt-restart",
                persisted.Capture.SessionId);
            Assert.IsFalse(resume.IsFullyRecoverable);
            Assert.IsTrue(resume.RecoveryDiagnostics.Any(diagnostic =>
                diagnostic.StartsWith("CanonicalHistoryCorrupt:", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ResumeSession_WithTamperedBlobReturnsHashMismatchDiagnostic()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        try
        {
            var persisted = await CreatePersistedExpertSessionAsync(
                workspace,
                secret,
                "blob-corrupt",
                new RuntimeLimits(MaxInlineContentBytes: 8),
                "This input is intentionally larger than the inline content threshold.");
            Assert.IsNotEmpty(persisted.BlobIds);
            var blobId = persisted.BlobIds[0];
            var blobPath = Path.Combine(
                workspace,
                ".madorin",
                "blobs",
                blobId.ToLowerInvariant() + ".blob");
            await File.WriteAllBytesAsync(
                blobPath,
                "tampered"u8.ToArray(),
                TestContext.CancellationToken);

            var resume = await ResumeAfterRestartAsync(
                workspace,
                secret,
                "blob-corrupt-restart",
                persisted.Capture.SessionId,
                new RuntimeLimits(MaxInlineContentBytes: 8));
            Assert.IsFalse(resume.IsFullyRecoverable);
            Assert.IsTrue(resume.RecoveryDiagnostics.Any(diagnostic =>
                diagnostic.StartsWith("BlobHashMismatch:", StringComparison.Ordinal)));
        }
        finally
        {
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private async Task<PersistedExpertSession> CreatePersistedExpertSessionAsync(
        string workspace,
        string secret,
        string purpose,
        RuntimeLimits? blobLimits = null,
        string input = "start")
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        var options = CreateOptions(workspace, secret, purpose, blobLimits);
        await using var server = await RuntimeServer.StartAsync(
            options,
            TestContext.CancellationToken);
        var serverTask = server.RunAsync(shutdown.Token);
        await using var client = new RuntimeClient(
            new RuntimeClientOptions(
                server.PipeName,
                Guid.NewGuid().ToString("N"),
                ExpectedRuntimeInstanceId: server.InstanceId,
                HandshakeSecret: secret));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(timeout.Token);
        var capture = await StartAndCompleteAsync(
            client,
            RuntimeMode.Expert,
            purpose,
            timeout.Token,
            input);
        var resume = await client.ResumeSessionAsync(capture.SessionId, timeout.Token);
        var blobIds = resume.BlobIds
            ?? throw new InvalidDataException("Resume did not return the Blob ID collection.");
        shutdown.Cancel();
        await serverTask;
        return new PersistedExpertSession(capture, blobIds);
    }

    private async Task<SessionResumeResult> ResumeAfterRestartAsync(
        string workspace,
        string secret,
        string purpose,
        string sessionId,
        RuntimeLimits? blobLimits = null)
    {
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        var options = CreateOptions(workspace, secret, purpose, blobLimits);
        await using var server = await RuntimeServer.StartAsync(
            options,
            TestContext.CancellationToken);
        var serverTask = server.RunAsync(shutdown.Token);
        await using var client = new RuntimeClient(
            new RuntimeClientOptions(
                server.PipeName,
                Guid.NewGuid().ToString("N"),
                ExpectedRuntimeInstanceId: server.InstanceId,
                HandshakeSecret: secret));
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        await client.ConnectAsync(timeout.Token);
        var resume = await client.ResumeSessionAsync(sessionId, timeout.Token);
        shutdown.Cancel();
        await serverTask;
        return resume;
    }

    private static NewSessionRunRequest CreateRequest(
        RuntimeMode mode,
        string key,
        string input = "start")
    {
        var selection = mode switch
        {
            RuntimeMode.Expert => new NextTurnSelection(
                1,
                mode,
                new DefaultSelection("resume-provider", "resume-model"),
                new ExpertModeOptions(ExpertAgent)),
            RuntimeMode.Meeting => new NextTurnSelection(
                1,
                mode,
                new DefaultSelection("resume-provider", "resume-model"),
                new MeetingModeOptions(
                [
                    new MeetingParticipant("participant-a", MeetingAgentA, "A", JoinOrder: 0),
                    new MeetingParticipant("participant-b", MeetingAgentB, "B", JoinOrder: 1)
                ])),
            RuntimeMode.Work => new NextTurnSelection(
                1,
                mode,
                new DefaultSelection("resume-provider", "resume-model"),
                new WorkModeOptions(WorkManager, [WorkWorker], WorkflowPolicy.Default)),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
        };
        return new NewSessionRunRequest(
            $"resume-{mode}-{key}-session",
            $"resume-{mode}-{key}-run",
            mode,
            selection,
            [new TextContentBlock(input)]);
    }

    private static AgentDefinition[] GetDefinitions(RuntimeMode mode) => mode switch
    {
        RuntimeMode.Expert => [new AgentDefinition(ExpertAgent)],
        RuntimeMode.Meeting =>
        [
            new AgentDefinition(MeetingAgentA),
            new AgentDefinition(MeetingAgentB)
        ],
        RuntimeMode.Work =>
        [
            new AgentDefinition(WorkManager),
            new AgentDefinition(WorkWorker)
        ],
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, null)
    };

    private static async Task<SessionRunCapture> StartAndCompleteAsync(
        RuntimeClient client,
        RuntimeMode mode,
        string key,
        CancellationToken ct,
        string input = "start")
    {
        var runId = await client.StartNewSessionRunAsync(
            CreateRequest(mode, key, input),
            ct);
        var terminal = await ReadTerminalAsync(client, runId, 0, ct);
        Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType, terminal.Payload.GetRawText());
        await client.AcknowledgeEventsAsync(terminal.Gsn, ct);
        var run = await client.QueryRunAsync(runId, ct);
        return new SessionRunCapture(mode, run.SessionId, runId, terminal.Gsn);
    }

    private static async Task<RuntimeEventEnvelope> ReadTerminalAsync(
        RuntimeClient client,
        string runId,
        long cursor,
        CancellationToken ct)
    {
        await foreach (var envelope in client.ReadEventsAsync(cursor, ct))
        {
            if (envelope.RunId == runId
                && envelope.MessageType is MessageTypes.RunCompleted
                    or MessageTypes.RunFailed
                    or MessageTypes.RunCancelled)
            {
                return envelope;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' did not emit a terminal event.");
    }

    private static async Task<long> CountPermanentRunningAsync(
        string databasePath,
        CancellationToken ct)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath};Pooling=False");
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM runs
                 WHERE LOWER(status) IN ('accepted', 'preparing', 'running', 'waitingfortool', 'persisting'))
              + (SELECT COUNT(*) FROM work_steps WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM work_step_attempts WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM work_background_jobs WHERE LOWER(status) = 'running')
              + (SELECT COUNT(*) FROM meeting_invocations WHERE LOWER(status) = 'running');
            """;
        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException("The Running-state count returned no value."));
    }

    private sealed record SessionRunCapture(
        RuntimeMode Mode,
        string SessionId,
        string RunId,
        long TerminalGsn);

    private sealed record PersistedExpertSession(
        SessionRunCapture Capture,
        string[] BlobIds);

    private static RuntimeServerOptions CreateOptions(
        string workspace,
        string secret,
        string pipePurpose,
        RuntimeLimits? blobLimits = null,
        int workPlanVersion = 1)
    {
        var provider = new ResumeProvider(workPlanVersion);
        return new RuntimeServerOptions(workspace)
        {
            InstanceId = Guid.NewGuid().ToString("N"),
            PipePrefix = $"madorin.resume-{pipePurpose}.{Guid.NewGuid():N}",
            HandshakeSecret = secret,
            BlobLimits = blobLimits ?? new RuntimeLimits(),
            MemoryUserHome = Path.Combine(workspace, "home"),
            ProviderResolver = _ => provider
        };
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-session-resume-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class ResumeProvider(int workPlanVersion) : IRuntimeProviderAdapter
    {
        public string ProviderId => "resume-provider";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("resume-model", "Resume Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var text = string.Equals(request.AgentId, "work-manager", StringComparison.Ordinal)
                ? workPlanVersion == 1
                    ? """
                      {"planVersion":"1","goal":"Resume all modes","steps":[{"stepId":"resume-step","goal":"Produce a work result","targetAgentId":"work-worker","dependsOn":[],"depth":0}]}
                      """
                    : """
                      {"planVersion":"2","previousPlanVersion":"1","goal":"Continue all modes","steps":[{"stepId":"resume-step","goal":"Produce the continued work result","targetAgentId":"work-worker","dependsOn":[],"depth":0,"reusesStepId":"resume-step"}]}
                      """
                : $"result:{request.AgentId}";
            yield return new TextDeltaProviderEvent(request.InvocationId, text);
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
