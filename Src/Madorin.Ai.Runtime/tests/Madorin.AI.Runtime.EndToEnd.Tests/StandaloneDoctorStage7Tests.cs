using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Persistence.Files;
using Madorin.AI.Runtime.Persistence.Sqlite;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneDoctorStage7Tests
{
    private const string ProviderId = "doctor-provider";
    private const string ModelId = "doctor-model";
    private const string AgentId = "doctor-agent";
    private const string ApiKey = "doctor-secret-api-key";
    private static readonly string?[] ExpectedCheckNames =
    [
        "runtime",
        "personal-config",
        "memory",
        "workspace",
        "data",
        "provider",
        "transport",
        "logs",
        "blob"
    ];

    private readonly TestContext _testContext;

    public StandaloneDoctorStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Doctor_HealthyEnvironment_UsesFixedOrderAndRedactsKey()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var result = RunDoctor(fixture, "doctor", "--json");

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
            Assert.DoesNotContain(ApiKey, result.AllOutput, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(result.Output);
            Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
            var checks = document.RootElement
                .GetProperty("data")
                .GetProperty("checks")
                .EnumerateArray()
                .ToArray();
            CollectionAssert.AreEqual(
                ExpectedCheckNames,
                checks.Select(static check => check.GetProperty("name").GetString()).ToArray());
            Assert.IsTrue(checks.All(static check =>
                check.GetProperty("status").GetString() is not "error"));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DoctorFix_JsonWithoutYes_DoesNotRepairOrCreateRecoveryPoint()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);
            var before = await File.ReadAllBytesAsync(
                fixture.MessagePath,
                _testContext.CancellationToken);

            var result = RunDoctor(fixture, "doctor", "--fix", "--json");

            Assert.AreEqual(ExitCodes.InvalidArguments, result.ExitCode, result.AllOutput);
            using var document = JsonDocument.Parse(result.Output);
            Assert.AreEqual(
                "ConfirmationRequired",
                document.RootElement.GetProperty("error").GetProperty("code").GetString());
            CollectionAssert.AreEqual(
                before,
                await File.ReadAllBytesAsync(
                    fixture.MessagePath,
                    _testContext.CancellationToken));
            Assert.IsFalse(Directory.Exists(Path.Combine(fixture.DataDirectory, "backups")));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task DoctorFix_WithYes_CreatesRecoveryPointAndRepairsDatabase()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            await AppendDamagedTailAsync(fixture.MessagePath);

            var result = RunDoctor(
                fixture,
                "doctor", "--fix", "--yes", "--json");

            Assert.AreEqual(ExitCodes.Success, result.ExitCode, result.AllOutput);
            using var document = JsonDocument.Parse(result.Output);
            Assert.IsTrue(document.RootElement.GetProperty("success").GetBoolean());
            var repair = document.RootElement.GetProperty("data").GetProperty("repair");
            Assert.AreEqual("repaired", repair.GetProperty("status").GetString());
            var backupPath = repair.GetProperty("backupPath").GetString();
            Assert.IsNotNull(backupPath);
            Assert.IsTrue(Directory.Exists(backupPath));
            Assert.HasCount(1, repair.GetProperty("repairedSessionIds").EnumerateArray());

            var check = await DatabaseMaintenanceService.CheckAsync(
                fixture.DataDirectory,
                _testContext.CancellationToken);
            Assert.IsTrue(check.Healthy);
            Assert.DoesNotContain(
                "{\"incomplete\":",
                await File.ReadAllTextAsync(
                    fixture.MessagePath,
                    _testContext.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Doctor_ProviderFailure_DoesNotExposeApiKey()
    {
        var fixture = await CreateFixtureAsync();
        try
        {
            var result = RunDoctor(
                fixture,
                static (_, _) => throw new InvalidOperationException(
                    $"Credential {ApiKey} was rejected."),
                "doctor", "--json");

            Assert.AreEqual(ExitCodes.GeneralError, result.ExitCode, result.AllOutput);
            Assert.DoesNotContain(ApiKey, result.AllOutput, StringComparison.Ordinal);
            Assert.Contains("***", result.Output, StringComparison.Ordinal);
            using var document = JsonDocument.Parse(result.Output);
            Assert.AreEqual(
                "issues",
                document.RootElement.GetProperty("data").GetProperty("status").GetString());
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private async Task<DoctorFixture> CreateFixtureAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "madorin-doctor-stage7-" + Guid.NewGuid().ToString("N"));
        var workspace = Path.Combine(root, "workspace");
        var dataDirectory = Path.Combine(root, "data");
        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(workspace);

        var loader = new StandaloneConfigLoader(configDirectory);
        await loader.SaveAsync(new StandaloneConfig
        {
            DefaultProvider = ProviderId,
            DefaultModel = ModelId,
            DefaultAgent = AgentId,
            Providers =
            [
                new ProviderEntry
                {
                    Name = ProviderId,
                    Protocol = "OpenAI",
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = ApiKey,
                    Models = [ModelId]
                }
            ]
        }, _testContext.CancellationToken);
        await loader.SaveAgentAsync(new AgentConfig
        {
            Id = AgentId,
            Name = "Doctor Agent",
            SystemPrompt = "Run deterministic diagnostics.",
            Provider = ProviderId,
            Model = ModelId
        }, _testContext.CancellationToken);

        var memory = new MemoryFileService(root, workspace);
        await memory.InitAsync(MemoryScope.Global, _testContext.CancellationToken);
        await memory.InitAsync(MemoryScope.Project, _testContext.CancellationToken);

        string sessionId;
        await using (var connection = await DataDirectoryInitializer.InitializeAsync(
            dataDirectory,
            _testContext.CancellationToken))
        {
            var repository = new SqliteSessionRepository(connection);
            sessionId = await repository.CreateSessionAsync(
                RuntimeMode.Expert,
                "doctor-session",
                TimeSpan.FromHours(1),
                _testContext.CancellationToken);
        }

        using var content = JsonDocument.Parse(
            "[{\"type\":\"text\",\"text\":\"doctor check\"}]");
        var store = new ConversationStore(dataDirectory);
        await store.AppendMessageAsync(
            sessionId,
            "Expert",
            new ConversationMessageDraft(
                "doctor-invocation",
                AgentId,
                "user",
                content.RootElement.Clone(),
                DateTimeOffset.UtcNow),
            _testContext.CancellationToken);
        Directory.CreateDirectory(Path.Combine(dataDirectory, "blobs"));

        return new DoctorFixture(
            root,
            workspace,
            dataDirectory,
            configDirectory,
            Path.Combine(dataDirectory, "messages", sessionId + ".jsonl"));
    }

    private static CliResult RunDoctor(DoctorFixture fixture, params string[] args) =>
        RunDoctor(fixture, static (_, _) => new DoctorProvider(), args);

    private static CliResult RunDoctor(
        DoctorFixture fixture,
        Func<StandaloneConfig, string, IRuntimeProviderAdapter> providerAdapterFactory,
        params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        string[] resolvedArgs =
        [
            .. args,
            "--workspace", fixture.Workspace,
            "--data-dir", fixture.DataDirectory
        ];
        var exitCode = CliApplication.RunForTests(
            resolvedArgs,
            output,
            new StringReader(string.Empty),
            fixture.ConfigDirectory,
            static _ => static _ => throw new InvalidOperationException(
                "Run Provider resolution was not expected."),
            memoryUserHome: fixture.Root,
            error: error,
            providerAdapterFactory: providerAdapterFactory);
        return new CliResult(exitCode, output.ToString(), error.ToString());
    }

    private async Task AppendDamagedTailAsync(string messagePath) =>
        await File.AppendAllTextAsync(
            messagePath,
            "{\"incomplete\":",
            Encoding.UTF8,
            _testContext.CancellationToken);

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record DoctorFixture(
        string Root,
        string Workspace,
        string DataDirectory,
        string ConfigDirectory,
        string MessagePath);

    private sealed record CliResult(int ExitCode, string Output, string Error)
    {
        public string AllOutput => Output + Error;
    }

    private sealed class DoctorProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => StandaloneDoctorStage7Tests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(StandaloneDoctorStage7Tests.ModelId, StandaloneDoctorStage7Tests.ModelId)];

        public IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            CancellationToken ct = default) =>
            throw new NotSupportedException("Doctor probes do not execute completions.");

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }
}
