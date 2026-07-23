using System.Security.Cryptography;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Server;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class ConnectionRecoveryTests
{
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ControlDisconnect_BackgroundHeartbeatReconnectsWithinWindow()
    {
        await using var fixture = await RuntimeFixture.StartAsync(
            new RuntimeClientOptions(
                string.Empty,
                "placeholder",
                HeartbeatInterval: TimeSpan.FromMilliseconds(50),
                ReconnectWindow: TimeSpan.FromSeconds(5),
                ReconnectDelay: TimeSpan.FromMilliseconds(25)));
        var firstConnectedAt = fixture.Client.LastConnectedAt;

        await fixture.Server.DisconnectControlChannelsAsync();
        await WaitUntilAsync(
            () => fixture.Client.LastConnectedAt > firstConnectedAt
                && fixture.Client.State == RuntimeClientState.Connected,
            TimeSpan.FromSeconds(8));

        await fixture.Client.HeartbeatAsync();
        Assert.IsNotNull(fixture.Client.LastSentAt);
        Assert.IsNotNull(fixture.Client.LastReceivedAt);
        Assert.AreEqual(1, fixture.Server.ConnectedSessionCount);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task EventDisconnect_ReconnectsWithoutRepeatingControlInitialization()
    {
        await using var fixture = await RuntimeFixture.StartAsync(
            new RuntimeClientOptions(
                string.Empty,
                "placeholder",
                ReconnectWindow: TimeSpan.FromSeconds(5),
                ReconnectDelay: TimeSpan.FromMilliseconds(25),
                EnableBackgroundHeartbeat: false));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var events = fixture.Client
            .ReadEventsAsync(0, timeout.Token)
            .GetAsyncEnumerator(timeout.Token);
        var nextEvent = events.MoveNextAsync().AsTask();

        await fixture.Server.DisconnectEventChannelsAsync();
        await WaitUntilAsync(
            () => fixture.Server.ConnectedEventChannelCount == 1,
            TimeSpan.FromSeconds(5));
        var runId = await fixture.Client.StartNewSessionRunAsync(CreateRequest(), timeout.Token);

        Assert.IsTrue(await nextEvent);
        Assert.AreEqual(runId, events.Current.RunId);
        Assert.AreEqual(1, fixture.Server.ConnectedSessionCount);
        Assert.AreEqual(RuntimeClientState.Connected, fixture.Client.State);
    }

    private static NewSessionRunRequest CreateRequest() =>
        new(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("missing", "missing"),
                new ExpertModeOptions(new AgentRef("agent", "v1", "test"))),
            [new TextContentBlock("test")]);

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (!predicate() && DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.IsTrue(predicate(), "The expected connection state was not reached before timeout.");
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _workspace;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private RuntimeFixture(
            string workspace,
            RuntimeServer server,
            RuntimeClient client,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            _workspace = workspace;
            Server = server;
            Client = client;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public RuntimeServer Server { get; }

        public RuntimeClient Client { get; }

        public static async Task<RuntimeFixture> StartAsync(RuntimeClientOptions clientTemplate)
        {
            var workspace = Path.Combine(
                Path.GetTempPath(),
                "madorin-runtime-recovery-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.recovery.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    HeartbeatIntervalSeconds = 1
                });
            var shutdown = new CancellationTokenSource();
            var serverTask = server.RunAsync(shutdown.Token);
            var options = clientTemplate with
            {
                PipeName = server.PipeName,
                HostInstanceId = Guid.NewGuid().ToString("N"),
                HandshakeSecret = secret,
                ExpectedRuntimeInstanceId = server.InstanceId
            };
            var client = new RuntimeClient(options);
            await client.ConnectAsync();
            return new RuntimeFixture(workspace, server, client, shutdown, serverTask);
        }

        public async ValueTask DisposeAsync()
        {
            await Client.DisposeAsync();
            _shutdown.Cancel();
            await _serverTask;
            await Server.DisposeAsync();
            _shutdown.Dispose();
            try
            {
                Directory.Delete(_workspace, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
