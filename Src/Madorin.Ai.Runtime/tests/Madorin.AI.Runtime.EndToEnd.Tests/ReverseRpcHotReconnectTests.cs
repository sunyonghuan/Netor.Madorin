using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Abstractions;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Tools;
using Madorin.AI.Runtime.Tools.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ReverseRpcHotReconnectTests
{
    private const string ToolId = "host.order.create";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ActiveRun_LostToolResponseReconnectsAndQueriesWithoutReexecution()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ReconnectProvider();
        var host = new RecoveringHost(workspace);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.reverse-reconnect.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
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
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishCatalogAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest(),
                timeout.Token);
            await host.ExecutionStarted.WaitAsync(timeout.Token);
            await server.DisconnectControlChannelsAsync();
            host.ReleaseLostResponse();
            await WaitForAsync(
                () => server.ConnectedSessionCount == 0,
                timeout.Token);

            await client.ReconnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            var terminal = await ReadTerminalRunEventAsync(client, runId, timeout.Token);

            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, host.PermissionRequestCount);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(1, host.QueryCount);
            var run = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual("Recovered host result applied.", run.TerminalText);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            host.ReleaseLostResponse();
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
    public async Task ActiveRun_LostPermissionResponseReconnectsWithSameRequestId()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ReconnectProvider();
        var host = new RecoveringPermissionHost(workspace);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.permission-reconnect.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HostLeaseTimeout = TimeSpan.FromSeconds(5),
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
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishCatalogAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest(),
                timeout.Token);
            await host.PermissionReceived.WaitAsync(timeout.Token);
            await server.DisconnectControlChannelsAsync();
            host.ReleaseLostPermissionResponse();
            await WaitForAsync(
                () => server.ConnectedSessionCount == 0,
                timeout.Token);

            await client.ReconnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            var terminal = await ReadTerminalRunEventAsync(client, runId, timeout.Token);

            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.AreEqual(2, provider.CallCount);
            Assert.AreEqual(1, host.UniquePermissionRequestCount);
            Assert.IsGreaterThanOrEqualTo(1, host.PhysicalPermissionRequestCount);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(0, host.QueryCount);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            host.ReleaseLostPermissionResponse();
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
    public async Task WorkRun_PermissionWaitReconnectsAndReplaysWithoutDuplicateBusinessState()
    {
        const string managerGrantId = "grant-manager-work-reconnect";
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new ReconnectWorkProvider();
        var host = new RecoveringWorkPermissionHost(workspace, managerGrantId);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.work-permission-reconnect.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HostLeaseTimeout = TimeSpan.FromSeconds(5),
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
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            await PublishCatalogAsync(client, timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateWorkRequest(),
                timeout.Token);
            await provider.ManagerStarted.WaitAsync(timeout.Token);
            var firstEvent = await ReadFirstRunEventAsync(client, runId, timeout.Token);
            await client.AcknowledgeEventsAsync(firstEvent.Gsn, timeout.Token);
            var replayCursor = firstEvent.Gsn;
            var running = await client.QueryRunAsync(runId, timeout.Token);

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = Path.Join(workspace, ".madorin", "state.db"),
                Pooling = false,
                DefaultTimeout = 5
            }.ToString();
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(timeout.Token);
            using var toolState = new SqliteToolIntentRepository(connection);
            var authorization = new ToolAuthorizationService(
                toolState,
                RuntimeToolPolicy.CreateRestricted(workspace));
            var managerGrantExpiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
            await authorization.SaveGrantAsync(
                CreateManagerGrant(
                    managerGrantId,
                    runId,
                    workspace,
                    managerGrantExpiresAt),
                ct: timeout.Token);

            provider.ReleaseManager();
            var permission = await host.PermissionReceived.WaitAsync(timeout.Token);
            Assert.AreEqual(runId, permission.RunId);
            Assert.AreEqual("worker", permission.AgentId);
            Assert.AreEqual("manager", permission.ParentAgentId);
            Assert.AreEqual("work-order-call", permission.CallId);
            Assert.IsFalse(string.IsNullOrWhiteSpace(permission.WorkStepId));
            Assert.AreEqual("1", permission.PlanVersion);

            var workRepo = new SqliteWorkRepository(connection);
            var waiting = await workRepo.GetResumeStateAsync(running.SessionId, timeout.Token);
            Assert.IsNotNull(waiting);
            Assert.AreEqual(WorkSessionStatus.WaitingForApproval, waiting.Status);
            Assert.AreEqual(permission.ApprovalRequestId, waiting.PendingApprovalRequestId);
            var waitingStep = Assert.ContainsSingle(waiting.Steps);
            Assert.AreEqual(WorkStepLifecycleStatus.WaitingForApproval, waitingStep.Status);
            Assert.AreEqual(permission.WorkStepId, waitingStep.StepId);

            await server.DisconnectControlChannelsAsync();
            host.ReleaseLostPermissionResponse();
            await WaitForAsync(
                () => server.ConnectedSessionCount == 0,
                timeout.Token);

            await client.ReconnectAsync(timeout.Token);
            client.ControlPeer.SetRequestHandler(host.HandleAsync);
            var replayed = await ReadRunEventsUntilTerminalAsync(
                client,
                runId,
                replayCursor,
                timeout.Token);
            var terminal = replayed[^1];
            await client.AcknowledgeEventsAsync(terminal.Gsn, timeout.Token);

            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.IsTrue(replayed.All(envelope => envelope.Gsn > replayCursor));
            Assert.AreEqual(
                replayed.Count,
                replayed.Select(static envelope => envelope.Gsn).Distinct().Count());
            Assert.AreEqual(
                1,
                replayed.Count(static envelope =>
                    envelope.MessageType == MessageTypes.RunCompleted));
            Assert.AreEqual(1, provider.ManagerCallCount);
            Assert.AreEqual(2, provider.WorkerCallCount);
            Assert.AreEqual(1, provider.UniqueWorkerInvocationCount);
            Assert.AreEqual(1, host.UniquePermissionRequestCount);
            Assert.IsGreaterThanOrEqualTo(1, host.PhysicalPermissionRequestCount);
            Assert.AreEqual(1, host.ExecutionCount);
            Assert.AreEqual(0, host.QueryCount);

            var completedRun = await client.QueryRunAsync(runId, timeout.Token);
            Assert.AreEqual(RunStatus.Completed, completedRun.Status);
            var completed = await workRepo.GetResumeStateAsync(
                completedRun.SessionId,
                timeout.Token);
            Assert.IsNotNull(completed);
            Assert.AreEqual(WorkSessionStatus.Completed, completed.Status);
            Assert.IsNull(completed.PendingApprovalRequestId);
            var completedStep = Assert.ContainsSingle(completed.Steps);
            Assert.AreEqual(WorkStepLifecycleStatus.Completed, completedStep.Status);
            Assert.AreEqual(1, completedStep.AttemptCount);

            var intent = await toolState.GetIntentAsync("work-order-call", timeout.Token);
            Assert.IsNotNull(intent);
            Assert.AreEqual(ToolIntentStatus.Succeeded, intent.Status);
            Assert.AreEqual(permission.ApprovalRequestId, intent.ApprovalRequestId);
            Assert.AreEqual(completedStep.StepId, intent.WorkStepId);
            Assert.AreEqual(completedStep.InvocationId, intent.InvocationId);
            await AssertSingleWorkBusinessStateAsync(
                connection,
                runId,
                completedRun.SessionId,
                completedStep.InvocationId!,
                permission,
                timeout.Token);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            provider.ReleaseManager();
            host.ReleaseLostPermissionResponse();
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
    public async Task StartupSentToolRecovery_BlocksNewRunsUntilHostQueryCompletes()
    {
        var workspace = CreateTemporaryDirectory();
        var dataDirectory = Path.Combine(workspace, ".madorin");
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var recoveryHost = new BlockingStartupRecoveryHost();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await SeedStartupSentIntentAsync(dataDirectory, TestContext.CancellationToken);
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.startup-tool-recovery.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => new TextOnlyProvider()
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                TestContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));

            await using var recoveryClient = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret,
                    Capabilities: new RuntimeCapabilities(
                        ReverseRpc: true,
                        ToolCatalog: true,
                        ToolPermissions: true)));
            await recoveryClient.ConnectAsync(timeout.Token);
            recoveryClient.ControlPeer.SetRequestHandler(recoveryHost.HandleAsync);

            await using var runClient = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            await runClient.ConnectAsync(timeout.Token);

            var beforeCatalog = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await runClient.StartNewSessionRunAsync(
                    CreateTextOnlyRequest("before-catalog"),
                    timeout.Token));
            StringAssert.Contains(beforeCatalog.Message, "Startup tool recovery is incomplete");

            var catalogTask = PublishCatalogAsync(recoveryClient, timeout.Token);
            await recoveryHost.QueryStarted.WaitAsync(timeout.Token);
            var whileQuerying = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await runClient.StartNewSessionRunAsync(
                    CreateTextOnlyRequest("while-querying"),
                    timeout.Token));
            StringAssert.Contains(whileQuerying.Message, "Startup tool recovery is incomplete");

            recoveryHost.ReleaseQuery();
            await catalogTask;
            var runId = await runClient.StartNewSessionRunAsync(
                CreateTextOnlyRequest("after-recovery"),
                timeout.Token);
            var terminal = await ReadTerminalRunEventAsync(runClient, runId, timeout.Token);
            Assert.AreEqual(MessageTypes.RunCompleted, terminal.MessageType);
            Assert.AreEqual(1, recoveryHost.QueryCount);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            recoveryHost.ReleaseQuery();
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
    public async Task WorkRun_PureModelStepsContinueOfflineAndReplayBufferedOutbox()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new OfflineWorkProvider();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.offline-work.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HostLeaseTimeout = TimeSpan.FromSeconds(10),
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
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            await client.ConnectAsync(timeout.Token);

            var runId = await client.StartNewSessionRunAsync(
                CreateOfflineWorkRequest(),
                timeout.Token);
            var firstEvent = await ReadFirstRunEventAsync(client, runId, timeout.Token);
            await client.AcknowledgeEventsAsync(firstEvent.Gsn, timeout.Token);
            await provider.ManagerStarted.WaitAsync(timeout.Token);

            await server.DisconnectEventChannelsAsync();
            await server.DisconnectControlChannelsAsync();
            await WaitForAsync(
                () => server.ConnectedSessionCount == 0
                    && server.ConnectedEventChannelCount == 0,
                timeout.Token);
            provider.ReleaseManager();
            await provider.WorkerCompleted.WaitAsync(timeout.Token);
            await WaitForRunStatusAsync(
                Path.Combine(workspace, ".madorin", "state.db"),
                runId,
                RunStatus.Completed,
                timeout.Token);

            await client.ReconnectAsync(timeout.Token);
            var replayed = await ReadRunEventsUntilTerminalAsync(
                client,
                runId,
                firstEvent.Gsn,
                timeout.Token);
            Assert.AreEqual(MessageTypes.RunCompleted, replayed[^1].MessageType);
            Assert.IsTrue(replayed.All(envelope => envelope.Gsn > firstEvent.Gsn));
            Assert.AreEqual(1, provider.ManagerCallCount);
            Assert.AreEqual(1, provider.WorkerCallCount);
            Assert.AreEqual(
                RunStatus.Completed,
                (await client.QueryRunAsync(runId, timeout.Token)).Status);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            provider.ReleaseManager();
            shutdown.Cancel();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }
    }

    private static async Task WaitForRunStatusAsync(
        string databasePath,
        string runId,
        RunStatus expected,
        CancellationToken ct)
    {
        while (true)
        {
            await using var connection = new SqliteConnection($"Data Source={databasePath};Pooling=False");
            await connection.OpenAsync(ct);
            var snapshot = await new SqliteSessionRepository(connection)
                .GetRunSnapshotAsync(runId, ct);
            if (snapshot?.Status == expected)
            {
                return;
            }

            await Task.Delay(25, ct);
        }
    }

    private static async Task SeedStartupSentIntentAsync(
        string dataDirectory,
        CancellationToken ct)
    {
        await using var connection = await DataDirectoryInitializer.InitializeAsync(
            dataDirectory,
            ct);
        var repository = new SqliteSessionRepository(connection);
        var sessionId = await repository.CreateSessionAsync(
            RuntimeMode.Work,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(10),
            ct);
        var runId = await repository.CreateRunAsync(
            sessionId,
            Guid.NewGuid().ToString("N"),
            TimeSpan.FromMinutes(10),
            ct);
        using var toolRepository = new SqliteToolIntentRepository(connection);
        const string argumentsJson = "{\"orderId\":42}";
        var argumentsHash = Convert.ToHexString(
            SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(argumentsJson)));
        Assert.IsTrue(await toolRepository.TryCreateIntentAsync(
            new ToolIntentState(
                "startup-sent-call",
                "startup-invocation",
                runId,
                sessionId,
                "worker",
                ParentAgentId: null,
                ToolId,
                "host-v1",
                argumentsHash,
                ToolIntentStatus.Pending,
                GrantId: null,
                ApprovalRequestId: null,
                ResultJson: null,
                ResultHash: null,
                ResultBlob: null,
                ErrorCode: null,
                ErrorMessage: null,
                IsResultVisible: true,
                DateTimeOffset.UtcNow,
                SentAt: null,
                CompletedAt: null),
            ct));
        Assert.IsTrue(await toolRepository.TryMarkSentAsync(
            "startup-sent-call",
            "startup-grant",
            approvalRequestId: null,
            DateTimeOffset.UtcNow,
            ct));
    }

    private static async Task PublishCatalogAsync(RuntimeClient client, CancellationToken ct)
    {
        var request = new ToolCatalogReplaceRequest(
            "host-v1",
            [
                new ToolCatalogItem(
                    ToolId,
                    "Create order",
                    "Creates one order on the host.",
                    ParseElement("""{"type":"object","additionalProperties":true}"""),
                    ParseElement("""{"type":"object","additionalProperties":true}"""),
                    ToolRiskLevel.Low,
                    10,
                    ["test"],
                    [],
                    ToolExecutionTarget.Host,
                    RequiresApproval: false,
                    IsIdempotent: false)
            ]);
        var response = await client.ControlPeer.SendRequestAsync(
            MessageTypes.ToolCatalogReplace,
            JsonSerializer.SerializeToElement(
                request,
                RuntimeJsonContext.Default.ToolCatalogReplaceRequest),
            TimeSpan.FromSeconds(5),
            ct);
        Assert.IsNull(response.Error);
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            "reconnect-session",
            "reconnect-run",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("reconnect-provider", "reconnect-model"),
                new ExpertModeOptions(
                    new AgentRef("agent-1", "v1", "Create the order."))),
            [new TextContentBlock("Create one order.")]);

    private static NewSessionRunRequest CreateTextOnlyRequest(string key) =>
        new(
            $"startup-recovery-session-{key}",
            $"startup-recovery-run-{key}",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("text-only-provider", "text-only-model"),
                new ExpertModeOptions(
                    new AgentRef("text-agent", "v1", "Answer without tools."))),
            [new TextContentBlock("Return a local answer.")]);

    private static NewSessionRunRequest CreateOfflineWorkRequest()
    {
        var manager = new AgentRef(
            "offline-manager",
            "v1",
            "Create one local step.",
            "offline-work-provider",
            "offline-work-model");
        var worker = new AgentRef(
            "offline-worker",
            "v1",
            "Complete the local step without tools.",
            "offline-work-provider",
            "offline-work-model");
        return new NewSessionRunRequest(
            "offline-work-session",
            "offline-work-run",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("offline-work-provider", "offline-work-model"),
                new WorkModeOptions(manager, [worker], WorkflowPolicy.Default)),
            [new TextContentBlock("Complete the local task while the Host is offline.")]);
    }

    private static NewSessionRunRequest CreateWorkRequest()
    {
        var manager = new AgentRef(
            "manager",
            "v1",
            "Create a one-step plan.",
            "work-reconnect-provider",
            "work-reconnect-model");
        var worker = new AgentRef(
            "worker",
            "v1",
            "Execute the assigned step.",
            "work-reconnect-provider",
            "work-reconnect-model",
            AllowedToolIds: [ToolId]);
        return new NewSessionRunRequest(
            "work-reconnect-session",
            "work-reconnect-run",
            RuntimeMode.Work,
            new NextTurnSelection(
                1,
                RuntimeMode.Work,
                new DefaultSelection("work-reconnect-provider", "work-reconnect-model"),
                new WorkModeOptions(manager, [worker], WorkflowPolicy.Default),
                ToolCatalogVersion: "host-v1"),
            [new TextContentBlock("Create one order.")]);
    }

    private static ToolGrant CreateManagerGrant(
        string grantId,
        string runId,
        string workspace,
        DateTimeOffset expiresAt) =>
        new(
            grantId,
            runId,
            workspace,
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
            AllowedToolIds: [ToolId],
            MaximumRisk: ToolRiskLevel.Low,
            AgentId: "manager",
            RootGrantId: grantId);

    private static async Task<RuntimeEventEnvelope> ReadFirstRunEventAsync(
        RuntimeClient client,
        string runId,
        CancellationToken ct)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
        {
            if (envelope.RunId == runId)
            {
                return envelope;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' did not emit an event.");
    }

    private static async Task<List<RuntimeEventEnvelope>> ReadRunEventsUntilTerminalAsync(
        RuntimeClient client,
        string runId,
        long replayCursor,
        CancellationToken ct)
    {
        var events = new List<RuntimeEventEnvelope>();
        await foreach (var envelope in client.ReadEventsAsync(replayCursor, ct))
        {
            if (envelope.RunId != runId)
            {
                continue;
            }

            events.Add(envelope);
            if (envelope.MessageType is MessageTypes.RunCompleted
                or MessageTypes.RunFailed
                or MessageTypes.RunCancelled)
            {
                return events;
            }
        }

        throw new InvalidOperationException($"Run '{runId}' did not emit a terminal event.");
    }

    private static async Task AssertSingleWorkBusinessStateAsync(
        SqliteConnection connection,
        string runId,
        string sessionId,
        string workerInvocationId,
        ToolPermissionRequest permission,
        CancellationToken ct)
    {
        Assert.AreEqual(
            1L,
            await CountAsync(
                connection,
                "SELECT COUNT(*) FROM work_steps WHERE run_id = $runId;",
                ct,
                ("$runId", runId)));
        Assert.AreEqual(
            1L,
            await CountAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM work_step_attempts
                WHERE invocation_id = $invocationId;
                """,
                ct,
                ("$invocationId", workerInvocationId)));
        Assert.AreEqual(
            1L,
            await CountAsync(
                connection,
                """
                SELECT COUNT(DISTINCT invocation_id)
                FROM work_step_attempts
                WHERE step_id = $stepId AND plan_version = $planVersion;
                """,
                ct,
                ("$stepId", permission.WorkStepId!),
                ("$planVersion", permission.PlanVersion!)));
        Assert.AreEqual(
            1L,
            await CountAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM tool_grants
                WHERE run_id = $runId
                  AND parent_grant_id = $parentGrantId
                  AND approval_request_id = $approvalRequestId;
                """,
                ct,
                ("$approvalRequestId", permission.ApprovalRequestId),
                ("$parentGrantId", "grant-manager-work-reconnect"),
                ("$runId", runId)));
        Assert.AreEqual(
            1L,
            await CountAsync(
                connection,
                """
                SELECT COUNT(*)
                FROM tool_intents
                WHERE call_id = $callId
                  AND run_id = $runId
                  AND session_id = $sessionId
                  AND status = 'succeeded';
                """,
                ct,
                ("$callId", permission.CallId),
                ("$runId", runId),
                ("$sessionId", sessionId)));
        Assert.AreEqual(
            1L,
            await CountOutboxEventsAsync(connection, runId, MessageTypes.RunAccepted, ct));
        Assert.AreEqual(
            2L,
            await CountOutboxEventsAsync(connection, runId, MessageTypes.InvocationStarted, ct));
        Assert.AreEqual(
            2L,
            await CountOutboxEventsAsync(connection, runId, MessageTypes.InvocationCompleted, ct));
        Assert.AreEqual(
            1L,
            await CountOutboxEventsAsync(connection, runId, MessageTypes.RunCompleted, ct));
    }

    private static Task<long> CountOutboxEventsAsync(
        SqliteConnection connection,
        string runId,
        string messageType,
        CancellationToken ct) =>
        CountAsync(
            connection,
            """
            SELECT COUNT(*)
            FROM event_outbox
            WHERE run_id = $runId AND message_type = $messageType;
            """,
            ct,
            ("$runId", runId),
            ("$messageType", messageType));

    private static async Task<long> CountAsync(
        SqliteConnection connection,
        string commandText,
        CancellationToken ct,
        params (string Name, object Value)[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return (long)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidDataException("The count query returned no value."));
    }

    private static async Task<RuntimeEventEnvelope> ReadTerminalRunEventAsync(
        RuntimeClient client,
        string runId,
        CancellationToken ct)
    {
        await foreach (var envelope in client.ReadEventsAsync(0, ct))
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

    private static async Task WaitForAsync(Func<bool> condition, CancellationToken ct)
    {
        while (!condition())
        {
            await Task.Delay(25, ct);
        }
    }

    private static JsonElement ParseElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-reverse-reconnect-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class TextOnlyProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "text-only-provider";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("text-only-model", "Text-only Model", ContextWindow: 4096)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(request.InvocationId, "recovered");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class OfflineWorkProvider : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _managerStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseManager =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _workerCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _managerCallCount;
        private int _workerCallCount;

        public string ProviderId => "offline-work-provider";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("offline-work-model", "Offline Work Model", ContextWindow: 4096)];

        public Task ManagerStarted => _managerStarted.Task;

        public Task WorkerCompleted => _workerCompleted.Task;

        public int ManagerCallCount => Volatile.Read(ref _managerCallCount);

        public int WorkerCallCount => Volatile.Read(ref _workerCallCount);

        public void ReleaseManager() => _releaseManager.TrySetResult();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.Equals(request.AgentId, "offline-manager", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _managerCallCount);
                _managerStarted.TrySetResult();
                await _releaseManager.Task.WaitAsync(ct);
                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    """
                    {"planVersion":"1","goal":"Finish locally","steps":[{"stepId":"offline-step","goal":"Produce the local result","targetAgentId":"offline-worker","dependsOn":[],"depth":0}]}
                    """);
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            Assert.AreEqual("offline-worker", request.AgentId);
            Interlocked.Increment(ref _workerCallCount);
            try
            {
                yield return new TextDeltaProviderEvent(request.InvocationId, "offline-result");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
            }
            finally
            {
                _workerCompleted.TrySetResult();
            }
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class BlockingStartupRecoveryHost
    {
        private readonly TaskCompletionSource _queryStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseQuery =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _queryCount;

        public Task QueryStarted => _queryStarted.Task;

        public int QueryCount => Volatile.Read(ref _queryCount);

        public void ReleaseQuery() => _releaseQuery.TrySetResult();

        public async ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            if (!string.Equals(request.Method, MessageTypes.ToolResultQuery, StringComparison.Ordinal))
            {
                return new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'."));
            }

            var query = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolResultQueryRequest);
            Interlocked.Increment(ref _queryCount);
            _queryStarted.TrySetResult();
            await _releaseQuery.Task.WaitAsync(ct);
            return RecoveringHost.CreateResponse(
                request,
                new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Succeeded,
                    ParseElement("""{"created":true,"orderId":42}""")),
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }
    }

    private sealed class ReconnectProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "reconnect-provider";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("reconnect-model", "Reconnect Model", ContextWindow: 4096)];

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
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "order-call",
                    ToolId,
                    "create",
                    "{\"orderId\":42}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var result = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "order-call");
            Assert.IsTrue(result.Success);
            StringAssert.Contains(result.Content.OfType<TextContentBlock>().Single().Text, "created");
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Recovered host result applied.");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ReconnectWorkProvider : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _managerStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseManager =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _workerInvocationIds =
            new(StringComparer.Ordinal);
        private int _managerCallCount;
        private int _workerCallCount;

        public string ProviderId => "work-reconnect-provider";

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("work-reconnect-model", "Work Reconnect Model", ContextWindow: 4096)];

        public Task ManagerStarted => _managerStarted.Task;

        public int ManagerCallCount => Volatile.Read(ref _managerCallCount);

        public int WorkerCallCount => Volatile.Read(ref _workerCallCount);

        public int UniqueWorkerInvocationCount => _workerInvocationIds.Count;

        public void ReleaseManager() => _releaseManager.TrySetResult();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (string.Equals(request.AgentId, "manager", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _managerCallCount);
                _managerStarted.TrySetResult();
                await _releaseManager.Task.WaitAsync(ct);
                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    "result:manager");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            Assert.AreEqual("worker", request.AgentId);
            _workerInvocationIds.TryAdd(request.InvocationId, 0);
            var workerCall = Interlocked.Increment(ref _workerCallCount);
            if (workerCall == 1)
            {
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "work-order-call",
                    ToolId,
                    "create",
                    "{\"orderId\":42}");
                yield return new InvocationCompletedProviderEvent(
                    request.InvocationId,
                    "tool_calls");
                yield break;
            }

            var result = request.Messages
                .Where(static message => message.Role == RuntimeProviderRoles.Tool)
                .SelectMany(static message => message.Content)
                .OfType<ToolResultContentBlock>()
                .Single(static item => item.CallId == "work-order-call");
            Assert.IsTrue(result.Success);
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "work-order-created");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class RecoveringHost(string workspace)
    {
        private readonly TaskCompletionSource _executionStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLostResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, ToolCallResponse> _results =
            new(StringComparer.Ordinal);
        private readonly string _workspace = workspace;
        private int _executionCount;
        private int _permissionRequestCount;
        private int _queryCount;

        public Task ExecutionStarted => _executionStarted.Task;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PermissionRequestCount => Volatile.Read(ref _permissionRequestCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public void ReleaseLostResponse() => _releaseLostResponse.TrySetResult();

        public ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct) =>
            request.Method switch
            {
                MessageTypes.ToolPermissionRequest => ValueTask.FromResult(
                    HandlePermission(request)),
                MessageTypes.ToolCallRequest => HandleToolCallAsync(request, ct),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(
                    HandleResultQuery(request)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'.")))
            };

        private JsonRpcResponse HandlePermission(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _permissionRequestCount);
            var permission = Deserialize(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            var grantId = $"grant-{permission.CallId}";
            var response = new ToolPermissionResponse(
                permission.CorrelationId ?? permission.ApprovalRequestId,
                permission.CallId,
                ToolAuthorizationDecision.Granted,
                new ToolGrant(
                    grantId,
                    permission.RunId!,
                    _workspace,
                    [_workspace],
                    [_workspace],
                    AllowOverwrite: false,
                    AllowMove: false,
                    AllowDelete: false,
                    AllowedExecutables: [],
                    AllowPowerShell: false,
                    new NetworkPolicy(),
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    AllowDelegation: false,
                    DelegatedAgentIds: [],
                    AllowedToolIds: [permission.ToolId],
                    AllowedCallIds: [permission.CallId],
                    MaximumRisk: ToolRiskLevel.Low,
                    AgentId: permission.AgentId,
                    RootGrantId: grantId));
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private async ValueTask<JsonRpcResponse> HandleToolCallAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            var call = Deserialize(request, RuntimeJsonContext.Default.ToolCallRequest);
            Interlocked.Increment(ref _executionCount);
            var response = new ToolCallResponse(
                call.CorrelationId,
                call.CallId,
                ToolCallStatus.Succeeded,
                ParseElement("""{"created":true,"orderId":42}"""));
            _results[call.CallId] = response;
            _executionStarted.TrySetResult();
            await _releaseLostResponse.Task.WaitAsync(ct);
            return CreateResponse(request, response, RuntimeJsonContext.Default.ToolCallResponse);
        }

        private JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _queryCount);
            var query = Deserialize(request, RuntimeJsonContext.Default.ToolResultQueryRequest);
            var response = _results.TryGetValue(query.CallId, out var result)
                ? new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    result.Status,
                    result.Result,
                    result.ResultHash,
                    result.ResultBlob,
                    result.ErrorCode,
                    result.ErrorMessage)
                : new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Unknown);
            return CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }

        public static T Deserialize<T>(
            JsonRpcRequest request,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            request.Params is { } parameters
                ? JsonSerializer.Deserialize(parameters, typeInfo)
                    ?? throw new InvalidDataException(
                        $"Request '{request.Method}' had an empty payload.")
                : throw new InvalidDataException(
                    $"Request '{request.Method}' did not contain parameters.");

        public static JsonRpcResponse CreateResponse<T>(
            JsonRpcRequest request,
            T response,
            System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo) =>
            new(
                "2.0",
                request.Id,
                JsonSerializer.SerializeToElement(response, typeInfo));
    }

    private sealed class RecoveringPermissionHost(string workspace)
    {
        private readonly TaskCompletionSource _permissionReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLostPermissionResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _permissionRequestIds =
            new(StringComparer.Ordinal);
        private readonly string _workspace = workspace;
        private int _executionCount;
        private int _permissionRequestCount;
        private int _queryCount;

        public Task PermissionReceived => _permissionReceived.Task;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PhysicalPermissionRequestCount =>
            Volatile.Read(ref _permissionRequestCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public int UniquePermissionRequestCount => _permissionRequestIds.Count;

        public void ReleaseLostPermissionResponse() =>
            _releaseLostPermissionResponse.TrySetResult();

        public ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct) =>
            request.Method switch
            {
                MessageTypes.ToolPermissionRequest => HandlePermissionAsync(request, ct),
                MessageTypes.ToolCallRequest => ValueTask.FromResult(HandleToolCall(request)),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(HandleResultQuery(request)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'.")))
            };

        private async ValueTask<JsonRpcResponse> HandlePermissionAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _permissionRequestCount);
            var permission = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            _permissionRequestIds.TryAdd(permission.ApprovalRequestId, 0);
            _permissionReceived.TrySetResult();
            var grantId = $"grant-{permission.CallId}";
            var response = new ToolPermissionResponse(
                permission.CorrelationId ?? permission.ApprovalRequestId,
                permission.CallId,
                ToolAuthorizationDecision.Granted,
                new ToolGrant(
                    grantId,
                    permission.RunId!,
                    _workspace,
                    [_workspace],
                    [_workspace],
                    AllowOverwrite: false,
                    AllowMove: false,
                    AllowDelete: false,
                    AllowedExecutables: [],
                    AllowPowerShell: false,
                    new NetworkPolicy(),
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    AllowDelegation: false,
                    DelegatedAgentIds: [],
                    AllowedToolIds: [permission.ToolId],
                    AllowedCallIds: [permission.CallId],
                    MaximumRisk: ToolRiskLevel.Low,
                    AgentId: permission.AgentId,
                    RootGrantId: grantId));
            if (count == 1)
            {
                await _releaseLostPermissionResponse.Task.WaitAsync(ct);
            }

            return RecoveringHost.CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            var call = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolCallRequest);
            Interlocked.Increment(ref _executionCount);
            var response = new ToolCallResponse(
                call.CorrelationId,
                call.CallId,
                ToolCallStatus.Succeeded,
                ParseElement("""{"created":true,"orderId":42}"""));
            return RecoveringHost.CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolCallResponse);
        }

        private JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _queryCount);
            var query = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolResultQueryRequest);
            return RecoveringHost.CreateResponse(
                request,
                new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Unknown),
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }
    }

    private sealed class RecoveringWorkPermissionHost(
        string workspace,
        string managerGrantId)
    {
        private readonly TaskCompletionSource<ToolPermissionRequest> _permissionReceived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseLostPermissionResponse =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<string, byte> _permissionRequestIds =
            new(StringComparer.Ordinal);
        private readonly string _managerGrantId = managerGrantId;
        private readonly string _workspace = workspace;
        private int _executionCount;
        private int _permissionRequestCount;
        private int _queryCount;

        public Task<ToolPermissionRequest> PermissionReceived => _permissionReceived.Task;

        public int ExecutionCount => Volatile.Read(ref _executionCount);

        public int PhysicalPermissionRequestCount =>
            Volatile.Read(ref _permissionRequestCount);

        public int QueryCount => Volatile.Read(ref _queryCount);

        public int UniquePermissionRequestCount => _permissionRequestIds.Count;

        public void ReleaseLostPermissionResponse() =>
            _releaseLostPermissionResponse.TrySetResult();

        public ValueTask<JsonRpcResponse> HandleAsync(
            JsonRpcRequest request,
            CancellationToken ct) =>
            request.Method switch
            {
                MessageTypes.ToolPermissionRequest => HandlePermissionAsync(request, ct),
                MessageTypes.ToolCallRequest => ValueTask.FromResult(HandleToolCall(request)),
                MessageTypes.ToolResultQuery => ValueTask.FromResult(HandleResultQuery(request)),
                _ => ValueTask.FromResult(new JsonRpcResponse(
                    "2.0",
                    request.Id,
                    Error: new JsonRpcError(-32601, $"Unknown method '{request.Method}'.")))
            };

        private async ValueTask<JsonRpcResponse> HandlePermissionAsync(
            JsonRpcRequest request,
            CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _permissionRequestCount);
            var permission = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolPermissionRequest);
            _permissionRequestIds.TryAdd(permission.ApprovalRequestId, 0);
            _permissionReceived.TrySetResult(permission);
            var response = new ToolPermissionResponse(
                permission.CorrelationId ?? permission.ApprovalRequestId,
                permission.CallId,
                ToolAuthorizationDecision.Granted,
                new ToolGrant(
                    $"grant-worker-{permission.CallId}",
                    permission.RunId!,
                    _workspace,
                    [],
                    [],
                    AllowOverwrite: false,
                    AllowMove: false,
                    AllowDelete: false,
                    AllowedExecutables: [],
                    AllowPowerShell: false,
                    new NetworkPolicy(),
                    DateTimeOffset.UtcNow.AddMinutes(10),
                    AllowDelegation: false,
                    DelegatedAgentIds: [],
                    AllowedToolIds: [permission.ToolId],
                    AllowedCallIds: [permission.CallId],
                    MaximumRisk: ToolRiskLevel.Low,
                    AgentId: permission.AgentId,
                    ParentGrantId: _managerGrantId,
                    RootGrantId: _managerGrantId,
                    DelegationChain: [_managerGrantId],
                    ApprovalRequestId: permission.ApprovalRequestId));
            if (count == 1)
            {
                await _releaseLostPermissionResponse.Task.WaitAsync(ct);
            }

            return RecoveringHost.CreateResponse(
                request,
                response,
                RuntimeJsonContext.Default.ToolPermissionResponse);
        }

        private JsonRpcResponse HandleToolCall(JsonRpcRequest request)
        {
            var call = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolCallRequest);
            Interlocked.Increment(ref _executionCount);
            return RecoveringHost.CreateResponse(
                request,
                new ToolCallResponse(
                    call.CorrelationId,
                    call.CallId,
                    ToolCallStatus.Succeeded,
                    ParseElement("""{"created":true,"orderId":42}""")),
                RuntimeJsonContext.Default.ToolCallResponse);
        }

        private JsonRpcResponse HandleResultQuery(JsonRpcRequest request)
        {
            Interlocked.Increment(ref _queryCount);
            var query = RecoveringHost.Deserialize(
                request,
                RuntimeJsonContext.Default.ToolResultQueryRequest);
            return RecoveringHost.CreateResponse(
                request,
                new ToolResultQueryResponse(
                    query.CorrelationId,
                    query.CallId,
                    ToolCallStatus.Unknown),
                RuntimeJsonContext.Default.ToolResultQueryResponse);
        }
    }
}
