using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class EventPressureTests
{
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task TenStreamingRuns_DoNotStarveHeartbeatOrCancellation()
    {
        const int runCount = 10;
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new FloodProvider(runCount);
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.pressure.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    MaxConcurrentRuns = runCount,
                    MaxConcurrentRunsPerProvider = runCount,
                    ProviderResolver = _ => provider
                });
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId,
                    EnableBackgroundHeartbeat: false));
            await client.ConnectAsync();

            var runIds = new List<string>(runCount);
            for (var index = 0; index < runCount; index++)
            {
                runIds.Add(await client.StartNewSessionRunAsync(CreateRequest(index)));
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await provider.AllStarted.WaitAsync(timeout.Token);
            await Task.Delay(150, timeout.Token);

            var heartbeat = Stopwatch.StartNew();
            await client.HeartbeatAsync(timeout.Token);
            heartbeat.Stop();
            Assert.IsLessThan(TimeSpan.FromSeconds(1), heartbeat.Elapsed);

            foreach (var runId in runIds)
            {
                await client.CancelRunAsync(runId, timeout.Token);
            }

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    private static NewSessionRunRequest CreateRequest(int index) =>
        new(
            $"session-{index}",
            $"run-{index}",
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("flood", "flood-model"),
                new ExpertModeOptions(new AgentRef($"agent-{index}", "v1", "flood"))),
            [new TextContentBlock("start")]);

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-pressure-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed class FloodProvider(int expectedRuns) : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource _allStarted = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private int _started;

        public string ProviderId => "flood";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("flood-model", "Flood Model", ContextWindow: 8192)];

        public Task AllStarted => _allStarted.Task;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            if (Interlocked.Increment(ref _started) >= expectedRuns)
            {
                _allStarted.TrySetResult();
            }

            var delta = new string('x', 2048);
            while (!ct.IsCancellationRequested)
            {
                yield return new TextDeltaProviderEvent(request.InvocationId, delta);
                await Task.Yield();
            }
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default) => Task.CompletedTask;
    }
}
