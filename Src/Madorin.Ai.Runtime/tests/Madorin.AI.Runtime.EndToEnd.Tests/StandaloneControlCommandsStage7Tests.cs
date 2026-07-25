using System.CommandLine;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneControlCommandsStage7Tests
{
    private const string RuntimeSecretEnvironmentVariable = "MADORIN_AI_RUNTIME_SECRET";

    public TestContext TestContext { get; set; }

    private static readonly string[] ControlSubcommands =
        ["status", "sessions", "runs", "cancel", "credential"];
    private static readonly string[] RunsOptions = ["--instance", "--session"];
    private static readonly string[] CancelOptions = ["--instance", "--reason"];
    private static readonly string[] CredentialUpdateOptions =
    [
        "--instance",
        "--run",
        "--provider",
        "--profile",
        "--api-key-env",
        "--expires-at"
    ];

    [TestMethod]
    public void ControlCommandTree_ExposesFrozenOptions()
    {
        var root = CliApplication.CreateRootCommand(
            new StringWriter(),
            new StringReader(string.Empty));
        var control = root.Subcommands.Single(static command => command.Name == "ctl");

        CollectionAssert.AreEquivalent(
            ControlSubcommands,
            control.Subcommands.Select(static command => command.Name).ToArray());
        Assert.Contains(
            "--instance",
            FindSubcommand(control, "status").Options.Select(static option => option.Name));
        Assert.Contains(
            "--instance",
            FindSubcommand(control, "sessions").Options.Select(static option => option.Name));

        var runs = FindSubcommand(control, "runs");
        CollectionAssert.IsSubsetOf(
            RunsOptions,
            runs.Options.Select(static option => option.Name).ToArray());

        var cancel = FindSubcommand(control, "cancel");
        CollectionAssert.IsSubsetOf(
            CancelOptions,
            cancel.Options.Select(static option => option.Name).ToArray());

        var credentialUpdate = FindSubcommand(FindSubcommand(control, "credential"), "update");
        CollectionAssert.IsSubsetOf(
            CredentialUpdateOptions,
            credentialUpdate.Options.Select(static option => option.Name).ToArray());
        Assert.DoesNotContain(
            "--api-key-file",
            credentialUpdate.Options.Select(static option => option.Name));
        Assert.DoesNotContain(
            "--api-key",
            credentialUpdate.Options.Select(static option => option.Name));
    }

    [TestMethod]
    public void ControlCommands_MissingOrInvalidRequiredValues_ReturnInvalidArguments()
    {
        var cancelExitCode = CliApplication.Run(
            ["ctl", "cancel"],
            new StringWriter(),
            new StringReader(string.Empty),
            new StringWriter());
        Assert.AreEqual(ExitCodes.InvalidArguments, cancelExitCode);

        var credentialOutput = new StringWriter();
        var credentialExitCode = CliApplication.Run(
            ["ctl", "credential", "update", "--json"],
            credentialOutput,
            new StringReader(string.Empty),
            new StringWriter());
        Assert.AreEqual(ExitCodes.InvalidArguments, credentialExitCode);
        using var document = System.Text.Json.JsonDocument.Parse(credentialOutput.ToString());
        Assert.AreEqual(
            "InvalidArguments",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    public void CtlStatus_WithoutSecret_ReturnsAuthenticationFailureWithoutPlaceholder()
    {
        var workspace = CreateTemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            RuntimeSecretEnvironmentVariable,
            value: null);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CliApplication.Run(
                ["ctl", "status", "--workspace", workspace],
                output,
                new StringReader(string.Empty),
                error);

            Assert.AreEqual(ExitCodes.AuthenticationFailed, exitCode);
            Assert.DoesNotContain("NotImplemented", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("NotImplemented", error.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    public void CtlStatus_WithoutSecret_JsonErrorIsParseableAndRedacted()
    {
        var workspace = CreateTemporaryDirectory();
        using var environment = new EnvironmentVariableScope(
            RuntimeSecretEnvironmentVariable,
            value: null);
        try
        {
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CliApplication.Run(
                ["ctl", "status", "--workspace", workspace, "--json"],
                output,
                new StringReader(string.Empty),
                error);

            Assert.AreEqual(ExitCodes.AuthenticationFailed, exitCode);
            using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.IsFalse(document.RootElement.GetProperty("success").GetBoolean());
            Assert.AreEqual(
                "AuthenticationRequired",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
            Assert.AreEqual(string.Empty, error.ToString());
        }
        finally
        {
            Directory.Delete(workspace, recursive: true);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task CtlStatus_AuthenticatedRuntime_ReturnsVerifiedInstance()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var environment = new EnvironmentVariableScope(
            RuntimeSecretEnvironmentVariable,
            secret);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                CreateServerOptions(workspace, instanceId, secret),
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await WaitForPidFileAsync(workspace, TestContext.CancellationToken);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CliApplication.Run(
                [
                    "ctl", "status",
                    "--workspace", workspace,
                    "--instance", instanceId,
                    "--json"
                ],
                output,
                new StringReader(string.Empty),
                error);

            Assert.AreEqual(ExitCodes.Success, exitCode);
            Assert.AreEqual(string.Empty, error.ToString());
            using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
            Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
            var runtime = document.RootElement
                .GetProperty("data")
                .GetProperty("runtime");
            Assert.AreEqual(
                instanceId,
                runtime.GetProperty("runtimeInstanceId").GetString());
            Assert.AreEqual(Environment.ProcessId, runtime.GetProperty("processId").GetInt32());
            Assert.AreEqual(
                Path.GetFullPath(workspace),
                runtime.GetProperty("workspace").GetString());

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
    public async Task CtlStatus_WrongSecretAndInstance_ReturnStableErrors()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                CreateServerOptions(workspace, instanceId, secret),
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await WaitForPidFileAsync(workspace, TestContext.CancellationToken);

            using (var wrongSecret = new EnvironmentVariableScope(
                RuntimeSecretEnvironmentVariable,
                "wrong-secret"))
            {
                var output = new StringWriter();
                var error = new StringWriter();
                var exitCode = CliApplication.Run(
                    ["ctl", "status", "--workspace", workspace, "--json"],
                    output,
                    new StringReader(string.Empty),
                    error);

                Assert.AreEqual(ExitCodes.AuthenticationFailed, exitCode);
                Assert.AreEqual(string.Empty, error.ToString());
                using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
                Assert.AreEqual(
                    "AuthenticationFailed",
                    document.RootElement.GetProperty("error").GetProperty("code").GetString());
                Assert.DoesNotContain("wrong-secret", output.ToString(), StringComparison.Ordinal);
            }

            using (var correctSecret = new EnvironmentVariableScope(
                RuntimeSecretEnvironmentVariable,
                secret))
            {
                var output = new StringWriter();
                var exitCode = CliApplication.Run(
                    [
                        "ctl", "status",
                        "--workspace", workspace,
                        "--instance", "another-instance",
                        "--json"
                    ],
                    output,
                    new StringReader(string.Empty),
                    new StringWriter());

                Assert.AreEqual(ExitCodes.ConnectionFailed, exitCode);
                using var document = System.Text.Json.JsonDocument.Parse(output.ToString());
                Assert.AreEqual(
                    "RuntimeConnectionFailed",
                    document.RootElement.GetProperty("error").GetProperty("code").GetString());
            }

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
    public async Task CtlSessionsRunsAndCancel_ActiveRun_UsesClientControlChannel()
    {
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new BlockingProviderAdapter();
        using var environment = new EnvironmentVariableScope(
            RuntimeSecretEnvironmentVariable,
            secret);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                CreateServerOptions(workspace, instanceId, secret, provider),
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await WaitForPidFileAsync(workspace, TestContext.CancellationToken);
            await using var ownerClient = CreateClient(server, instanceId, secret);
            using var timeout = CreateTimeout();
            await ownerClient.ConnectAsync(timeout.Token);
            var runId = await ownerClient.StartNewSessionRunAsync(
                CreateRequest("ctl-cli-session", "ctl-cli-run", provider.ProviderId),
                timeout.Token);
            await provider.Started.WaitAsync(timeout.Token);
            var run = await ownerClient.QueryRunAsync(runId, timeout.Token);

            var sessionsOutput = new StringWriter();
            var sessionsExitCode = CliApplication.Run(
                ["ctl", "sessions", "--workspace", workspace, "--json"],
                sessionsOutput,
                new StringReader(string.Empty),
                new StringWriter());
            Assert.AreEqual(ExitCodes.Success, sessionsExitCode);
            using (var document = System.Text.Json.JsonDocument.Parse(sessionsOutput.ToString()))
            {
                Assert.AreEqual(
                    run.SessionId,
                    document.RootElement.GetProperty("data").GetProperty("sessions")[0]
                        .GetProperty("sessionId")
                        .GetString());
            }

            var runsOutput = new StringWriter();
            var runsExitCode = CliApplication.Run(
                [
                    "ctl", "runs",
                    "--workspace", workspace,
                    "--session", run.SessionId,
                    "--json"
                ],
                runsOutput,
                new StringReader(string.Empty),
                new StringWriter());
            Assert.AreEqual(ExitCodes.Success, runsExitCode);
            using (var document = System.Text.Json.JsonDocument.Parse(runsOutput.ToString()))
            {
                Assert.AreEqual(
                    runId,
                    document.RootElement.GetProperty("data").GetProperty("runs")[0]
                        .GetProperty("runId")
                        .GetString());
            }

            var cancelOutput = new StringWriter();
            var cancelExitCode = CliApplication.Run(
                [
                    "ctl", "cancel", runId,
                    "--workspace", workspace,
                    "--reason", "operator request",
                    "--json"
                ],
                cancelOutput,
                new StringReader(string.Empty),
                new StringWriter());
            Assert.AreEqual(ExitCodes.Success, cancelExitCode);
            using (var document = System.Text.Json.JsonDocument.Parse(cancelOutput.ToString()))
            {
                var data = document.RootElement.GetProperty("data");
                Assert.IsTrue(data.GetProperty("cancelAccepted").GetBoolean());
                Assert.IsFalse(data.GetProperty("cancelled").GetBoolean());
            }

            await provider.CancellationObserved.WaitAsync(timeout.Token);
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
    public async Task CtlCredentialUpdate_EnvironmentCredential_ResumesRunWithoutEcho()
    {
        const string credentialEnvironmentVariable = "MADORIN_STAGE7_TEST_API_KEY";
        const string credential = "replacement key with spaces";
        var workspace = CreateTemporaryDirectory();
        var instanceId = Guid.NewGuid().ToString("N");
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new CredentialRefreshProviderAdapter(credential);
        using var runtimeEnvironment = new EnvironmentVariableScope(
            RuntimeSecretEnvironmentVariable,
            secret);
        using var credentialEnvironment = new EnvironmentVariableScope(
            credentialEnvironmentVariable,
            credential);
        using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                CreateServerOptions(workspace, instanceId, secret, provider),
                TestContext.CancellationToken);
            var serverTask = server.RunAsync(shutdown.Token);
            await WaitForPidFileAsync(workspace, TestContext.CancellationToken);
            await using var ownerClient = CreateClient(server, instanceId, secret);
            using var timeout = CreateTimeout();
            await ownerClient.ConnectAsync(timeout.Token);
            var runId = await ownerClient.StartNewSessionRunAsync(
                CreateRequest("credential-cli-session", "credential-cli-run", provider.ProviderId),
                timeout.Token);
            await WaitForRunStatusAsync(
                ownerClient,
                runId,
                RunStatus.WaitingForCredentials,
                timeout.Token);
            var output = new StringWriter();
            var error = new StringWriter();

            var exitCode = CliApplication.Run(
                [
                    "ctl", "credential", "update",
                    "--workspace", workspace,
                    "--run", runId,
                    "--provider", provider.ProviderId,
                    "--profile", "production",
                    "--api-key-env", credentialEnvironmentVariable,
                    "--expires-at", "2026-07-25T12:00:00+08:00",
                    "--json"
                ],
                output,
                new StringReader(string.Empty),
                error);

            Assert.AreEqual(ExitCodes.Success, exitCode);
            Assert.AreEqual(string.Empty, error.ToString());
            Assert.DoesNotContain(credential, output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(
                credentialEnvironmentVariable,
                output.ToString(),
                StringComparison.Ordinal);
            var update = await provider.CredentialUpdated.WaitAsync(timeout.Token);
            Assert.AreEqual(credential, update.Credential);
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

    private static Command FindSubcommand(Command command, string name) =>
        command.Subcommands.Single(candidate => candidate.Name == name);

    private static RuntimeServerOptions CreateServerOptions(
        string workspace,
        string instanceId,
        string secret,
        IRuntimeProviderAdapter? provider = null) =>
        new(workspace)
        {
            InstanceId = instanceId,
            PipePrefix = "madorin.ai.runtime",
            HandshakeSecret = secret,
            MemoryUserHome = Path.Combine(workspace, "home"),
            ProviderResolver = provider is null ? null : _ => provider
        };

    private static RuntimeClient CreateClient(
        RuntimeServer server,
        string instanceId,
        string secret) =>
        new(new RuntimeClientOptions(
            server.PipeName,
            Guid.NewGuid().ToString("N"),
            EventPipeName: server.EventPipeName,
            ExpectedRuntimeInstanceId: instanceId,
            HandshakeSecret: secret,
            ExpectedServerProcessId: Environment.ProcessId,
            EnableBackgroundHeartbeat: false));

    private CancellationTokenSource CreateTimeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        return timeout;
    }

    private static async Task WaitForPidFileAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(workspace, ".madorin", "runtime.pid");
        while (!File.Exists(path))
        {
            await Task.Delay(20, cancellationToken);
        }
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
        string providerId) =>
        new(
            sessionIdempotencyKey,
            runIdempotencyKey,
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection(providerId, "blocking-model"),
                new ExpertModeOptions(new AgentRef("agent-1", "v1", "Stage 7 CLI test."))),
            [new TextContentBlock("start")]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-stage7-ctl",
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

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _originalValue);
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

    private sealed class CredentialRefreshProviderAdapter(
        string expectedCredential) :
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
            if (!string.Equals(_credential, expectedCredential, StringComparison.Ordinal))
            {
                yield return new InvocationFailedProviderEvent(
                    request.InvocationId,
                    new RuntimeError(
                        RuntimeErrorCodes.AuthenticationFailed,
                        "authentication",
                        "The test credential has expired.",
                        IsRetryable: true,
                        ProviderDetails: null,
                        DiagnosticId: "stage7-cli-credential-refresh"));
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
