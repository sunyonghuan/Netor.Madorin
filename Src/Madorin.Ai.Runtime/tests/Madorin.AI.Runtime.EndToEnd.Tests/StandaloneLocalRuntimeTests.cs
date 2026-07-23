using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class StandaloneLocalRuntimeTests
{
    private readonly TestContext _testContext;

    public StandaloneLocalRuntimeTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void CreateContext_NormalizesWorkspaceAndDerivesStableWorkspaceKey()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);

            var first = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            var second = StandaloneRuntimeContext.Create(
                Path.Combine(workspace, "."),
                configDirectory: configDirectory);
            var overridden = StandaloneRuntimeContext.Create(
                workspace,
                Path.Combine(root, "custom-data"),
                configDirectory);

            Assert.AreEqual(first.WorkspaceRoot, second.WorkspaceRoot);
            Assert.AreEqual(first.WorkspaceKey, second.WorkspaceKey);
            Assert.AreEqual(64, first.WorkspaceKey.Length);
            Assert.AreNotEqual(first.RuntimeInstanceId, second.RuntimeInstanceId);
            Assert.AreEqual(
                Path.Combine(configDirectory, "data", "workspaces", first.WorkspaceKey),
                first.DataDirectory);
            Assert.AreEqual(first.WorkspaceKey, overridden.WorkspaceKey);
            Assert.AreEqual(Path.Combine(root, "custom-data"), overridden.DataDirectory);
            Assert.StartsWith(overridden.DataDirectory, overridden.LogDirectory);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void CreateContext_WithoutWorkspace_UsesCurrentDirectory()
    {
        var root = CreateTemporaryDirectory();
        var previousDirectory = Directory.GetCurrentDirectory();
        try
        {
            Directory.SetCurrentDirectory(root);

            var context = StandaloneRuntimeContext.Create(
                configDirectory: Path.Combine(root, "config"));

            Assert.AreEqual(Path.TrimEndingDirectorySeparator(root), context.WorkspaceRoot);
        }
        finally
        {
            Directory.SetCurrentDirectory(previousDirectory);
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task RunAsync_NewThenExistingSession_ReusesLocalRuntimeWithoutIpcArtifacts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var provider = new RecordingProvider();
            await using var runtime = await LocalRuntime.StartAsync(
                CreateOptions(workspace, dataDirectory, provider),
                _testContext.CancellationToken);
            var selection = CreateSelection();

            var firstEvents = await CollectAsync(
                runtime.RunAsync(
                    new NewSessionRunRequest(
                        Guid.NewGuid().ToString("N"),
                        Guid.NewGuid().ToString("N"),
                        RuntimeMode.Expert,
                        selection,
                        [new TextContentBlock("first")]),
                    _testContext.CancellationToken),
                _testContext.CancellationToken);
            var acceptedEnvelope = Assert.ContainsSingle(
                firstEvents.Where(static envelope =>
                    envelope.MessageType == MessageTypes.RunAccepted));
            var accepted = acceptedEnvelope.Payload.Deserialize(
                RuntimeJsonContext.Default.RunAcceptedEvent);
            Assert.IsNotNull(accepted);

            var secondEvents = await CollectAsync(
                runtime.RunAsync(
                    new ExistingSessionRunRequest(
                        accepted.SessionId,
                        Guid.NewGuid().ToString("N"),
                        selection,
                        [new TextContentBlock("second")]),
                    _testContext.CancellationToken),
                _testContext.CancellationToken);

            Assert.IsTrue(firstEvents.Any(
                static envelope => envelope.MessageType == MessageTypes.RunCompleted));
            Assert.IsTrue(secondEvents.Any(
                static envelope => envelope.MessageType == MessageTypes.RunCompleted));
            Assert.HasCount(2, provider.Requests);
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "runtime.pid")));
            Assert.IsFalse(
                Directory.EnumerateFiles(dataDirectory, "*", SearchOption.AllDirectories)
                    .Any(static path =>
                        Path.GetFileName(path).Contains("pipe", StringComparison.OrdinalIgnoreCase)));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task StartAsync_WorkspaceLockIgnoresDataDirectoryButAllowsDifferentWorkspaces()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var provider = new RecordingProvider();
            var firstWorkspace = Path.Combine(root, "workspace-a");
            var secondWorkspace = Path.Combine(root, "workspace-b");
            await using var first = await LocalRuntime.StartAsync(
                CreateOptions(firstWorkspace, Path.Combine(root, "data-a"), provider),
                _testContext.CancellationToken);
            await using var second = await LocalRuntime.StartAsync(
                CreateOptions(secondWorkspace, Path.Combine(root, "data-b"), provider),
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => LocalRuntime.StartAsync(
                    CreateOptions(firstWorkspace, Path.Combine(root, "data-c"), provider),
                    _testContext.CancellationToken));

            Assert.Contains("active Runtime instance", exception.Message, StringComparison.Ordinal);
            Assert.AreNotEqual(first.WorkspaceRoot, second.WorkspaceRoot);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task StartAsync_MemoryServiceForDifferentWorkspace_IsRejectedBeforeDataCreation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            var options = CreateOptions(
                workspace,
                dataDirectory,
                new RecordingProvider()) with
            {
                MemoryFiles = new MemoryFileService(
                    Path.Combine(root, "home"),
                    Path.Combine(root, "other-workspace"))
            };

            var exception = await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => LocalRuntime.StartAsync(options, _testContext.CancellationToken));

            Assert.Contains("Runtime workspace", exception.Message, StringComparison.Ordinal);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(15_000, CooperativeCancellation = true)]
    public async Task StartAsync_HostServerAndLocalRuntimeShareWorkspaceLock()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            var hostData = Path.Combine(root, "host-data");
            var hostOptions = new RuntimeServerOptions(workspace)
            {
                DataDirectory = hostData,
                ConfigDirectory = Path.Combine(hostData, "config"),
                LogDirectory = Path.Combine(hostData, "logs"),
                InstanceId = Guid.NewGuid().ToString("N"),
                HandshakeSecret = Convert.ToBase64String(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),
                ProviderResolver = null
            };
            await using var server = await RuntimeServer.StartAsync(
                hostOptions,
                _testContext.CancellationToken);

            var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => LocalRuntime.StartAsync(
                    CreateOptions(
                        workspace,
                        Path.Combine(root, "local-data"),
                        new RecordingProvider()),
                    _testContext.CancellationToken));

            Assert.Contains("active Runtime instance", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task LocalAndHostRuntimes_UseIdenticalMemoryWithoutHostConfigDependency()
    {
        var root = CreateTemporaryDirectory();
        using var serverCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _testContext.CancellationToken);
        try
        {
            var userHome = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "workspace");
            var memoryFiles = new MemoryFileService(userHome, workspace);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Shared global rule.\n",
                _testContext.CancellationToken);
            await memoryFiles.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Shared project rule.\n",
                _testContext.CancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(userHome, ".madorin", "config.json"),
                "{ this personal provider configuration is intentionally invalid",
                _testContext.CancellationToken);
            var selection = CreateSelection();

            var localProvider = new RecordingProvider();
            await using (var localRuntime = await LocalRuntime.StartAsync(
                CreateOptions(
                    workspace,
                    Path.Combine(root, "local-data"),
                    localProvider) with
                {
                    MemoryFiles = memoryFiles
                },
                _testContext.CancellationToken))
            {
                _ = await CollectAsync(
                    localRuntime.RunAsync(
                        new NewSessionRunRequest(
                            Guid.NewGuid().ToString("N"),
                            Guid.NewGuid().ToString("N"),
                            RuntimeMode.Expert,
                            selection,
                            [new TextContentBlock("local")]),
                        _testContext.CancellationToken),
                    _testContext.CancellationToken);
            }

            var localRequest = Assert.ContainsSingle(localProvider.Requests);
            var localInstructions = GetSystemInstructions(localRequest);

            var hostProvider = new RecordingProvider();
            var hostData = Path.Combine(root, "host-data");
            var instanceId = Guid.NewGuid().ToString("N");
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    DataDirectory = hostData,
                    ConfigDirectory = Path.Combine(hostData, "config"),
                    LogDirectory = Path.Combine(hostData, "logs"),
                    InstanceId = instanceId,
                    PipePrefix = $"madorin.ai.runtime.memory.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MemoryUserHome = userHome,
                    ProviderResolver = _ => hostProvider
                },
                _testContext.CancellationToken);
            var serverTask = server.RunAsync(serverCancellation.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
                _testContext.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await client.ConnectAsync(timeout.Token);
            var runId = await client.StartNewSessionRunAsync(
                new NewSessionRunRequest(
                    Guid.NewGuid().ToString("N"),
                    Guid.NewGuid().ToString("N"),
                    RuntimeMode.Expert,
                    selection,
                    [new TextContentBlock("host")]),
                timeout.Token);

            await foreach (var envelope in client.ReadEventsAsync(0, timeout.Token))
            {
                if (envelope.RunId == runId
                    && envelope.MessageType == MessageTypes.RunCompleted)
                {
                    break;
                }
            }

            var hostRequest = Assert.ContainsSingle(hostProvider.Requests);
            var hostInstructions = GetSystemInstructions(hostRequest);
            Assert.AreEqual(localInstructions, hostInstructions);
            var globalIndex = localInstructions.IndexOf(
                "Shared global rule.",
                StringComparison.Ordinal);
            var projectIndex = localInstructions.IndexOf(
                "Shared project rule.",
                StringComparison.Ordinal);
            Assert.IsGreaterThan(-1, globalIndex);
            Assert.IsGreaterThan(-1, projectIndex);
            Assert.IsLessThan(projectIndex, globalIndex);

            serverCancellation.Cancel();
            await serverTask;
        }
        finally
        {
            serverCancellation.Cancel();
            DeleteDirectory(root);
        }
    }

    private static LocalRuntimeOptions CreateOptions(
        string workspace,
        string dataDirectory,
        IRuntimeProviderAdapter provider) =>
        new(
            workspace,
            dataDirectory,
            Path.Combine(dataDirectory, "logs"),
            Guid.NewGuid().ToString("N"),
            _ => provider);

    private static NextTurnSelection CreateSelection() =>
        new(
            1,
            RuntimeMode.Expert,
            new DefaultSelection("fake", "fake-model"),
            new ExpertModeOptions(
                new AgentRef(
                    "default",
                    "1.0",
                    "Preserve this system prompt exactly.",
                    "fake",
                    "fake-model")));

    private static string GetSystemInstructions(RuntimeProviderRequest request) => string.Join(
        '\n',
        request.Messages
            .Where(static message => message.Role == RuntimeProviderRoles.System)
            .SelectMany(static message => message.Content)
            .OfType<TextContentBlock>()
            .Select(static block => block.Text));

    private static async Task<List<RuntimeEventEnvelope>> CollectAsync(
        IAsyncEnumerable<RuntimeEventEnvelope> events,
        CancellationToken ct)
    {
        var result = new List<RuntimeEventEnvelope>();
        await foreach (var envelope in events.WithCancellation(ct))
        {
            result.Add(envelope);
        }

        return result;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-local-runtime-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingProvider : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => "fake";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("fake-model", "Fake Model", ContextWindow: 8192)];

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var invocation = Interlocked.Increment(ref _invocationCount);
            yield return new TextDeltaProviderEvent(request.InvocationId, $"reply-{invocation}");
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
