using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
[DoNotParallelize]
public sealed class ReferenceHostProcessTests
{
    private const string ProviderId = "reference-provider";
    private const string ModelId = "reference-model";
    private const string PermissionToolId = "host.reference.permission";
    private const string ApprovalToolId = "mcp.reference.approval";
    private const string WorkPlanJson =
        "{\"planVersion\":\"1\",\"goal\":\"Reference host work\",\"steps\":["
        + "{\"stepId\":\"reference-step\",\"goal\":\"Complete the reference task\","
        + "\"targetAgentId\":\"reference-worker\",\"dependsOn\":[],\"depth\":0}]}";
    private static readonly string[] ExpectedProjectReferences =
    [
        "Madorin.AI.Runtime.Client",
        "Madorin.AI.Runtime.Contracts"
    ];

    private readonly TestContext _testContext;

    public ReferenceHostProcessTests(TestContext testContext)
    {
        _testContext = testContext;
    }

    [TestMethod]
    public void SampleHost_ProjectReferencesOnlyClientAndContracts()
    {
        var runtimeRoot = FindRuntimeRoot();
        var projectPath = Path.Combine(
            runtimeRoot,
            "samples",
            "Madorin.AI.Runtime.SampleHost",
            "Madorin.AI.Runtime.SampleHost.csproj");
        var document = XDocument.Load(projectPath, LoadOptions.None);
        var references = document
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value)
            .Where(static value => value is not null)
            .Select(static value => Path.GetFileNameWithoutExtension(value!))
            .Order(StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            ExpectedProjectReferences,
            references);
        Assert.IsFalse(references.Any(static reference =>
            reference.Contains("Provider", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Server", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("Sqlite", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("MAF", StringComparison.OrdinalIgnoreCase)
            || reference.Contains("MEAI", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    [Timeout(120_000, CooperativeCancellation = true)]
    public async Task ReferenceHost_ThreeRuntimes_DemonstratesStage7HostWorkflow()
    {
        var root = CreateTemporaryDirectory();
        var endpoints = new[]
        {
            RuntimeEndpointFixture.Create(root, 0, new ExpertReferenceProvider()),
            RuntimeEndpointFixture.Create(root, 1, new MeetingReferenceProvider()),
            RuntimeEndpointFixture.Create(root, 2, new WorkReferenceProvider())
        };

        try
        {
            await Task.WhenAll(endpoints.Select(endpoint => endpoint.StartAsync()));
            var sampleHostPath = FindArtifact(
                "samples",
                "Madorin.AI.Runtime.SampleHost",
                "Madorin.AI.Runtime.SampleHost.dll");
            await using var host = new ReferenceHostProcess(
                sampleHostPath,
                $"reference-host-{Guid.NewGuid():N}",
                endpoints);

            var readyLine = await host.StartAsync(_testContext.CancellationToken);
            var ready = readyLine.Split('|');
            Assert.HasCount(6, ready);
            Assert.AreEqual("READY", ready[0]);
            Assert.AreEqual("reference", ready[1]);
            Assert.AreEqual(host.HostInstanceId, ready[2]);
            for (var index = 0; index < endpoints.Length; index++)
            {
                Assert.AreEqual(endpoints[index].InstanceId, ready[index + 3]);
            }

            var demo = DemoResult.Parse(
                await host.ReadUntilAsync("DEMO|", _testContext.CancellationToken));
            Assert.AreEqual(MessageTypes.RunCompleted, demo.ExpertTerminal);
            Assert.AreEqual(MessageTypes.RunCompleted, demo.MeetingTerminal);
            Assert.AreEqual(MessageTypes.RunCompleted, demo.WorkTerminal);
            Assert.AreEqual(MessageTypes.RunCancelled, demo.CancelTerminal);
            Assert.AreEqual(2, demo.SelectionVersion);
            Assert.AreEqual(1, demo.PermissionCount);
            Assert.AreEqual(1, demo.ApprovalCount);
            Assert.AreEqual(2, demo.ToolCallCount);
            Assert.AreEqual(1, demo.ToolQueryCount);
            Assert.IsTrue(demo.Gsns.All(static gsn => gsn > 0));

            var eventLines = host.Lines
                .Where(static line => line.StartsWith("EVENT|", StringComparison.Ordinal))
                .ToArray();
            Assert.IsNotEmpty(eventLines);
            foreach (var eventLine in eventLines)
            {
                var parts = eventLine.Split('|');
                Assert.HasCount(7, parts);
                var index = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
                Assert.IsInRange(0, endpoints.Length - 1, index);
                Assert.AreEqual(endpoints[index].InstanceId, parts[2]);
            }

            var eventGsnsByRuntime = eventLines
                .Select(static line => line.Split('|'))
                .GroupBy(static parts => parts[2], StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group
                        .Select(parts => long.Parse(
                            parts[4],
                            System.Globalization.CultureInfo.InvariantCulture))
                        .ToHashSet(),
                    StringComparer.Ordinal);
            Assert.HasCount(3, eventGsnsByRuntime);
            Assert.IsTrue(eventGsnsByRuntime.Values
                .SelectMany(static values => values)
                .GroupBy(static gsn => gsn)
                .Any(static group => group.Count() > 1));

            await endpoints[0].RestartAsync();
            var recovery = RecoveryResult.Parse(
                await host.RecoverAsync(0, _testContext.CancellationToken));
            Assert.AreEqual(0, recovery.Index);
            Assert.AreEqual(demo.ExpertSessionId, recovery.SessionId);
            Assert.AreEqual("Ready", recovery.RehydrateStatus);
            Assert.AreEqual(0, recovery.MismatchedCount);
            Assert.AreEqual(MessageTypes.RunCompleted, recovery.TerminalType);
            Assert.IsGreaterThan(demo.ExpertGsn, recovery.Gsn);

            var standardError = await host.StopAsync(_testContext.CancellationToken);
            var allOutput = string.Join(Environment.NewLine, host.Lines);
            foreach (var endpoint in endpoints)
            {
                Assert.DoesNotContain(endpoint.Secret, allOutput, StringComparison.Ordinal);
                Assert.DoesNotContain(endpoint.Secret, standardError, StringComparison.Ordinal);
            }
        }
        finally
        {
            foreach (var endpoint in endpoints.Reverse())
            {
                await endpoint.DisposeAsync();
            }

            DeleteDirectory(root);
        }
    }

    private static string FindRuntimeRoot()
    {
        var outputDirectory = new DirectoryInfo(AppContext.BaseDirectory);
        return outputDirectory.Parent?.Parent?.Parent?.Parent?.Parent?.FullName
            ?? throw new InvalidOperationException("The Runtime repository root could not be resolved.");
    }

    private static string FindArtifact(params string[] pathParts)
    {
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new InvalidOperationException("The test build configuration could not be resolved.");
        var parts = new List<string> { FindRuntimeRoot() };
        parts.AddRange(pathParts[..^1]);
        parts.Add("bin");
        parts.Add(configuration);
        parts.Add("net10.0");
        parts.Add(pathParts[^1]);
        var artifact = Path.Combine([.. parts]);
        return File.Exists(artifact)
            ? artifact
            : throw new FileNotFoundException("The process test artifact was not built.", artifact);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-reference-host-tests",
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

    private sealed record DemoResult(
        string ExpertSessionId,
        string MeetingSessionId,
        string WorkSessionId,
        string ExpertRunId,
        string MeetingRunId,
        string WorkRunId,
        string CancelRunId,
        string ExpertTerminal,
        string MeetingTerminal,
        string WorkTerminal,
        string CancelTerminal,
        long ExpertGsn,
        long MeetingGsn,
        long WorkGsn,
        long CancelGsn,
        int SelectionVersion,
        int PermissionCount,
        int ApprovalCount,
        int ToolCallCount,
        int ToolQueryCount)
    {
        public long[] Gsns => [ExpertGsn, MeetingGsn, WorkGsn, CancelGsn];

        public static DemoResult Parse(string line)
        {
            var parts = line.Split('|');
            if (parts.Length != 21 || !string.Equals(parts[0], "DEMO", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected reference-host demo line: {line}");
            }

            return new DemoResult(
                parts[1],
                parts[2],
                parts[3],
                parts[4],
                parts[5],
                parts[6],
                parts[7],
                parts[8],
                parts[9],
                parts[10],
                parts[11],
                ParseInt64(parts[12]),
                ParseInt64(parts[13]),
                ParseInt64(parts[14]),
                ParseInt64(parts[15]),
                ParseInt32(parts[16]),
                ParseInt32(parts[17]),
                ParseInt32(parts[18]),
                ParseInt32(parts[19]),
                ParseInt32(parts[20]));
        }
    }

    private sealed record RecoveryResult(
        int Index,
        string SessionId,
        string RehydrateStatus,
        int MismatchedCount,
        string RunId,
        string TerminalType,
        long Gsn)
    {
        public static RecoveryResult Parse(string line)
        {
            var parts = line.Split('|');
            if (parts.Length != 9
                || !string.Equals(parts[0], "OK", StringComparison.Ordinal)
                || !string.Equals(parts[1], "recover", StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Unexpected reference-host recovery line: {line}");
            }

            return new RecoveryResult(
                ParseInt32(parts[2]),
                parts[3],
                parts[4],
                ParseInt32(parts[5]),
                parts[6],
                parts[7],
                ParseInt64(parts[8]));
        }
    }

    private static int ParseInt32(string value) => int.Parse(
        value,
        System.Globalization.CultureInfo.InvariantCulture);

    private static long ParseInt64(string value) => long.Parse(
        value,
        System.Globalization.CultureInfo.InvariantCulture);

    private sealed class ExpertReferenceProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => ReferenceHostProcessTests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(
            Streaming: true,
            ToolCalling: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(ModelId, "Reference Model", ContextWindow: 8_192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            var latestUserText = GetLatestUserText(request);
            if (latestUserText.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                yield break;
            }

            if (latestUserText.Contains("continue after restart", StringComparison.Ordinal))
            {
                yield return new TextDeltaProviderEvent(
                    request.InvocationId,
                    "Recovered expert session completed.");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
                yield break;
            }

            if (!HasToolResult(request, "reference-permission-call"))
            {
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "reference-permission-call",
                    PermissionToolId,
                    "permission",
                    "{}");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "tool_calls");
                yield break;
            }

            if (!HasToolResult(request, "reference-approval-call"))
            {
                yield return new ToolCallCompleteProviderEvent(
                    request.InvocationId,
                    "reference-approval-call",
                    ApprovalToolId,
                    "approval",
                    "{}");
                yield return new InvocationCompletedProviderEvent(request.InvocationId, "tool_calls");
                yield break;
            }

            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                "Reference host tools completed.");
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

    private sealed class MeetingReferenceProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => ReferenceHostProcessTests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(ModelId, "Reference Model", ContextWindow: 8_192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                $"Meeting response from {request.AgentId}.");
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

    private sealed class WorkReferenceProvider : IRuntimeProviderAdapter
    {
        public string ProviderId => ReferenceHostProcessTests.ProviderId;

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new(ModelId, "Reference Model", ContextWindow: 8_192)];

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            ct.ThrowIfCancellationRequested();
            yield return new TextDeltaProviderEvent(
                request.InvocationId,
                request.AgentId == "reference-manager"
                    ? WorkPlanJson
                    : "Reference work step completed.");
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

    private static string GetLatestUserText(RuntimeProviderRequest request) => request.Messages
        .LastOrDefault(static message => message.Role == RuntimeProviderRoles.User)?
        .Content
        .OfType<TextContentBlock>()
        .LastOrDefault()?
        .Text
        ?? string.Empty;

    private static bool HasToolResult(RuntimeProviderRequest request, string callId) =>
        request.Messages
            .Where(static message => message.Role == RuntimeProviderRoles.Tool)
            .SelectMany(static message => message.Content)
            .OfType<ToolResultContentBlock>()
            .Any(result => string.Equals(result.CallId, callId, StringComparison.Ordinal));

    private sealed class RuntimeEndpointFixture : IAsyncDisposable
    {
        private readonly IRuntimeProviderAdapter _provider;
        private CancellationTokenSource? _shutdown;
        private RuntimeServer? _server;
        private Task? _serverTask;

        private RuntimeEndpointFixture(
            string workspace,
            string instanceId,
            string pipePrefix,
            string secret,
            IRuntimeProviderAdapter provider)
        {
            Workspace = workspace;
            InstanceId = instanceId;
            PipePrefix = pipePrefix;
            Secret = secret;
            _provider = provider;
        }

        public string Workspace { get; }

        public string InstanceId { get; }

        public string PipePrefix { get; }

        public string Secret { get; }

        public string PipeName => _server?.PipeName
            ?? throw new InvalidOperationException("The Runtime endpoint has not started.");

        public static RuntimeEndpointFixture Create(
            string root,
            int index,
            IRuntimeProviderAdapter provider)
        {
            var workspace = Path.Combine(root, $"workspace-{index}");
            Directory.CreateDirectory(workspace);
            return new RuntimeEndpointFixture(
                workspace,
                $"reference-runtime-{index}-{Guid.NewGuid():N}",
                $"madorin.reference-host.{index}.{Guid.NewGuid():N}",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                provider);
        }

        public async Task StartAsync()
        {
            if (_server is not null)
            {
                throw new InvalidOperationException("The Runtime endpoint is already running.");
            }

            _server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(Workspace)
                {
                    InstanceId = InstanceId,
                    PipePrefix = PipePrefix,
                    HandshakeSecret = Secret,
                    MemoryUserHome = Path.Combine(Workspace, "home"),
                    ProviderResolver = _ => _provider
                },
                CancellationToken.None);
            _shutdown = new CancellationTokenSource();
            _serverTask = _server.RunAsync(_shutdown.Token);
        }

        public async Task RestartAsync()
        {
            await StopAsync();
            await StartAsync();
        }

        public async ValueTask DisposeAsync() => await StopAsync();

        private async Task StopAsync()
        {
            if (_server is null || _shutdown is null || _serverTask is null)
            {
                return;
            }

            _shutdown.Cancel();
            await _serverTask;
            await _server.DisposeAsync();
            _shutdown.Dispose();
            _server = null;
            _shutdown = null;
            _serverTask = null;
        }
    }

    private sealed class ReferenceHostProcess(
        string sampleHostPath,
        string hostInstanceId,
        RuntimeEndpointFixture[] endpoints) : IAsyncDisposable
    {
        private Process? _process;
        private Task<string>? _standardErrorTask;
        private string _standardError = string.Empty;
        private int _stopped;

        public string HostInstanceId { get; } = hostInstanceId;

        public List<string> Lines { get; } = [];

        public async Task<string> StartAsync(CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(sampleHostPath)
                    ?? throw new InvalidOperationException("The SampleHost directory is unavailable.")
            };
            startInfo.ArgumentList.Add(sampleHostPath);
            startInfo.ArgumentList.Add("reference-host");
            startInfo.ArgumentList.Add("--host");
            startInfo.ArgumentList.Add(HostInstanceId);
            for (var index = 0; index < endpoints.Length; index++)
            {
                var suffix = (index + 1).ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
                var endpoint = endpoints[index];
                startInfo.ArgumentList.Add("--workspace-" + suffix);
                startInfo.ArgumentList.Add(endpoint.Workspace);
                startInfo.ArgumentList.Add("--pipe-" + suffix);
                startInfo.ArgumentList.Add(endpoint.PipeName);
                startInfo.ArgumentList.Add("--instance-" + suffix);
                startInfo.ArgumentList.Add(endpoint.InstanceId);
            }

            _process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("The SampleHost process could not start.");
            _standardErrorTask = _process.StandardError.ReadToEndAsync(cancellationToken);
            foreach (var endpoint in endpoints)
            {
                await _process.StandardInput.WriteLineAsync(
                    endpoint.Secret.AsMemory(),
                    cancellationToken);
            }

            await _process.StandardInput.FlushAsync(cancellationToken);
            return await ReadUntilAsync("READY|reference|", cancellationToken);
        }

        public async Task<string> RecoverAsync(
            int index,
            CancellationToken cancellationToken)
        {
            var process = GetProcess();
            await process.StandardInput.WriteLineAsync(
                $"recover {index}".AsMemory(),
                cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
            return await ReadUntilAsync(
                $"OK|recover|{index}|",
                cancellationToken);
        }

        public async Task<string> ReadUntilAsync(
            string prefix,
            CancellationToken cancellationToken)
        {
            var process = GetProcess();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            while (true)
            {
                string? line;
                try
                {
                    line = await process.StandardOutput.ReadLineAsync(timeout.Token);
                }
                catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"SampleHost did not emit a line beginning with '{prefix}'.",
                        ex);
                }

                if (line is null)
                {
                    throw new EndOfStreamException(
                        $"SampleHost exited before emitting '{prefix}'. stderr: {await GetStandardErrorAsync()}");
                }

                Lines.Add(line);
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                {
                    return line;
                }
            }
        }

        public async Task<string> StopAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref _stopped, 1) != 0)
            {
                return _standardError;
            }

            var process = _process;
            if (process is null)
            {
                return _standardError;
            }

            try
            {
                if (!process.HasExited)
                {
                    await process.StandardInput.WriteLineAsync("exit".AsMemory(), cancellationToken);
                    await process.StandardInput.FlushAsync(cancellationToken);
                    _ = await ReadUntilAsync("OK|exit", cancellationToken);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
            finally
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }

                _standardError = await GetStandardErrorAsync();
                process.Dispose();
                _process = null;
            }

            return _standardError;
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                _ = await StopAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                if (_process is { HasExited: false } process)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
        }

        private Process GetProcess() => _process
            ?? throw new InvalidOperationException("The SampleHost process has not started.");

        private async Task<string> GetStandardErrorAsync()
        {
            if (_standardErrorTask is null || !_standardErrorTask.IsCompleted)
            {
                return _standardError;
            }

            return await _standardErrorTask;
        }
    }
}
