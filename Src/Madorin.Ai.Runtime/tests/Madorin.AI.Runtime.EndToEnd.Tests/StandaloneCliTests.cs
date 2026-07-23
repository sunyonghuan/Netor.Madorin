using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Services.Memory;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneCliTests
{
    private readonly TestContext _testContext;

    public StandaloneCliTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_FirstUseWizardAndRepl_PersistsPromptAndContinuesSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var systemPrompt = "  Keep this prompt exactly, including spaces.  ";
            var input = new StringReader(string.Join(
                Environment.NewLine,
                [
                    "",
                    "fake",
                    "",
                    "secret-key-value",
                    "fake-model",
                    "",
                    systemPrompt,
                    "first turn",
                    "second turn",
                    "/exit",
                    ""
                ]));
            var output = new StringWriter();
            var provider = new RecordingProvider();
            Directory.CreateDirectory(workspace);
            var previousDirectory = Directory.GetCurrentDirectory();
            int exitCode;
            try
            {
                Directory.SetCurrentDirectory(workspace);
                exitCode = CliApplication.RunForTests(
                    [],
                    output,
                    input,
                    configDirectory,
                    _ => _ => provider);
            }
            finally
            {
                Directory.SetCurrentDirectory(previousDirectory);
            }

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.HasCount(2, provider.Requests);
            Assert.Contains("reply-1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("reply-2", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-key-value", output.ToString(), StringComparison.Ordinal);

            var loader = new StandaloneConfigLoader(configDirectory);
            var config = await loader.LoadAsync(_testContext.CancellationToken);
            var agents = await loader.LoadAgentsAsync(_testContext.CancellationToken);
            Assert.IsNotNull(config);
            Assert.AreEqual("fake-model", config.DefaultModel);
            var agent = Assert.ContainsSingle(agents);
            Assert.AreEqual(systemPrompt, agent.SystemPrompt);

            var context = StandaloneRuntimeContext.Create(
                workspace,
                configDirectory: configDirectory);
            Assert.IsFalse(File.Exists(Path.Combine(context.DataDirectory, "runtime.pid")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_SingleRun_UsesDataOverrideAndRedactsProviderFailure()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string apiKey = "secret-that-must-not-leak";
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "custom-data");
            await WriteConfigurationAsync(configDirectory, apiKey);
            var output = new StringWriter();
            var provider = new FailingProvider(apiKey);

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--data-dir", dataDirectory,
                    "--input", "fail safely"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.GeneralError, exitCode);
            Assert.Contains("***", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(apiKey, output.ToString(), StringComparison.Ordinal);
            Assert.IsTrue(Directory.Exists(dataDirectory));
            Assert.IsFalse(File.Exists(Path.Combine(dataDirectory, "runtime.pid")));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_MeetingMode_IsRejectedBeforeRuntimeStarts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var dataDirectory = Path.Combine(root, "data");
            await WriteConfigurationAsync(configDirectory, "test-key");
            var output = new StringWriter();
            var provider = new RecordingProvider();

            var exitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", workspace,
                    "--data-dir", dataDirectory,
                    "--mode", "meeting",
                    "--input", "not implemented"
                ],
                output,
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.InvalidArguments, exitCode);
            Assert.Contains("not implemented", output.ToString(), StringComparison.Ordinal);
            Assert.IsEmpty(provider.Requests);
            Assert.IsFalse(Directory.Exists(dataDirectory));
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_AgentCommands_CompleteInteractiveCrudFlow()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            await WriteConfigurationAsync(configDirectory, "test-key");
            var providerFactory = new Func<
                StandaloneConfig,
                Func<NextTurnSelection, IRuntimeProviderAdapter>>(
                _ => _ => new RecordingProvider());

            var createOutput = new StringWriter();
            var createExitCode = CliApplication.RunForTests(
                ["agent", "create", "reviewer"],
                createOutput,
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["Reviewer", "Reviews changes", "  preserve this prompt  ", "fake", "fake-model", "0.2"])),
                configDirectory,
                providerFactory);
            Assert.AreEqual(ExitCodes.Success, createExitCode, createOutput.ToString());

            var editInput = string.Join(
                    Environment.NewLine,
                    ["", "Reviews final changes", "  updated prompt  ", "", "", ""])
                + Environment.NewLine;
            var editOutput = new StringWriter();
            var editExitCode = CliApplication.RunForTests(
                ["agent", "edit", "reviewer"],
                editOutput,
                new StringReader(editInput),
                configDirectory,
                providerFactory);
            Assert.AreEqual(ExitCodes.Success, editExitCode, editOutput.ToString());

            var listOutput = new StringWriter();
            var listExitCode = CliApplication.RunForTests(
                ["agent", "list"],
                listOutput,
                new StringReader(string.Empty),
                configDirectory,
                providerFactory);
            Assert.AreEqual(ExitCodes.Success, listExitCode, listOutput.ToString());
            Assert.Contains("reviewer", listOutput.ToString(), StringComparison.Ordinal);

            var deleteOutput = new StringWriter();
            var deleteExitCode = CliApplication.RunForTests(
                ["agent", "delete", "default", "--replacement", "reviewer", "--yes"],
                deleteOutput,
                new StringReader(string.Empty),
                configDirectory,
                providerFactory);
            Assert.AreEqual(ExitCodes.Success, deleteExitCode, deleteOutput.ToString());

            var loader = new StandaloneConfigLoader(configDirectory);
            var config = await loader.LoadAsync(_testContext.CancellationToken);
            var agents = await loader.LoadAgentsAsync(_testContext.CancellationToken);
            Assert.IsNotNull(config);
            Assert.AreEqual("reviewer", config.DefaultAgent);
            var reviewer = Assert.ContainsSingle(agents);
            Assert.AreEqual("reviewer", reviewer.Id);
            Assert.AreEqual("Reviews final changes", reviewer.Description);
            Assert.AreEqual("  updated prompt  ", reviewer.SystemPrompt);
            Assert.AreEqual(0.2f, reviewer.Temperature);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_Selection_UsesCommandThenAgentThenConfigPriority()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var loader = new StandaloneConfigLoader(configDirectory);
            await loader.SaveAsync(
                new StandaloneConfig
                {
                    DefaultProvider = "config-provider",
                    DefaultModel = "config-model",
                    DefaultAgent = "default",
                    Providers =
                    [
                        CreateProvider("config-provider", "config-model"),
                        CreateProvider("agent-provider", "agent-model"),
                        CreateProvider("command-provider", "command-model")
                    ]
                },
                _testContext.CancellationToken);
            await loader.SaveAgentAsync(
                new AgentConfig
                {
                    Id = "default",
                    Name = "Default",
                    SystemPrompt = "Selection test prompt",
                    Provider = "agent-provider",
                    Model = "agent-model"
                },
                _testContext.CancellationToken);
            var selections = new List<NextTurnSelection>();
            Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>> factory =
                _ => selection =>
                {
                    selections.Add(selection);
                    return new RecordingProvider(
                        selection.DefaultSelection.ProviderId,
                        selection.DefaultSelection.ModelId);
                };

            var agentOutput = new StringWriter();
            var agentExitCode = CliApplication.RunForTests(
                ["run", "--workspace", Path.Combine(root, "workspace"), "--input", "agent"],
                agentOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory);
            var commandOutput = new StringWriter();
            var commandExitCode = CliApplication.RunForTests(
                [
                    "run",
                    "--workspace", Path.Combine(root, "workspace"),
                    "--provider", "command-provider",
                    "--model", "command-model",
                    "--input", "command"
                ],
                commandOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory);

            Assert.AreEqual(ExitCodes.Success, agentExitCode, agentOutput.ToString());
            Assert.AreEqual(ExitCodes.Success, commandExitCode, commandOutput.ToString());
            Assert.HasCount(2, selections);
            Assert.AreEqual("agent-provider", selections[0].DefaultSelection.ProviderId);
            Assert.AreEqual("agent-model", selections[0].DefaultSelection.ModelId);
            Assert.AreEqual("command-provider", selections[1].DefaultSelection.ProviderId);
            Assert.AreEqual("command-model", selections[1].DefaultSelection.ModelId);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_MemoryCommands_CoverLifecycleCancellationAndFailures()
    {
        var root = CreateTemporaryDirectory();
        var previousMadorinEditor = Environment.GetEnvironmentVariable("MADORIN_EDITOR");
        var previousVisual = Environment.GetEnvironmentVariable("VISUAL");
        var previousEditor = Environment.GetEnvironmentVariable("EDITOR");
        try
        {
            Environment.SetEnvironmentVariable("MADORIN_EDITOR", string.Empty);
            Environment.SetEnvironmentVariable("VISUAL", string.Empty);
            Environment.SetEnvironmentVariable("EDITOR", string.Empty);
            var configDirectory = Path.Combine(root, "config");
            var userHome = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            Func<StandaloneConfig, Func<NextTurnSelection, IRuntimeProviderAdapter>> factory =
                _ => _ => new RecordingProvider();

            var initOutput = new StringWriter();
            var initExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "init", "global"],
                initOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.Success, initExitCode, initOutput.ToString());

            var addOutput = new StringWriter();
            var addExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "add", "global"],
                addOutput,
                new StringReader("Global CLI rule.\n"),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.Success, addExitCode, addOutput.ToString());

            var editOutput = new StringWriter();
            var editExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "edit", "project"],
                editOutput,
                new StringReader("# Memory\n\n- Edited project rule.\n.\n"),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.Success, editExitCode, editOutput.ToString());

            var service = new MemoryFileService(userHome, workspace);
            const string projectContent = "# Memory\n\n- Edited project rule.\n";
            Assert.AreEqual(
                projectContent,
                await service.ReadAsync(MemoryScope.Project, _testContext.CancellationToken));

            var showOutput = new StringWriter();
            var showExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "show", "effective"],
                showOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.Success, showExitCode, showOutput.ToString());
            Assert.Contains("Global CLI rule.", showOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains("Edited project rule.", showOutput.ToString(), StringComparison.Ordinal);

            var cancelledEditOutput = new StringWriter();
            var cancelledEditExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "edit", "project"],
                cancelledEditOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.UserInterrupted, cancelledEditExitCode);
            Assert.AreEqual(
                projectContent,
                await service.ReadAsync(MemoryScope.Project, _testContext.CancellationToken));

            var cancelledClearOutput = new StringWriter();
            var cancelledClearExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "clear", "project"],
                cancelledClearOutput,
                new StringReader("no\n"),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.UserInterrupted, cancelledClearExitCode);

            var nonInteractiveOutput = new StringWriter();
            var nonInteractiveExitCode = CliApplication.RunForTests(
                ["--json", "--workspace", workspace, "memory", "clear", "project"],
                nonInteractiveOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.InvalidArguments, nonInteractiveExitCode);
            Assert.Contains("--yes", nonInteractiveOutput.ToString(), StringComparison.Ordinal);

            var clearOutput = new StringWriter();
            var clearExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "clear", "project", "--yes"],
                clearOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.Success, clearExitCode, clearOutput.ToString());
            Assert.AreEqual(
                "# Memory\n",
                await service.ReadAsync(MemoryScope.Project, _testContext.CancellationToken));

            var invalidOutput = new StringWriter();
            var invalidExitCode = CliApplication.RunForTests(
                ["memory", "show", "invalid"],
                invalidOutput,
                new StringReader(string.Empty),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.InvalidArguments, invalidExitCode);

            var emptyAddOutput = new StringWriter();
            var emptyAddExitCode = CliApplication.RunForTests(
                ["memory", "add", "global"],
                emptyAddOutput,
                new StringReader("\n"),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.InvalidArguments, emptyAddExitCode);

            await File.WriteAllBytesAsync(
                service.ProjectPath!,
                [0xC3, 0x28],
                _testContext.CancellationToken);
            var invalidUtf8Output = new StringWriter();
            var invalidUtf8ExitCode = CliApplication.RunForTests(
                ["--workspace", workspace, "memory", "edit", "project"],
                invalidUtf8Output,
                new StringReader(".\n"),
                configDirectory,
                factory,
                userHome);
            Assert.AreEqual(ExitCodes.GeneralError, invalidUtf8ExitCode);
            Assert.Contains("not valid UTF-8", invalidUtf8Output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MADORIN_EDITOR", previousMadorinEditor);
            Environment.SetEnvironmentVariable("VISUAL", previousVisual);
            Environment.SetEnvironmentVariable("EDITOR", previousEditor);
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task RunForTests_ReplMemoryCommands_UseRuntimeMemoryWithoutProviderRouting()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var userHome = Path.Combine(root, "home");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, "test-key");
            var service = new MemoryFileService(userHome, workspace);
            await service.ReplaceAsync(
                MemoryScope.Global,
                "# Memory\n\n- Global REPL rule.\n",
                _testContext.CancellationToken);
            await service.ReplaceAsync(
                MemoryScope.Project,
                "# Memory\n\n- Project REPL rule.\n",
                _testContext.CancellationToken);
            var provider = new RecordingProvider();
            var output = new StringWriter();
            var input = new StringReader(string.Join(
                Environment.NewLine,
                [
                    "/memory effective",
                    "/remember project",
                    "Remembered from REPL.",
                    "/remember global",
                    string.Empty,
                    "provider turn",
                    "/exit",
                    string.Empty
                ]));

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                input,
                configDirectory,
                _ => _ => provider,
                userHome);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.Contains("Global REPL rule.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Project REPL rule.", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Memory was not added.", output.ToString(), StringComparison.Ordinal);
            Assert.HasCount(1, provider.Requests);
            var projectMemory = await service.ReadAsync(
                MemoryScope.Project,
                _testContext.CancellationToken);
            Assert.IsNotNull(projectMemory);
            Assert.Contains("Remembered from REPL.", projectMemory, StringComparison.Ordinal);
            var request = Assert.ContainsSingle(provider.Requests);
            var systemText = string.Join(
                '\n',
                request.Messages
                    .Where(static message => message.Role == RuntimeProviderRoles.System)
                    .SelectMany(static message => message.Content)
                    .OfType<TextContentBlock>()
                    .Select(static block => block.Text));
            Assert.Contains("Global REPL rule.", systemText, StringComparison.Ordinal);
            Assert.Contains("Remembered from REPL.", systemText, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    public async Task RunForTests_ConfigInit_DoesNotCreateOrModifyMemoryFromCredentials()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            const string secret = "token-that-must-stay-in-config";
            const string memory = "# Memory\n\n- Existing stable preference.\n";
            var configDirectory = Path.Combine(root, ".madorin");
            var service = new MemoryFileService(root);
            await service.ReplaceAsync(
                MemoryScope.Global,
                memory,
                _testContext.CancellationToken);
            var output = new StringWriter();
            var input = new StringReader(string.Join(
                Environment.NewLine,
                ["", "fake", "", secret, "fake-model", "", "Prompt", ""]));

            var exitCode = CliApplication.RunForTests(
                ["config", "init"],
                output,
                input,
                configDirectory,
                _ => _ => new RecordingProvider(),
                root);

            Assert.AreEqual(ExitCodes.Success, exitCode, output.ToString());
            Assert.AreEqual(
                memory,
                await service.ReadAsync(MemoryScope.Global, _testContext.CancellationToken));
            var globalMemory = await service.ReadAsync(
                MemoryScope.Global,
                _testContext.CancellationToken);
            Assert.IsNotNull(globalMemory);
            Assert.DoesNotContain(secret, globalMemory, StringComparison.Ordinal);
            Assert.Contains(
                secret,
                await File.ReadAllTextAsync(
                    Path.Combine(configDirectory, "config.json"),
                    _testContext.CancellationToken),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task MemoryAdd_TwoCliProcesses_DoNotLoseCommittedItems()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var workspace = Path.Combine(root, "workspace");
            Directory.CreateDirectory(workspace);
            using var first = StartMemoryAddProcess(workspace);
            using var second = StartMemoryAddProcess(workspace);
            await first.StandardInput.WriteLineAsync("process-one");
            await second.StandardInput.WriteLineAsync("process-two");
            first.StandardInput.Close();
            second.StandardInput.Close();

            await Task.WhenAll(
                first.WaitForExitAsync(_testContext.CancellationToken),
                second.WaitForExitAsync(_testContext.CancellationToken));
            var firstOutput = await first.StandardOutput.ReadToEndAsync();
            var secondOutput = await second.StandardOutput.ReadToEndAsync();
            var firstError = await first.StandardError.ReadToEndAsync();
            var secondError = await second.StandardError.ReadToEndAsync();

            Assert.AreEqual(ExitCodes.Success, first.ExitCode, $"{firstOutput}\n{firstError}");
            Assert.AreEqual(ExitCodes.Success, second.ExitCode, $"{secondOutput}\n{secondError}");
            var content = await File.ReadAllTextAsync(
                MemoryFileService.ResolveProjectPath(workspace),
                _testContext.CancellationToken);
            Assert.Contains("- process-one\n", content, StringComparison.Ordinal);
            Assert.Contains("- process-two\n", content, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    private async Task WriteConfigurationAsync(string configDirectory, string apiKey)
    {
        var loader = new StandaloneConfigLoader(configDirectory);
        var config = new StandaloneConfig
        {
            DefaultProvider = "fake",
            DefaultModel = "fake-model",
            DefaultAgent = "default",
            Providers =
            [
                new ProviderEntry
                {
                    Name = "fake",
                    Protocol = "OpenAI",
                    BaseUrl = "https://example.invalid/v1",
                    ApiKey = apiKey,
                    Models = ["fake-model"]
                }
            ]
        };
        var agent = new AgentConfig
        {
            Id = "default",
            Name = "Default",
            SystemPrompt = "Test system prompt",
            Provider = "fake",
            Model = "fake-model"
        };

        await loader.SaveAsync(config, _testContext.CancellationToken);
        await loader.SaveAgentAsync(agent, _testContext.CancellationToken);
    }

    private static ProviderEntry CreateProvider(string name, string model) =>
        new()
        {
            Name = name,
            Protocol = "OpenAI",
            BaseUrl = "https://example.invalid/v1",
            ApiKey = $"{name}-key",
            Models = [model]
        };

    private static Process StartMemoryAddProcess(string workspace)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workspace
        };
        startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "madorin.dll"));
        startInfo.ArgumentList.Add("memory");
        startInfo.ArgumentList.Add("add");
        startInfo.ArgumentList.Add("project");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start the madorin CLI process.");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-cli-tests",
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

    private sealed class RecordingProvider(
        string providerId = "fake",
        string modelId = "fake-model") : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => providerId;

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

    private sealed class FailingProvider(string apiKey) : IRuntimeProviderAdapter
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
            if (!string.IsNullOrWhiteSpace(request.InvocationId))
            {
                throw new InvalidOperationException($"Credential '{apiKey}' was rejected.");
            }

            yield break;
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
