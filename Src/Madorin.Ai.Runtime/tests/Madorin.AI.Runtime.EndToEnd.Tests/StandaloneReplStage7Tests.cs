using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Madorin.AI.Runtime.Cli;
using Madorin.AI.Runtime.Cli.Commands;
using Madorin.AI.Runtime.Cli.Config;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Microsoft.Data.Sqlite;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class StandaloneReplStage7Tests
{
    private readonly TestContext _testContext;

    public StandaloneReplStage7Tests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_CoreCommands_UpdateOnlyNextTurnAndKeepMode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: true);
            var providers = new ProviderSet(
                new RecordingProvider("provider-one", ["model-one"]),
                new RecordingProvider("provider-two", ["model-two", "model-two-alt"]));
            var output = new StringWriter();
            var error = new StringWriter();
            var input = new StringReader(string.Join(
                Environment.NewLine,
                [
                    "/provider provider-two",
                    "/model model-two-alt",
                    "first turn",
                    "/agent agent-two",
                    "second turn",
                    "/mode work",
                    "/mode",
                    "/tools",
                    "/session",
                    "/help",
                    "/exit",
                    string.Empty
                ]));

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                input,
                configDirectory,
                _ => providers.Resolve,
                error: error);

            Assert.AreEqual(ExitCodes.Success, exitCode, error.ToString());
            Assert.IsEmpty(providers.ProviderOne.Requests);
            Assert.HasCount(2, providers.ProviderTwo.Requests);
            var requests = providers.ProviderTwo.Requests.ToArray();
            Assert.AreEqual("model-two-alt", requests[0].ModelId);
            Assert.AreEqual("agent-one", requests[0].AgentId);
            Assert.AreEqual("model-two", requests[1].ModelId);
            Assert.AreEqual("agent-two", requests[1].AgentId);
            Assert.Contains("Mode: Expert", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "The Session mode cannot be changed inside the REPL.",
                error.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "builtin.memory.read [read-only]",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                "builtin.memory.append [approval required]",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains("/resume", output.ToString(), StringComparison.Ordinal);

            var sessions = await ReadSessionsAsync(workspace, configDirectory);
            var session = Assert.ContainsSingle(sessions);
            Assert.AreEqual(2, session.SelectionVersion);
            var runs = await ReadRunsAsync(workspace, configDirectory);
            Assert.HasCount(2, runs);
            Assert.IsTrue(runs.All(run => run.SessionId == session.SessionId));
            Assert.IsTrue(runs.All(run => run.Status == RunStatus.Completed.ToString()));
            await AssertOneTerminalEventPerRunAsync(workspace, configDirectory, runs);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_ModelWithoutArgument_PromptsAndSwitchesNextTurnModel()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: true);
            var providers = new ProviderSet(
                new RecordingProvider("provider-one", ["model-one"]),
                new RecordingProvider("provider-two", ["model-two", "model-two-alt"]));
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                new StringReader(string.Join(
                    Environment.NewLine,
                    [
                        "/provider provider-two",
                        "first turn",
                        "/model",
                        "2",
                        "second turn",
                        "/exit",
                        string.Empty
                    ])),
                configDirectory,
                _ => providers.Resolve,
                error: error);

            Assert.AreEqual(ExitCodes.Success, exitCode, error.ToString());
            Assert.HasCount(2, providers.ProviderTwo.Requests);
            var requests = providers.ProviderTwo.Requests.ToArray();
            Assert.AreEqual("model-two", requests[0].ModelId);
            Assert.AreEqual("model-two-alt", requests[1].ModelId);
            Assert.Contains(
                "Models for Provider 'provider-two':",
                output.ToString(),
                StringComparison.Ordinal);
            Assert.Contains("2. model-two-alt", output.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                "Next turn: provider-two/model-two-alt",
                output.ToString(),
                StringComparison.Ordinal);
            var session = Assert.ContainsSingle(
                await ReadSessionsAsync(workspace, configDirectory));
            Assert.AreEqual(2, session.SelectionVersion);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_NewResumeListAndEof_PreservePersistedSessions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var provider = new RecordingProvider("provider-one", ["model-one"]);
            var firstOutput = new StringWriter();
            var firstExitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                firstOutput,
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["first Session", "/new", "second Session", "/exit", string.Empty])),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.Success, firstExitCode, firstOutput.ToString());
            var initialSessions = await ReadSessionsAsync(workspace, configDirectory);
            Assert.HasCount(2, initialSessions);
            var resumedSessionId = initialSessions[0].SessionId;
            var secondOutput = new StringWriter();
            var secondExitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                secondOutput,
                new StringReader(string.Join(
                    Environment.NewLine,
                    [
                        "/resume",
                        $"/resume {resumedSessionId}",
                        "resumed turn",
                        "/exit",
                        string.Empty
                    ])),
                configDirectory,
                _ => _ => provider);

            Assert.AreEqual(ExitCodes.Success, secondExitCode, secondOutput.ToString());
            Assert.Contains(resumedSessionId, secondOutput.ToString(), StringComparison.Ordinal);
            Assert.Contains(
                $"Resumed Session: {resumedSessionId}",
                secondOutput.ToString(),
                StringComparison.Ordinal);
            var runs = await ReadRunsAsync(workspace, configDirectory);
            Assert.HasCount(3, runs);
            Assert.HasCount(2, runs.Where(run => run.SessionId == resumedSessionId));

            var eofWorkspace = Path.Combine(root, "eof-workspace");
            var eofExitCode = CliApplication.RunForTests(
                ["run", "--workspace", eofWorkspace],
                new StringWriter(),
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => provider);
            Assert.AreEqual(ExitCodes.Success, eofExitCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_OperationalCommands_UseRuntimeServicesAndAtomicExport()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var exportPath = Path.Combine(root, "会话 导出.md");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var provider = new RecordingProvider("provider-one", ["model-one"]);
            var output = new StringWriter();
            var error = new StringWriter();
            var input = new StringReader(string.Join(
                Environment.NewLine,
                [
                    "keep this history entry",
                    "/context",
                    "/compact",
                    "/context",
                    "/history 1",
                    $"/export {exportPath}",
                    "/status",
                    "/clear",
                    "/help compact",
                    "/exit",
                    string.Empty
                ]));

            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                input,
                configDirectory,
                _ => _ => provider,
                error: error);

            Assert.AreEqual(ExitCodes.Success, exitCode, error.ToString());
            Assert.HasCount(2, provider.Requests);
            Assert.IsTrue(File.Exists(exportPath));
            var export = await File.ReadAllTextAsync(exportPath, _testContext.CancellationToken);
            Assert.Contains("keep this history entry", export, StringComparison.Ordinal);
            Assert.Contains("reply-provider-one-1", export, StringComparison.Ordinal);
            Assert.Contains("CanonicalHistory:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("ContextProjection:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Compacted Session:", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("(summary)", output.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("reply-provider-one-1", output.ToString(), StringComparison.Ordinal);
            Assert.Contains($"Exported Session: {exportPath}", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("Active runs: 0", output.ToString(), StringComparison.Ordinal);
            Assert.Contains("/compact - create or refresh", output.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("Unknown REPL command", error.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain("\u001b[", output.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_WorkspaceLock_IsScopedAndReleasedByOwningCli()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            var otherWorkspace = Path.Combine(root, "other-workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var interrupts = new ManualReplInterruptSource();
            var reader = new BlockingTextReader();
            var firstError = new StringWriter();
            var firstTask = Task.Run(() => CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                new StringWriter(),
                reader,
                configDirectory,
                _ => _ => new RecordingProvider("provider-one", ["model-one"]),
                error: firstError,
                replInterruptSource: interrupts));

            await reader.ReadStarted.Task.WaitAsync(_testContext.CancellationToken);
            try
            {
                var competingError = new StringWriter();
                var competingExitCode = CliApplication.RunForTests(
                    ["run", "--workspace", workspace],
                    new StringWriter(),
                    new StringReader(string.Empty),
                    configDirectory,
                    _ => _ => new RecordingProvider("provider-one", ["model-one"]),
                    error: competingError);
                Assert.AreEqual(ExitCodes.WorkspaceError, competingExitCode);
                Assert.Contains("active Runtime instance", competingError.ToString(), StringComparison.Ordinal);
                Assert.Contains("runtimeInstanceId=", competingError.ToString(), StringComparison.Ordinal);

                var independentExitCode = CliApplication.RunForTests(
                    ["run", "--workspace", otherWorkspace],
                    new StringWriter(),
                    new StringReader(string.Empty),
                    configDirectory,
                    _ => _ => new RecordingProvider("provider-one", ["model-one"]));
                Assert.AreEqual(ExitCodes.Success, independentExitCode);
            }
            finally
            {
                interrupts.Interrupt();
            }

            Assert.AreEqual(
                ExitCodes.UserInterrupted,
                await firstTask.WaitAsync(_testContext.CancellationToken),
                firstError.ToString());

            var afterReleaseExitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                new StringWriter(),
                new StringReader(string.Empty),
                configDirectory,
                _ => _ => new RecordingProvider("provider-one", ["model-one"]));
            Assert.AreEqual(ExitCodes.Success, afterReleaseExitCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_FirstInterrupt_CancelsRunAndContinuesSameSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var provider = new InterruptThenCompleteProvider();
            var interrupts = new ManualReplInterruptSource();
            var output = new StringWriter();
            var error = new StringWriter();
            var runTask = Task.Run(() => CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["cancel this", "continue here", "/exit", string.Empty])),
                configDirectory,
                _ => _ => provider,
                error: error,
                replInterruptSource: interrupts));

            await provider.FirstRunStarted.Task.WaitAsync(_testContext.CancellationToken);
            interrupts.Interrupt();
            var exitCode = await runTask.WaitAsync(_testContext.CancellationToken);

            Assert.AreEqual(ExitCodes.Success, exitCode, error.ToString());
            Assert.AreEqual(2, provider.InvocationCount);
            Assert.Contains("Run cancelled.", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("reply-after-cancel", output.ToString(), StringComparison.Ordinal);
            var runs = await ReadRunsAsync(workspace, configDirectory);
            Assert.HasCount(2, runs);
            Assert.AreEqual(RunStatus.Cancelled.ToString(), runs[0].Status);
            Assert.AreEqual(RunStatus.Completed.ToString(), runs[1].Status);
            Assert.AreEqual(runs[0].SessionId, runs[1].SessionId);
            await AssertOneTerminalEventPerRunAsync(workspace, configDirectory, runs);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_ProviderFailure_ReportsErrorAndContinuesSameSession()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var provider = new FailThenCompleteProvider();
            var output = new StringWriter();
            var error = new StringWriter();
            var exitCode = CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                output,
                new StringReader(string.Join(
                    Environment.NewLine,
                    ["fail this turn", "continue after failure", "/exit", string.Empty])),
                configDirectory,
                _ => _ => provider,
                error: error);

            Assert.AreEqual(ExitCodes.Success, exitCode, error.ToString());
            Assert.AreEqual(2, provider.InvocationCount);
            Assert.Contains("Run failed: simulated provider failure", error.ToString(), StringComparison.Ordinal);
            Assert.Contains("reply-after-failure", output.ToString(), StringComparison.Ordinal);
            var runs = await ReadRunsAsync(workspace, configDirectory);
            Assert.HasCount(2, runs);
            Assert.AreEqual(RunStatus.Failed.ToString(), runs[0].Status);
            Assert.AreEqual(RunStatus.Completed.ToString(), runs[1].Status);
            Assert.AreEqual(runs[0].SessionId, runs[1].SessionId);
            await AssertOneTerminalEventPerRunAsync(workspace, configDirectory, runs);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(20_000, CooperativeCancellation = true)]
    public async Task Repl_IdleInterrupt_Returns130()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var interrupts = new ManualReplInterruptSource();
            var reader = new BlockingTextReader();
            var runTask = Task.Run(() => CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                new StringWriter(),
                reader,
                configDirectory,
                _ => _ => new RecordingProvider("provider-one", ["model-one"]),
                replInterruptSource: interrupts));

            await reader.ReadStarted.Task.WaitAsync(_testContext.CancellationToken);
            interrupts.Interrupt();
            var exitCode = await runTask.WaitAsync(_testContext.CancellationToken);

            Assert.AreEqual(ExitCodes.UserInterrupted, exitCode);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Repl_SecondInterruptDuringCancellation_Returns130()
    {
        var root = CreateTemporaryDirectory();
        var provider = new SlowCancellationProvider();
        try
        {
            var configDirectory = Path.Combine(root, "config");
            var workspace = Path.Combine(root, "workspace");
            await WriteConfigurationAsync(configDirectory, includeSecondProvider: false);
            var interrupts = new ManualReplInterruptSource();
            var runTask = Task.Run(() => CliApplication.RunForTests(
                ["run", "--workspace", workspace],
                new StringWriter(),
                new StringReader($"cancel slowly{Environment.NewLine}"),
                configDirectory,
                _ => _ => provider,
                replInterruptSource: interrupts));

            await provider.RunStarted.Task.WaitAsync(_testContext.CancellationToken);
            interrupts.Interrupt();
            await provider.CancellationObserved.Task.WaitAsync(_testContext.CancellationToken);
            interrupts.Interrupt();
            provider.AllowCancellationToFinish.TrySetResult();
            var exitCode = await runTask.WaitAsync(_testContext.CancellationToken);

            Assert.AreEqual(ExitCodes.UserInterrupted, exitCode);
        }
        finally
        {
            provider.AllowCancellationToFinish.TrySetResult();
            DeleteDirectory(root);
        }
    }

    private async Task WriteConfigurationAsync(
        string configDirectory,
        bool includeSecondProvider)
    {
        var loader = new StandaloneConfigLoader(configDirectory);
        var providers = new List<ProviderEntry>
        {
            CreateProvider("provider-one", "model-one")
        };
        if (includeSecondProvider)
        {
            var second = CreateProvider("provider-two", "model-two");
            second.Models.Add("model-two-alt");
            providers.Add(second);
        }

        await loader.SaveAsync(
            new StandaloneConfig
            {
                DefaultProvider = "provider-one",
                DefaultModel = "model-one",
                DefaultAgent = "agent-one",
                Providers = providers
            },
            _testContext.CancellationToken);
        await loader.SaveAgentAsync(
            new AgentConfig
            {
                Id = "agent-one",
                Name = "Agent One",
                SystemPrompt = "Agent one prompt",
                Provider = "provider-one",
                Model = "model-one"
            },
            _testContext.CancellationToken);
        if (includeSecondProvider)
        {
            await loader.SaveAgentAsync(
                new AgentConfig
                {
                    Id = "agent-two",
                    Name = "Agent Two",
                    SystemPrompt = "Agent two prompt",
                    Provider = "provider-two",
                    Model = "model-two"
                },
                _testContext.CancellationToken);
        }
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

    private async Task<IReadOnlyList<SessionRow>> ReadSessionsAsync(
        string workspace,
        string configDirectory)
    {
        var rows = new List<SessionRow>();
        await using var connection = await OpenReadOnlyConnectionAsync(
            workspace,
            configDirectory);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT sessions.session_id, COALESCE(session_selections.selection_version, 0) "
            + "FROM sessions "
            + "LEFT JOIN session_selections USING (session_id) "
            + "ORDER BY sessions.created_at, sessions.session_id;";
        await using var reader = await command.ExecuteReaderAsync(_testContext.CancellationToken);
        while (await reader.ReadAsync(_testContext.CancellationToken))
        {
            rows.Add(new SessionRow(reader.GetString(0), reader.GetInt32(1)));
        }

        return rows;
    }

    private async Task<IReadOnlyList<RunRow>> ReadRunsAsync(
        string workspace,
        string configDirectory)
    {
        var rows = new List<RunRow>();
        await using var connection = await OpenReadOnlyConnectionAsync(
            workspace,
            configDirectory);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT run_id, session_id, status FROM runs ORDER BY created_at, run_id;";
        await using var reader = await command.ExecuteReaderAsync(_testContext.CancellationToken);
        while (await reader.ReadAsync(_testContext.CancellationToken))
        {
            rows.Add(new RunRow(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return rows;
    }

    private async Task AssertOneTerminalEventPerRunAsync(
        string workspace,
        string configDirectory,
        IReadOnlyList<RunRow> runs)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var connection = await OpenReadOnlyConnectionAsync(
            workspace,
            configDirectory);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT run_id, COUNT(*) FROM event_outbox "
            + "WHERE message_type IN ($completed, $failed, $cancelled) GROUP BY run_id;";
        command.Parameters.AddWithValue("$completed", MessageTypes.RunCompleted);
        command.Parameters.AddWithValue("$failed", MessageTypes.RunFailed);
        command.Parameters.AddWithValue("$cancelled", MessageTypes.RunCancelled);
        await using var reader = await command.ExecuteReaderAsync(_testContext.CancellationToken);
        while (await reader.ReadAsync(_testContext.CancellationToken))
        {
            counts.Add(reader.GetString(0), reader.GetInt32(1));
        }

        foreach (var run in runs)
        {
            Assert.IsTrue(counts.TryGetValue(run.RunId, out var count), run.RunId);
            Assert.AreEqual(1, count, run.RunId);
        }
    }

    private async Task<SqliteConnection> OpenReadOnlyConnectionAsync(
        string workspace,
        string configDirectory)
    {
        var context = StandaloneRuntimeContext.Create(
            workspace,
            configDirectory: configDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(context.DataDirectory, "state.db"),
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString());
        await connection.OpenAsync(_testContext.CancellationToken);
        return connection;
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-repl-stage7-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteDirectory(string path)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed record SessionRow(string SessionId, int SelectionVersion);

    private sealed record RunRow(string RunId, string SessionId, string Status);

    private sealed class ProviderSet(
        RecordingProvider providerOne,
        RecordingProvider providerTwo)
    {
        public RecordingProvider ProviderOne { get; } = providerOne;

        public RecordingProvider ProviderTwo { get; } = providerTwo;

        public RecordingProvider Resolve(NextTurnSelection selection) =>
            selection.DefaultSelection.ProviderId switch
            {
                "provider-one" => ProviderOne,
                "provider-two" => ProviderTwo,
                var providerId => throw new InvalidOperationException(
                    $"Provider '{providerId}' is not configured for the test.")
            };
    }

    private sealed class RecordingProvider(
        string providerId,
        IReadOnlyList<string> modelIds) : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => providerId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } = modelIds
            .Select(static modelId => new ProviderModel(
                modelId,
                modelId,
                ContextWindow: 8192))
            .ToArray();

        public ConcurrentQueue<RuntimeProviderRequest> Requests { get; } = new();

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            Requests.Enqueue(request);
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var invocation = Interlocked.Increment(ref _invocationCount);
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                $"reply-{providerId}-{invocation}");
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

    private sealed class InterruptThenCompleteProvider : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => "provider-one";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("model-one", "model-one", ContextWindow: 8192)];

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public TaskCompletionSource FirstRunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var invocation = Interlocked.Increment(ref _invocationCount);
            if (invocation == 1)
            {
                FirstRunStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "reply-after-cancel");
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

    private sealed class FailThenCompleteProvider : IRuntimeProviderAdapter
    {
        private int _invocationCount;

        public string ProviderId => "provider-one";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("model-one", "model-one", ContextWindow: 8192)];

        public int InvocationCount => Volatile.Read(ref _invocationCount);

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _invocationCount) == 1)
            {
                throw new InvalidOperationException("simulated provider failure");
            }

            yield return new TextDeltaProviderEvent(request.InvocationId, "reply-after-failure");
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

    private sealed class SlowCancellationProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => "provider-one";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("model-one", "model-one", ContextWindow: 8192)];

        public TaskCompletionSource RunStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource CancellationObserved { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowCancellationToFinish { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            _ = request;
            RunStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await AllowCancellationToFinish.Task.ConfigureAwait(false);
                throw;
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

    private sealed class ManualReplInterruptSource : IReplInterruptSource
    {
        private readonly object _sync = new();
        private Action? _handler;

        public IDisposable Subscribe(Action interruptHandler)
        {
            lock (_sync)
            {
                if (_handler is not null)
                {
                    throw new InvalidOperationException("An interrupt handler is already subscribed.");
                }

                _handler = interruptHandler;
            }

            return new CallbackSubscription(() =>
            {
                lock (_sync)
                {
                    if (ReferenceEquals(_handler, interruptHandler))
                    {
                        _handler = null;
                    }
                }
            });
        }

        public void Interrupt()
        {
            Action handler;
            lock (_sync)
            {
                handler = _handler
                    ?? throw new InvalidOperationException("No interrupt handler is subscribed.");
            }

            handler();
        }
    }

    private sealed class BlockingTextReader : TextReader
    {
        public TaskCompletionSource ReadStarted { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<string?> ReadLineAsync(
            CancellationToken cancellationToken)
        {
            ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return null;
        }
    }

    private sealed class CallbackSubscription(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;

        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
