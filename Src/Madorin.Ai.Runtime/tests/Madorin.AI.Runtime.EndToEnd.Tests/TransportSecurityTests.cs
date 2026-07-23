using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class TransportSecurityTests
{
    [TestMethod]
    public async Task AuthenticatedFrame_TamperedMacIsRejected()
    {
        var pipeName = $"madorin.auth-frame.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var sender = await NamedPipeTransport.ConnectAsync(pipeName);
        await using var receiverTransport = await serverTask;
        var key = RandomNumberGenerator.GetBytes(32);
        using var receiver = new AuthenticatedFrameChannel(
            receiverTransport,
            Convert.ToBase64String(key),
            "server",
            "client");
        var envelope = CreateAuthenticatedEnvelope(key, "client", 1, "authenticated"u8);
        envelope[^1] ^= 0x01;

        await sender.SendFrameAsync(envelope);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await receiver.ReceiveFrameAsync());
    }

    [TestMethod]
    public async Task AuthenticatedFrame_ReplayedSequenceIsRejected()
    {
        var pipeName = $"madorin.auth-frame.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var sender = await NamedPipeTransport.ConnectAsync(pipeName);
        await using var receiverTransport = await serverTask;
        var key = RandomNumberGenerator.GetBytes(32);
        using var receiver = new AuthenticatedFrameChannel(
            receiverTransport,
            Convert.ToBase64String(key),
            "server",
            "client");
        var envelope = CreateAuthenticatedEnvelope(key, "client", 1, "once"u8);

        await sender.SendFrameAsync(envelope);
        CollectionAssert.AreEqual(
            "once"u8.ToArray(),
            (await receiver.ReceiveFrameAsync()).ToArray());
        await sender.SendFrameAsync(envelope);

        await Assert.ThrowsExactlyAsync<InvalidDataException>(
            async () => await receiver.ReceiveFrameAsync());
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Handshake_StaleTimestampIsRejected()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var transport = await NamedPipeTransport.ConnectAsync(
            fixture.Server.PipeName,
            timeout.Token);
        var channel = new FramedChannel(transport);
        await channel.SendJsonAsync(
            new HandshakeClientHello(
                "stale-host",
                Convert.ToBase64String(HandshakeProtocol.CreateNonce()),
                DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeMilliseconds()),
            timeout.Token);

        var response = await channel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeServerHello,
            timeout.Token);

        Assert.IsNull(response);
        Assert.AreEqual(0, fixture.Server.ConnectedSessionCount);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task Handshake_ReplayedClientNonceIsRejected()
    {
        await using var fixture = await RuntimeFixture.StartAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var hello = new HandshakeClientHello(
            "replay-host",
            Convert.ToBase64String(HandshakeProtocol.CreateNonce()),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

        await using (var first = await NamedPipeTransport.ConnectAsync(
                         fixture.Server.PipeName,
                         timeout.Token))
        {
            var channel = new FramedChannel(first);
            await channel.SendJsonAsync(hello, timeout.Token);
            Assert.IsNotNull(await channel.ReceiveJsonAsync(
                RuntimeJsonContext.Default.HandshakeServerHello,
                timeout.Token));
        }

        await using var replay = await NamedPipeTransport.ConnectAsync(
            fixture.Server.PipeName,
            timeout.Token);
        var replayChannel = new FramedChannel(replay);
        await replayChannel.SendJsonAsync(hello, timeout.Token);

        var response = await replayChannel.ReceiveJsonAsync(
            RuntimeJsonContext.Default.HandshakeServerHello,
            timeout.Token);

        Assert.IsNull(response);
        Assert.AreEqual(0, fixture.Server.ConnectedSessionCount);
    }

    private static byte[] CreateAuthenticatedEnvelope(
        ReadOnlySpan<byte> key,
        string role,
        long sequence,
        ReadOnlySpan<byte> payload)
    {
        const int headerLength = 12;
        const int macLength = 32;
        var roleBytes = Encoding.UTF8.GetBytes(role);
        var macInput = new byte[roleBytes.Length + 1 + sizeof(long) + payload.Length];
        roleBytes.CopyTo(macInput, 0);
        BinaryPrimitives.WriteInt64BigEndian(
            macInput.AsSpan(roleBytes.Length + 1, sizeof(long)),
            sequence);
        payload.CopyTo(macInput.AsSpan(roleBytes.Length + 1 + sizeof(long)));
        var mac = HMACSHA256.HashData(key, macInput);
        var envelope = new byte[headerLength + payload.Length + macLength];
        "MAF1"u8.CopyTo(envelope);
        BinaryPrimitives.WriteInt64BigEndian(envelope.AsSpan(4, sizeof(long)), sequence);
        payload.CopyTo(envelope.AsSpan(headerLength));
        mac.CopyTo(envelope.AsSpan(headerLength + payload.Length));
        return envelope;
    }

    private sealed class RuntimeFixture : IAsyncDisposable
    {
        private readonly string _workspace;
        private readonly CancellationTokenSource _shutdown;
        private readonly Task _serverTask;

        private RuntimeFixture(
            string workspace,
            RuntimeServer server,
            CancellationTokenSource shutdown,
            Task serverTask)
        {
            _workspace = workspace;
            Server = server;
            _shutdown = shutdown;
            _serverTask = serverTask;
        }

        public RuntimeServer Server { get; }

        public static async Task<RuntimeFixture> StartAsync()
        {
            var workspace = Path.Combine(
                Path.GetTempPath(),
                "madorin-transport-security-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workspace);
            var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.security.{Guid.NewGuid():N}",
                    HandshakeSecret = Convert.ToBase64String(
                        RandomNumberGenerator.GetBytes(32))
                });
            var shutdown = new CancellationTokenSource();
            return new RuntimeFixture(
                workspace,
                server,
                shutdown,
                server.RunAsync(shutdown.Token));
        }

        public async ValueTask DisposeAsync()
        {
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
