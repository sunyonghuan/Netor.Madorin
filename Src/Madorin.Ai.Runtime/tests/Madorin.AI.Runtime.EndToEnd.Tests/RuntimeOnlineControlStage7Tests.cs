using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class RuntimeOnlineControlStage7Tests
{
    public TestContext TestContext { get; set; }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task StatusRunListAndCancel_ActiveRun_RoundTrip()
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
                    PipePrefix = $"madorin.ctl-stage7.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = CreateClient(server, instanceId, secret);
            using var timeout = CreateTimeout();
            await client.ConnectAsync(timeout.Token);

            var initialStatus = await client.GetRuntimeStatusAsync(timeout.Token);
            Assert.AreEqual(instanceId, initialStatus.RuntimeInstanceId);
            Assert.AreEqual(Path.GetFullPath(workspace), initialStatus.Workspace);
            Assert.AreEqual(Environment.ProcessId, initialStatus.ProcessId);
            Assert.AreEqual(ProtocolVersions.Current, initialStatus.ProtocolVersion);
            Assert.AreEqual(0, initialStatus.ActiveRunCount);

            var runId = await client.StartNewSessionRunAsync(
                CreateRequest("ctl-session", "ctl-run"),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);
            var runSnapshot = await client.QueryRunAsync(runId, timeout.Token);

            var activeStatus = await client.GetRuntimeStatusAsync(timeout.Token);
            Assert.AreEqual(1, activeStatus.ActiveRunCount);
            Assert.IsGreaterThanOrEqualTo(1, activeStatus.ConnectedHostCount);

            var allRuns = await client.ListRunsAsync(new RunListParameters(), timeout.Token);
            var activeRun = Assert.ContainsSingle(allRuns.Runs);
            Assert.AreEqual(runId, activeRun.RunId);
            Assert.AreEqual(runSnapshot.SessionId, activeRun.SessionId);
            Assert.IsLessThanOrEqualTo(DateTimeOffset.UtcNow, activeRun.StartedAt);

            var filtered = await client.ListRunsAsync(
                new RunListParameters(runSnapshot.SessionId),
                timeout.Token);
            Assert.AreEqual(runId, Assert.ContainsSingle(filtered.Runs).RunId);
            var excluded = await client.ListRunsAsync(
                new RunListParameters("another-session"),
                timeout.Token);
            Assert.IsEmpty(excluded.Runs);

            Assert.IsTrue(await client.CancelRunAsync(
                runId,
                "stage 7 online control test",
                timeout.Token));
            await provider.CancellationObserved.WaitAsync(timeout.Token);
            var cancelled = await WaitForRunStatusAsync(
                client,
                runId,
                RunStatus.Cancelled,
                timeout.Token);
            Assert.AreEqual(RunStatus.Cancelled, cancelled.Status);
            Assert.IsFalse(await client.CancelRunAsync(runId, timeout.Token));

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            DeleteTemporaryDirectory(workspace);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CredentialUpdate_FromSeparateAuthenticatedClient_ResumesWaitingRun()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new CredentialRefreshProviderAdapter();
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.ctl-credentials-stage7.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                },
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await using var ownerClient = CreateClient(server, instanceId, secret);
            using var timeout = CreateTimeout();
            await ownerClient.ConnectAsync(timeout.Token);
            var runId = await ownerClient.StartNewSessionRunAsync(
                CreateRequest("credential-session", "credential-run", provider.ProviderId),
                timeout.Token);
            await WaitForRunStatusAsync(
                ownerClient,
                runId,
                RunStatus.WaitingForCredentials,
                timeout.Token);

            await using var controlClient = CreateClient(server, instanceId, secret);
            await controlClient.ConnectAsync(timeout.Token);
            Assert.AreNotEqual(
                ownerClient.InstanceBinding.HostInstanceId,
                controlClient.InstanceBinding.HostInstanceId);
            await controlClient.UpdateCredentialsAsync(
                new CredentialsUpdateParameters(
                    runId,
                    provider.ProviderId,
                    "replacement-key",
                    "production",
                    DateTimeOffset.UtcNow.AddHours(1)),
                timeout.Token);

            var update = await provider.CredentialUpdated.WaitAsync(timeout.Token);
            Assert.AreEqual("replacement-key", update.Credential);
            Assert.AreEqual("production", update.ProviderProfileId);
            var completed = await WaitForRunStatusAsync(
                ownerClient,
                runId,
                RunStatus.Completed,
                timeout.Token);
            Assert.AreEqual("authenticated", completed.TerminalText);

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            DeleteTemporaryDirectory(workspace);
        }
    }

    private static RuntimeClient CreateClient(
        RuntimeServer server,
        string instanceId,
        string secret) =>
        new(new RuntimeClientOptions(
            server.PipeName,
            Guid.NewGuid().ToString("N"),
            ExpectedRuntimeInstanceId: instanceId,
            HandshakeSecret: secret));

    private CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    private static async Task<RunQueryResult> WaitForRunStatusAsync(
        RuntimeClient client,
        string runId,
        RunStatus expectedStatus,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = await client.QueryRunAsync(runId, cancellationToken);
            if (current.Status == expectedStatus)
            {
                return current;
            }

            await Task.Delay(20, cancellationToken);
        }
    }

    private static NewSessionRunRequest CreateRequest(
        string sessionIdempotencyKey,
        string runIdempotencyKey,
        string providerId = "blocking") =>
        new(
            sessionIdempotencyKey,
            runIdempotencyKey,
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection(providerId, "blocking-model"),
                new ExpertModeOptions(new AgentRef("agent-1", "v1", "Stage 7 test."))),
            [new TextContentBlock("start")]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-online-control-stage7",
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

    private sealed class BlockingProviderAdapter : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _started = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _cancellationObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "blocking";

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

    private sealed class CredentialRefreshProviderAdapter :
        IRuntimeProviderAdapter,
        IRuntimeProviderCredentialUpdater
    {
        private readonly TaskCompletionSource<CredentialsUpdateParameters> _credentialUpdated =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _credential = "expired-key";

        public string ProviderId => "refreshing";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("blocking-model", "Credential Refresh Model", ContextWindow: 8192)];

        public Task<CredentialsUpdateParameters> CredentialUpdated => _credentialUpdated.Task;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (!string.Equals(_credential, "replacement-key", StringComparison.Ordinal))
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        RuntimeErrorCodes.AuthenticationFailed,
                        "authentication",
                        "The test credential has expired.",
                        IsRetryable: true,
                        ProviderDetails: null,
                        DiagnosticId: "stage7-credential-refresh"));
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "authenticated");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;

        public Task UpdateCredentialsAsync(
            CredentialsUpdateParameters parameters,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            _credential = parameters.Credential;
            _credentialUpdated.TrySetResult(parameters);
            return Task.CompletedTask;
        }
    }
}
