using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class RuntimeControlTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BlockingRun_ReturnsImmediately_EnforcesCapacity_AndCancels()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var pipePrefix = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = pipePrefix,
                    MaxConcurrentRuns = 1,
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(serverCancellation.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var responseStopwatch = Stopwatch.StartNew();
            var runId = await client.StartNewSessionRunAsync(
                CreateRequest("session-1", "run-1"),
                timeout.Token);
            responseStopwatch.Stop();
            Assert.IsLessThan(TimeSpan.FromSeconds(1), responseStopwatch.Elapsed);
            await provider.Started.WaitAsync(timeout.Token);

            var repeatedRunId = await client.StartNewSessionRunAsync(
                CreateRequest("session-1", "run-1"),
                timeout.Token);
            Assert.AreEqual(runId, repeatedRunId);
            var conflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartNewSessionRunAsync(
                    CreateRequest("session-1", "run-1", "different"),
                    timeout.Token));
            StringAssert.Contains(conflict.Message, "different request payload");

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartNewSessionRunAsync(
                    CreateRequest("session-2", "run-2"),
                    timeout.Token));

            var cancelStopwatch = Stopwatch.StartNew();
            Assert.IsTrue(await client.CancelRunAsync(runId, timeout.Token));
            RuntimeEventEnvelope? cancelled = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.RunCancelled)
                {
                    cancelled = envelope;
                    break;
                }
            }

            cancelStopwatch.Stop();
            Assert.IsNotNull(cancelled);
            Assert.AreEqual(instanceId, cancelled.RuntimeInstanceId);
            Assert.IsLessThan(TimeSpan.FromSeconds(1), cancelStopwatch.Elapsed);
            await client.AcknowledgeEventsAsync(cancelled.Gsn, timeout.Token);
            await client.QueryRunAsync(runId, timeout.Token);
            Assert.IsFalse(await client.CancelRunAsync(runId, timeout.Token));

            serverCancellation.Cancel();
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ProviderCapacity_WhenOneProviderIsFull_AllowsAnotherProvider()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var providerA = new BlockingProviderAdapter("provider-a");
        var providerB = new BlockingProviderAdapter("provider-b");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.provider-capacity.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MaxConcurrentRuns = 2,
                    MaxConcurrentRunsPerProvider = 1,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection =>
                        selection.DefaultSelection.ProviderId == providerA.ProviderId
                            ? providerA
                            : providerB
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var runA = await client.StartNewSessionRunAsync(
                CreateRequest("provider-a-session", "provider-a-run", providerId: "provider-a"),
                timeout.Token);
            await providerA.Started.WaitAsync(timeout.Token);

            var providerCapacity = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartNewSessionRunAsync(
                    CreateRequest(
                        "provider-a-session-2",
                        "provider-a-run-2",
                        providerId: "provider-a"),
                    timeout.Token));
            StringAssert.Contains(providerCapacity.Message, "Provider 'provider-a'");

            var runB = await client.StartNewSessionRunAsync(
                CreateRequest("provider-b-session", "provider-b-run", providerId: "provider-b"),
                timeout.Token);
            await providerB.Started.WaitAsync(timeout.Token);

            await client.CancelRunAsync(runA, timeout.Token);
            await client.CancelRunAsync(runB, timeout.Token);
            await providerA.CancellationObserved.WaitAsync(timeout.Token);
            await providerB.CancellationObserved.WaitAsync(timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ConcurrentRuns_CancellingOne_DoesNotCancelTheOther()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var providerA = new BlockingProviderAdapter("provider-a");
        var providerB = new BlockingProviderAdapter("provider-b");
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.run-isolation.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MaxConcurrentRuns = 2,
                    MaxConcurrentRunsPerProvider = 1,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection =>
                        selection.DefaultSelection.ProviderId == providerA.ProviderId
                            ? providerA
                            : providerB
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var runA = await client.StartNewSessionRunAsync(
                CreateRequest("isolation-session-a", "isolation-run-a", providerId: "provider-a"),
                timeout.Token);
            var runB = await client.StartNewSessionRunAsync(
                CreateRequest("isolation-session-b", "isolation-run-b", providerId: "provider-b"),
                timeout.Token);
            await Task.WhenAll(
                providerA.Started.WaitAsync(timeout.Token),
                providerB.Started.WaitAsync(timeout.Token));

            await client.CancelRunAsync(runA, timeout.Token);
            await providerA.CancellationObserved.WaitAsync(timeout.Token);
            var unexpectedCancellation = await Task.WhenAny(
                providerB.CancellationObserved,
                Task.Delay(TimeSpan.FromMilliseconds(100), timeout.Token));
            Assert.AreNotSame(providerB.CancellationObserved, unexpectedCancellation);

            await client.CancelRunAsync(runB, timeout.Token);
            await providerB.CancellationObserved.WaitAsync(timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RunTimeout_BlockingProvider_EmitsRunTimedOutAndPersistsFailedState()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.run-timeout.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    RunTimeout = TimeSpan.FromSeconds(1),
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest("timeout-session", "timeout-run"),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);
            var failedEnvelope = await ReadRunEventAsync(
                client,
                runId,
                MessageTypes.RunFailed,
                timeout.Token);
            var failed = JsonSerializer.Deserialize(
                failedEnvelope.Payload,
                RuntimeJsonContext.Default.RunFailedEvent);

            Assert.IsNotNull(failed);
            Assert.AreEqual(RuntimeErrorCodes.RunTimedOut, failed.Error.Code);
            await provider.CancellationObserved.WaitAsync(timeout.Token);
            var snapshot = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual(RunStatus.Failed, snapshot.Status);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HostLeaseTimeout_CancelsOwnedRunAndDisconnectsSession()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.lease.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HeartbeatIntervalSeconds = 1,
                    HostLeaseTimeout = TimeSpan.FromMilliseconds(300),
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret,
                    EnableBackgroundHeartbeat: false));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);
            await client.StartNewSessionRunAsync(
                CreateRequest("lease-session", "lease-run"),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);

            await provider.CancellationObserved.WaitAsync(timeout.Token);
            await WaitUntilAsync(
                () => server.ConnectedSessionCount == 0,
                TimeSpan.FromSeconds(5),
                timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task HostLeaseTimeout_AfterControlDisconnect_CancelsOwnedRun()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.disconnect-lease.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HeartbeatIntervalSeconds = 1,
                    HostLeaseTimeout = TimeSpan.FromMilliseconds(300),
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    hostInstanceId,
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);
            var runId = await client.StartNewSessionRunAsync(
                CreateRequest("disconnect-lease-session", "disconnect-lease-run"),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);

            await server.DisconnectControlChannelsAsync();
            await WaitUntilAsync(
                () => server.ConnectedSessionCount == 0,
                TimeSpan.FromSeconds(5),
                timeout.Token);

            await provider.CancellationObserved.WaitAsync(timeout.Token);
            await client.ReconnectAsync(timeout.Token);
            RunQueryResult? run = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                run = await client.QueryRunAsync(runId, timeout.Token);
                if (run.Status == RunStatus.Cancelled)
                {
                    break;
                }

                await Task.Delay(20, timeout.Token);
            }

            Assert.IsNotNull(run);
            Assert.AreEqual(RunStatus.Cancelled, run.Status);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CompletedRun_CanQueryPersistedTerminalText_AndUnknownRunFails()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.query.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new CompletingProviderAdapter()
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest("query-session", "query-run"),
                timeout.Token);
            RuntimeEventEnvelope? completed = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.RunCompleted)
                {
                    completed = envelope;
                    break;
                }
            }

            Assert.IsNotNull(completed);
            var result = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual(runId, result.RunId);
            Assert.AreEqual(RunStatus.Completed, result.Status);
            Assert.AreEqual("The answer is 42.", result.TerminalText);

            var unknownRun = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.QueryRunAsync("missing-run", timeout.Token));
            StringAssert.Contains(unknownRun.Message, "was not found");

            await client.AcknowledgeEventsAsync(completed.Gsn, timeout.Token);
            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SessionMetadata_SelectionAndExistingRun_RoundTrip()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.session-metadata.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new CompletingProviderAdapter()
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var firstRunId = await client.StartNewSessionRunAsync(
                CreateRequest("metadata-session", "metadata-run-1"),
                timeout.Token);
            var firstCompleted = await ReadRunEventAsync(
                client,
                firstRunId,
                MessageTypes.RunCompleted,
                timeout.Token);
            await client.AcknowledgeEventsAsync(firstCompleted.Gsn, timeout.Token);
            var firstRun = await client.QueryRunAsync(firstRunId, timeout.Token);

            var session = await client.GetSessionAsync(firstRun.SessionId, timeout.Token);
            Assert.AreEqual(RuntimeMode.Expert, session.Mode);
            Assert.IsNotNull(session.Selection);
            Assert.AreEqual(1, session.Selection.SelectionVersion);
            Assert.AreEqual(
                string.Empty,
                ((ExpertModeOptions)session.Selection.ModeOptions).Agent.SystemPrompt);
            Assert.IsTrue(session.IsFullyRecoverable);
            Assert.IsEmpty(session.RecoveryDiagnostics);

            var resume = await client.ResumeSessionAsync(firstRun.SessionId, timeout.Token);
            Assert.AreEqual(1, resume.LatestSelectionVersion);
            Assert.HasCount(0, resume.NeededDefinitions);
            Assert.AreEqual(firstRunId, resume.LatestRun?.RunId);

            var updatedSelection = CreateSelection(2, "Updated system prompt.");
            var persistedSelection = await client.UpdateSessionSelectionAsync(
                new SessionSelectionUpdateParameters(
                    firstRun.SessionId,
                    ExpectedSelectionVersion: 1,
                    updatedSelection),
                timeout.Token);
            Assert.AreEqual(2, persistedSelection.SelectionVersion);
            Assert.AreEqual(
                string.Empty,
                ((ExpertModeOptions)persistedSelection.ModeOptions).Agent.SystemPrompt);
            var conflict = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.UpdateSessionSelectionAsync(
                    new SessionSelectionUpdateParameters(
                        firstRun.SessionId,
                        ExpectedSelectionVersion: 1,
                        CreateSelection(3, "Conflicting prompt.")),
                    timeout.Token));
            StringAssert.Contains(conflict.Message, "no longer current");

            var secondRunId = await client.StartExistingSessionRunAsync(
                new ExistingSessionRunRequest(
                    firstRun.SessionId,
                    "metadata-run-2",
                    InputOverride: [new TextContentBlock("continue")],
                    ExpectedSelectionVersion: 2),
                timeout.Token);
            RuntimeEventEnvelope? secondTerminal = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == secondRunId
                    && envelope.MessageType is MessageTypes.RunCompleted
                        or MessageTypes.RunFailed
                        or MessageTypes.RunCancelled)
                {
                    secondTerminal = envelope;
                    break;
                }
            }

            Assert.IsNotNull(secondTerminal);
            Assert.AreEqual(
                MessageTypes.RunCompleted,
                secondTerminal.MessageType,
                secondTerminal.Payload.GetRawText());
            await client.AcknowledgeEventsAsync(secondTerminal.Gsn, timeout.Token);
            var secondRun = await client.QueryRunAsync(secondRunId, timeout.Token);
            Assert.AreEqual(firstRun.SessionId, secondRun.SessionId);

            var firstPage = await client.ListSessionMessagesAsync(
                new SessionMessagesListParameters(firstRun.SessionId, PageSize: 2),
                timeout.Token);
            Assert.HasCount(2, firstPage.Messages);
            Assert.IsNotNull(firstPage.NextCursor);
            var secondPage = await client.ListSessionMessagesAsync(
                new SessionMessagesListParameters(
                    firstRun.SessionId,
                    firstPage.NextCursor,
                    PageSize: 2),
                timeout.Token);
            Assert.HasCount(2, secondPage.Messages);
            Assert.IsGreaterThan(
                firstPage.Messages[^1].Sequence,
                secondPage.Messages[0].Sequence);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task SessionRehydrate_AfterRuntimeRestart_RequiresMatchingDefinitionBeforeExistingRun()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        const string systemPrompt = "Persisted system prompt.";

        try
        {
            string sessionId;
            using (var firstShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                       TestContext.CancellationToken))
            {
                var firstInstanceId = Guid.NewGuid().ToString("N");
                await using var firstServer = await RuntimeServer.StartAsync(
                    new RuntimeServerOptions(workspace)
                    {
                        InstanceId = firstInstanceId,
                        PipePrefix = $"madorin.session-rehydrate.first.{Guid.NewGuid():N}",
                        HandshakeSecret = secret,
                        MemoryUserHome = Path.Combine(workspace, "home"),
                        ProviderResolver = _ => new CompletingProviderAdapter()
                    },
                    TestContext.CancellationToken);
                var firstServerTask = firstServer.RunAsync(firstShutdown.Token);
                await using var firstClient = new RuntimeClient(
                    new RuntimeClientOptions(
                        firstServer.PipeName,
                        Guid.NewGuid().ToString("N"),
                        ExpectedRuntimeInstanceId: firstInstanceId,
                        HandshakeSecret: secret));
                using var firstTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    TestContext.CancellationToken);
                firstTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await firstClient.ConnectAsync(firstTimeout.Token);

                var firstRunId = await firstClient.StartNewSessionRunAsync(
                    new NewSessionRunRequest(
                        "rehydrate-session",
                        "rehydrate-run-1",
                        RuntimeMode.Expert,
                        CreateSelection(1, systemPrompt),
                        [new TextContentBlock("start")]),
                    firstTimeout.Token);
                var firstCompleted = await ReadRunEventAsync(
                    firstClient,
                    firstRunId,
                    MessageTypes.RunCompleted,
                    firstTimeout.Token);
                await firstClient.AcknowledgeEventsAsync(
                    firstCompleted.Gsn,
                    firstTimeout.Token);
                sessionId = (await firstClient.QueryRunAsync(
                    firstRunId,
                    firstTimeout.Token)).SessionId;

                firstShutdown.Cancel();
                await firstServerTask;
            }

            using var secondShutdown = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            var secondInstanceId = Guid.NewGuid().ToString("N");
            await using var secondServer = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = secondInstanceId,
                    PipePrefix = $"madorin.session-rehydrate.second.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new CompletingProviderAdapter()
                },
                TestContext.CancellationToken);
            var secondServerTask = secondServer.RunAsync(secondShutdown.Token);
            await using var secondClient = new RuntimeClient(
                new RuntimeClientOptions(
                    secondServer.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: secondInstanceId,
                    HandshakeSecret: secret));
            using var secondTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            secondTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            await secondClient.ConnectAsync(secondTimeout.Token);

            var resume = await secondClient.ResumeSessionAsync(sessionId, secondTimeout.Token);
            Assert.HasCount(1, resume.NeededDefinitions);
            Assert.AreEqual("agent-1", resume.NeededDefinitions[0].AgentId);

            var beforeRehydrate = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await secondClient.StartExistingSessionRunAsync(
                    new ExistingSessionRunRequest(
                        sessionId,
                        "rehydrate-run-2",
                        InputOverride: [new TextContentBlock("blocked")]),
                    secondTimeout.Token));
            StringAssert.Contains(beforeRehydrate.Message, "session.rehydrate");

            var mismatched = await secondClient.RehydrateSessionAsync(
                new SessionRehydrateParameters(
                    sessionId,
                    [new AgentDefinition(new AgentRef("agent-1", "v1", "wrong prompt"))]),
                secondTimeout.Token);
            Assert.AreEqual("Failed", mismatched.Status);
            Assert.HasCount(1, mismatched.Mismatched);
            Assert.AreEqual("agent-1", mismatched.Mismatched[0]);

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await secondClient.StartExistingSessionRunAsync(
                    new ExistingSessionRunRequest(
                        sessionId,
                        "rehydrate-run-2",
                        InputOverride: [new TextContentBlock("still blocked")]),
                    secondTimeout.Token));

            var ready = await secondClient.RehydrateSessionAsync(
                new SessionRehydrateParameters(
                    sessionId,
                    [new AgentDefinition(new AgentRef("agent-1", "v1", systemPrompt))]),
                secondTimeout.Token);
            Assert.AreEqual("Ready", ready.Status);
            Assert.HasCount(0, ready.Mismatched);

            var secondRunId = await secondClient.StartExistingSessionRunAsync(
                new ExistingSessionRunRequest(
                    sessionId,
                    "rehydrate-run-2",
                    InputOverride: [new TextContentBlock("continue")]),
                secondTimeout.Token);
            var secondCompleted = await ReadRunEventAsync(
                secondClient,
                secondRunId,
                MessageTypes.RunCompleted,
                secondTimeout.Token);
            await secondClient.AcknowledgeEventsAsync(
                secondCompleted.Gsn,
                secondTimeout.Token);
            Assert.AreEqual(
                sessionId,
                (await secondClient.QueryRunAsync(secondRunId, secondTimeout.Token)).SessionId);

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
    public async Task ExistingSessionRun_WhenSessionIsActive_RejectsUntilLeaseIsReleased()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.session-capacity.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MaxConcurrentRuns = 2,
                    MaxConcurrentRunsPerProvider = 2,
                    MaxConcurrentRunsPerSession = 1,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var firstRunId = await client.StartNewSessionRunAsync(
                CreateRequest("serial-session", "serial-run-1"),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);
            var firstRun = await client.QueryRunAsync(firstRunId, timeout.Token);
            var blocked = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.StartExistingSessionRunAsync(
                    new ExistingSessionRunRequest(
                        firstRun.SessionId,
                        "serial-run-2",
                        InputOverride: [new TextContentBlock("blocked")]),
                    timeout.Token));
            StringAssert.Contains(blocked.Message, "Session");

            await client.CancelRunAsync(firstRunId, timeout.Token);
            var cancelled = await ReadRunEventAsync(
                client,
                firstRunId,
                MessageTypes.RunCancelled,
                timeout.Token);
            await client.AcknowledgeEventsAsync(cancelled.Gsn, timeout.Token);

            string? secondRunId = null;
            var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(3);
            while (secondRunId is null && DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    secondRunId = await client.StartExistingSessionRunAsync(
                        new ExistingSessionRunRequest(
                            firstRun.SessionId,
                            "serial-run-2",
                            InputOverride: [new TextContentBlock("accepted")]),
                        timeout.Token);
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains(
                    "Session",
                    StringComparison.Ordinal))
                {
                    await Task.Delay(20, timeout.Token);
                }
            }

            Assert.IsNotNull(secondRunId);
            await client.CancelRunAsync(secondRunId, timeout.Token);
            var secondCancelled = await ReadRunEventAsync(
                client,
                secondRunId,
                MessageTypes.RunCancelled,
                timeout.Token);
            await client.AcknowledgeEventsAsync(secondCancelled.Gsn, timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ExistingSessionRun_TurnOverride_DoesNotPersistNextTurnSelection()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.turn-override.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection => new CompletingProviderAdapter(
                        selection.DefaultSelection.ProviderId,
                        selection.DefaultSelection.ModelId)
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var firstRunId = await client.StartNewSessionRunAsync(
                CreateRequest(
                    "turn-override-session",
                    "turn-override-run-1",
                    providerId: "persisted-provider"),
                timeout.Token);
            var firstCompleted = await ReadRunEventAsync(
                client,
                firstRunId,
                MessageTypes.RunCompleted,
                timeout.Token);
            await client.AcknowledgeEventsAsync(firstCompleted.Gsn, timeout.Token);
            var firstRun = await client.QueryRunAsync(firstRunId, timeout.Token);
            var sessionId = firstRun.SessionId;

            var initialSession = await client.GetSessionAsync(sessionId, timeout.Token);
            Assert.IsNotNull(initialSession.Selection);
            Assert.AreEqual(1, initialSession.Selection.SelectionVersion);
            Assert.AreEqual(
                "persisted-provider",
                initialSession.Selection.DefaultSelection.ProviderId);

            var turnOverride = new NextTurnSelection(
                99,
                RuntimeMode.Expert,
                new DefaultSelection("override-provider", "override-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Wait until cancelled.")));

            var secondRunId = await client.StartExistingSessionRunAsync(
                new ExistingSessionRunRequest(
                    sessionId,
                    "turn-override-run-2",
                    TurnOverride: turnOverride,
                    InputOverride: [new TextContentBlock("continue")],
                    ExpectedSelectionVersion: 1),
                timeout.Token);
            var secondCompleted = await ReadRunEventAsync(
                client,
                secondRunId,
                MessageTypes.RunCompleted,
                timeout.Token);
            await client.AcknowledgeEventsAsync(secondCompleted.Gsn, timeout.Token);

            var finalSession = await client.GetSessionAsync(sessionId, timeout.Token);
            Assert.IsNotNull(finalSession.Selection);
            Assert.AreEqual(1, finalSession.Selection.SelectionVersion);
            Assert.AreEqual(
                "persisted-provider",
                finalSession.Selection.DefaultSelection.ProviderId);
            Assert.AreEqual(
                "blocking-model",
                finalSession.Selection.DefaultSelection.ModelId);
            Assert.AreNotEqual(99, finalSession.Selection.SelectionVersion);
            Assert.AreNotEqual(
                "override-provider",
                finalSession.Selection.DefaultSelection.ProviderId);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task MeetingRun_ResolvesParticipantProviders_AndContinuesPersistedSelection()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var pipePrefix = $"madorin.meeting-dispatch.{Guid.NewGuid():N}";
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var providerCounts = new ConcurrentDictionary<string, int>();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);

        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = pipePrefix,
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = selection =>
                    {
                        var id = selection.DefaultSelection.ProviderId;
                        providerCounts.AddOrUpdate(id, 1, (_, count) => count + 1);
                        return new CompletingProviderAdapter(
                            id,
                            selection.DefaultSelection.ModelId);
                    }
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var participants = new[]
            {
                new MeetingParticipant(
                    "participant-a",
                    new AgentRef("agent-a", "v1", "Agent A",
                        ProviderId: "participant-provider-a",
                        ModelId: "participant-model-a"),
                    "Agent A",
                    JoinOrder: 0),
                new MeetingParticipant(
                    "participant-b",
                    new AgentRef("agent-b", "v1", "Agent B",
                        ProviderId: "participant-provider-b",
                        ModelId: "participant-model-b"),
                    "Agent B",
                    JoinOrder: 1),
            };

            var meetingOptions = new MeetingModeOptions(participants);

            var firstRunId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "meeting-session",
                    "meeting-run-1",
                    RuntimeMode.Meeting,
                    new NextTurnSelection(
                        1,
                        RuntimeMode.Meeting,
                        new DefaultSelection("meeting-primary", "meeting-model"),
                        meetingOptions),
                    [new TextContentBlock("start")]),
                timeout.Token);

            var firstCompleted = await ReadRunEventAsync(
                client,
                firstRunId,
                MessageTypes.RunCompleted,
                timeout.Token);
            await client.AcknowledgeEventsAsync(firstCompleted.Gsn, timeout.Token);
            var firstRun = await client.QueryRunAsync(firstRunId, timeout.Token);
            var sessionId = firstRun.SessionId;

            Assert.AreEqual(1, providerCounts.GetValueOrDefault("meeting-primary", 0));
            Assert.AreEqual(1, providerCounts.GetValueOrDefault("participant-provider-a", 0));
            Assert.AreEqual(1, providerCounts.GetValueOrDefault("participant-provider-b", 0));

            var secondRunId = await client.StartExistingSessionRunAsync(
                new ExistingSessionRunRequest(
                    sessionId,
                    "meeting-run-2",
                    TurnOverride: null,
                    InputOverride: [new TextContentBlock("continue")],
                    ExpectedSelectionVersion: 1),
                timeout.Token);

            RuntimeEventEnvelope? secondTerminal = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == secondRunId
                    && envelope.MessageType is MessageTypes.RunCompleted
                        or MessageTypes.RunFailed
                        or MessageTypes.RunCancelled)
                {
                    secondTerminal = envelope;
                    break;
                }
            }

            Assert.IsNotNull(secondTerminal);
            Assert.AreEqual(
                MessageTypes.RunCompleted,
                secondTerminal.MessageType,
                secondTerminal.Payload.GetRawText());
            await client.AcknowledgeEventsAsync(secondTerminal.Gsn, timeout.Token);

            Assert.AreEqual(2, providerCounts.GetValueOrDefault("meeting-primary", 0));
            Assert.AreEqual(2, providerCounts.GetValueOrDefault("participant-provider-a", 0));
            Assert.AreEqual(2, providerCounts.GetValueOrDefault("participant-provider-b", 0));

            var resumedSession = await client.ResumeSessionAsync(sessionId, timeout.Token);
            Assert.IsNotNull(resumedSession.MeetingState);
            Assert.AreEqual(4, resumedSession.MeetingState.CurrentRound);
            Assert.IsNull(resumedSession.MeetingState.NextScheduledInvocation);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task WorkRun_CompletesThroughStartRpc()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.work-run.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new CompletingProviderAdapter()
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);

            var manager = new AgentRef("gm-agent", "v1", "General manager system prompt.");
            var worker = new AgentRef("worker-agent", "v1", "Worker system prompt.");
            var workSelection = new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("blocking", "blocking-model"),
                new WorkModeOptions(manager, [worker]));

            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    "work-session",
                    "work-run",
                    RuntimeMode.Work,
                    workSelection,
                    [new TextContentBlock("start")]),
                timeout.Token);
            RuntimeEventEnvelope? completed = null;
            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.RunCompleted)
                {
                    completed = envelope;
                    break;
                }
            }

            Assert.IsNotNull(completed);
            var result = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual(RunStatus.Completed, result.Status);
            var resumed = await client.ResumeSessionAsync(result.SessionId, timeout.Token);
            Assert.IsNotNull(resumed.WorkState);
            Assert.AreEqual(WorkSessionStatus.Completed, resumed.WorkState.Status);
            Assert.HasCount(1, resumed.WorkState.Steps);
            Assert.AreEqual(WorkStepLifecycleStatus.Completed, resumed.WorkState.Steps[0].Status);
            Assert.AreEqual("worker-agent", resumed.WorkState.Steps[0].TargetAgentId);
            var modeChange = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.UpdateSessionSelectionAsync(
                    new SessionSelectionUpdateParameters(
                        result.SessionId,
                        ExpectedSelectionVersion: resumed.LatestSelectionVersion
                            ?? throw new InvalidDataException("The Work session has no selection version."),
                        CreateSelection(2, "Attempt to change Work session to Expert.")),
                    timeout.Token));
            StringAssert.Contains(modeChange.Message, "mode cannot be changed");

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static NewSessionRunRequest CreateRequest(
        string sessionIdempotencyKey,
        string runIdempotencyKey,
        string input = "start",
        string providerId = "blocking") =>
        new(
            sessionIdempotencyKey,
            runIdempotencyKey,
            RuntimeMode.Expert,
            CreateSelection(1, "Wait until cancelled.", providerId),
            [new TextContentBlock(input)]);

    private static NextTurnSelection CreateSelection(
        int version,
        string systemPrompt,
        string providerId = "blocking") =>
        new(
            version,
            RuntimeMode.Expert,
            new DefaultSelection(providerId, "blocking-model"),
            new ExpertModeOptions(
                new AgentRef("agent-1", $"v{version}", systemPrompt)));

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-control-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20, cancellationToken);
        }

        Assert.IsTrue(predicate(), "The expected server state was not reached before timeout.");
    }

    private static async Task<RuntimeEventEnvelope> ReadRunEventAsync(
        RuntimeClient client,
        string runId,
        string messageType,
        CancellationToken cancellationToken)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, cancellationToken))
        {
            if (envelope.RunId == runId
                && string.Equals(envelope.MessageType, messageType, StringComparison.Ordinal))
            {
                return envelope;
            }
        }

        throw new InvalidOperationException(
            $"Run '{runId}' did not emit '{messageType}'.");
    }

    private sealed class BlockingProviderAdapter(string providerId = "blocking")
        : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId { get; } = providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("blocking-model", "Blocking Model", ContextWindow: 8192)];

        public Task Started => _started.Task;

        public Task CancellationObserved => _cancellationObserved.Task;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _cancellationObserved.TrySetResult();
                throw;
            }

            yield break;
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class CompletingProviderAdapter(
        string providerId = "blocking",
        string modelId = "blocking-model") : IRuntimeProviderAdapter
    {
        public string ProviderId => providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(modelId, "Completing Model", ContextWindow: 8192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
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
}
