using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneRunModesStage7Tests
{
    private const string ManagerAgentId = "manager";
    private const string ManagerProviderId = "manager-provider";
    private const string ManagerModelId = "manager-model";
    private const string WorkerAgentId = "worker";
    private const string WorkerProviderId = "worker-provider";
    private const string WorkerModelId = "worker-model";
    private const string WorkPlanJson =
        "{\"planVersion\":\"1\",\"goal\":\"CLI work mode\",\"steps\":["
        + "{\"stepId\":\"cli-step\",\"goal\":\"Produce a deterministic result\","
        + "\"targetAgentId\":\"worker\",\"dependsOn\":[],\"depth\":0}]}";

    private readonly TestContext _testContext;

    public StandaloneRunModesStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task RunForTests_ThreeModes_RouteConfiguredAgentsAndComplete()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            await WriteConfigurationAsync(configDirectory, includeWorker: true);

            var expertManager = CreateProvider(
                ManagerProviderId,
                ManagerModelId,
                request => $"expert:{request.AgentId}");
            var expertWorker = CreateProvider(
                WorkerProviderId,
                WorkerModelId,
                request => $"unexpected:{request.AgentId}");
            var expert = RunMode(
                configDirectory,
                workspace,
                Path.Combine(root, "expert-data"),
                "expert",
                expertManager,
                expertWorker);

            Assert.AreEqual(ExitCodes.Success, expert.ExitCode, expert.Output);
            Assert.Contains("expert:manager", expert.Output, StringComparison.Ordinal);
            AssertAgentRoute(expertManager, ManagerAgentId);
            Assert.IsEmpty(expertWorker.Requests);
            await AssertPersistedCompletedRunAsync(Path.Combine(root, "expert-data"));

            var meetingManager = CreateProvider(
                ManagerProviderId,
                ManagerModelId,
                request => request.AgentId == "meeting.selector"
                    ? "invalid selector response"
                    : $"meeting:{request.AgentId}");
            var meetingWorker = CreateProvider(
                WorkerProviderId,
                WorkerModelId,
                request => $"meeting:{request.AgentId}");
            var meeting = RunMode(
                configDirectory,
                workspace,
                Path.Combine(root, "meeting-data"),
                "meeting",
                meetingManager,
                meetingWorker);

            Assert.AreEqual(ExitCodes.Success, meeting.ExitCode, meeting.Output);
            Assert.Contains("meeting:manager", meeting.Output, StringComparison.Ordinal);
            Assert.Contains("meeting:worker", meeting.Output, StringComparison.Ordinal);
            AssertAgentRoute(meetingManager, ManagerAgentId);
            AssertAgentRoute(meetingWorker, WorkerAgentId);
            Assert.IsNotEmpty(meetingManager.Requests.Where(static request =>
                request.AgentId == "meeting.selector"));
            AssertProviderRoute(meetingManager);
            AssertProviderRoute(meetingWorker);
            await AssertPersistedCompletedRunAsync(Path.Combine(root, "meeting-data"));

            var workManager = CreateProvider(
                ManagerProviderId,
                ManagerModelId,
                _ => WorkPlanJson);
            var workWorker = CreateProvider(
                WorkerProviderId,
                WorkerModelId,
                request => $"work:{request.AgentId}");
            var work = RunMode(
                configDirectory,
                workspace,
                Path.Combine(root, "work-data"),
                "work",
                workManager,
                workWorker);

            Assert.AreEqual(ExitCodes.Success, work.ExitCode, work.Output);
            Assert.Contains("work:worker", work.Output, StringComparison.Ordinal);
            AssertAgentRoute(workManager, ManagerAgentId);
            AssertAgentRoute(workWorker, WorkerAgentId);
            await AssertPersistedCompletedRunAsync(Path.Combine(root, "work-data"));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_WorkWithOneAgent_RejectsBeforeDataDirectoryCreation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "work-data");
            Directory.CreateDirectory(workspace);
            await WriteConfigurationAsync(configDirectory, includeWorker: false);
            var manager = CreateProvider(
                ManagerProviderId,
                ManagerModelId,
                _ => WorkPlanJson);
            var worker = CreateProvider(
                WorkerProviderId,
                WorkerModelId,
                request => $"work:{request.AgentId}");

            var result = RunMode(
                configDirectory,
                workspace,
                dataDirectory,
                "work",
                manager,
                worker);

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.Output);
            Assert.Contains(
                "at least two configured Agents",
                result.Output,
                StringComparison.Ordinal);
            Assert.IsEmpty(manager.Requests);
            Assert.IsEmpty(worker.Requests);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private async Task WriteConfigurationAsync(
        string configDirectory,
        bool includeWorker)
    {
        var loader = new StandaloneConfigLoader(configDirectory);
        var config = new StandaloneConfig
        {
            DefaultProvider = ManagerProviderId,
            DefaultModel = ManagerModelId,
            DefaultAgent = ManagerAgentId,
            Providers =
            [
                CreateProviderEntry(ManagerProviderId, ManagerModelId),
                CreateProviderEntry(WorkerProviderId, WorkerModelId)
            ]
        };
        var manager = new AgentConfig
        {
            Id = ManagerAgentId,
            Name = "Manager",
            SystemPrompt = "Manage the requested work.",
            Provider = ManagerProviderId,
            Model = ManagerModelId
        };

        await loader.SaveAsync(config, _testContext.CancellationToken);
        await loader.SaveAgentAsync(manager, _testContext.CancellationToken);
        if (includeWorker)
        {
            var worker = new AgentConfig
            {
                Id = WorkerAgentId,
                Name = "Worker",
                SystemPrompt = "Complete the assigned step.",
                Provider = WorkerProviderId,
                Model = WorkerModelId
            };
            await loader.SaveAgentAsync(worker, _testContext.CancellationToken);
        }
    }

    private static ProviderEntry CreateProviderEntry(string providerId, string modelId) =>
        new()
        {
            Name = providerId,
            Protocol = "OpenAI",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = $"{providerId}-key",
            Models = [modelId]
        };

    private static DeterministicProvider CreateProvider(
        string providerId,
        string modelId,
        Func<RuntimeProviderRequest, string> responseFactory) =>
        new(providerId, modelId, responseFactory);

    private static CliRunResult RunMode(
        string configDirectory,
        string workspace,
        string dataDirectory,
        string mode,
        DeterministicProvider manager,
        DeterministicProvider worker)
    {
        var output = new StringWriter();
        var exitCode = CliApplication.RunForTests(
            [
                "run",
                "--workspace", workspace,
                "--data-dir", dataDirectory,
                "--mode", mode,
                "--input", $"Run {mode} mode"
            ],
            output,
            new StringReader(string.Empty),
            configDirectory,
            _ => selection => selection.DefaultSelection.ProviderId switch
            {
                ManagerProviderId => manager,
                WorkerProviderId => worker,
                var providerId => throw new InvalidOperationException(
                    $"Unexpected Provider '{providerId}'.")
            });
        return new CliRunResult(exitCode, output.ToString());
    }

    private static void AssertAgentRoute(
        DeterministicProvider provider,
        string agentId)
    {
        var request = Assert.ContainsSingle(provider.Requests.Where(request =>
            request.AgentId == agentId));
        Assert.AreEqual(provider.ProviderId, request.ProviderId);
        Assert.AreEqual(provider.ModelId, request.ModelId);
    }

    private static void AssertProviderRoute(DeterministicProvider provider)
    {
        Assert.IsNotEmpty(provider.Requests);
        Assert.IsTrue(provider.Requests.All(request =>
            request.ProviderId == provider.ProviderId
            && request.ModelId == provider.ModelId));
    }

    private async Task AssertPersistedCompletedRunAsync(string dataDirectory)
    {
        var databasePath = Path.Combine(dataDirectory, "state.db");
        Assert.IsTrue(File.Exists(databasePath));

        var statuses = new List<string>();
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(_testContext.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT status FROM runs ORDER BY created_at, run_id;";
            await using var reader = await command.ExecuteReaderAsync(
                _testContext.CancellationToken);
            while (await reader.ReadAsync(_testContext.CancellationToken))
            {
                statuses.Add(reader.GetString(0));
            }
        }

        Assert.HasCount(1, statuses);
        Assert.AreEqual(RunStatus.Completed.ToString(), statuses[0]);

        var messageFiles = Directory.GetFiles(
            Path.Combine(dataDirectory, "messages"),
            "*.jsonl");
        var messageFile = Assert.ContainsSingle(messageFiles);
        Assert.IsGreaterThan(0, new FileInfo(messageFile).Length);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-run-modes-stage7-tests",
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

    private sealed record CliRunResult(int ExitCode, string Output);

    private sealed class DeterministicProvider(
        string providerId,
        string modelId,
        Func<RuntimeProviderRequest, string> responseFactory) : IRuntimeProviderAdapter
    {
        public string ProviderId => providerId;

        public string ModelId => modelId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(modelId, modelId, ContextWindow: 8192)];

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                responseFactory(request));
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
