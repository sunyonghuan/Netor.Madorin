using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class TransportFrameTests
{
    [TestMethod]
    public async Task LargeFrame_RoundTripsAcrossShortReads()
    {
        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = await NamedPipeTransport.ConnectAsync(pipeName);
        await using var server = await serverTask;
        var payload = new byte[128 * 1024];
        Random.Shared.NextBytes(payload);

        var sendTask = client.SendFrameAsync(payload).AsTask();
        var received = await server.ReceiveFrameAsync();
        await sendTask;

        CollectionAssert.AreEqual(payload, received);
    }

    [TestMethod]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task ConnectAsync_ListenerStartsAfterClient_WaitsForEndpoint()
    {
        var pipeName = $"madorin.ai.runtime.delayed-listener.{Guid.NewGuid():N}";
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var clientTask = NamedPipeTransport.ConnectAsync(pipeName, timeout.Token);
        await Task.Delay(TimeSpan.FromMilliseconds(50), timeout.Token);
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName, timeout.Token);

        await using var client = await clientTask;
        await using var server = await serverTask;

        Assert.IsNotNull(client.PeerProcessId);
        Assert.IsNotNull(server.PeerProcessId);
    }

    [TestMethod]
    public async Task RandomFragmentation_ReassemblesLengthPrefixAndPayload()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The raw fragmented-frame fixture uses Windows Named Pipe primitives.");
        }

        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync();
        await using var server = await serverTask;
        var payload = RandomNumberGenerator.GetBytes(128 * 1024);
        var frame = new byte[sizeof(uint) + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, checked((uint)payload.Length));
        payload.CopyTo(frame, sizeof(uint));
        var receiveTask = server.ReceiveFrameAsync().AsTask();

        var offset = 0;
        var random = new Random(42);
        while (offset < frame.Length)
        {
            var count = Math.Min(random.Next(1, 98), frame.Length - offset);
            await client.WriteAsync(frame.AsMemory(offset, count));
            offset += count;
        }

        await client.FlushAsync();
        CollectionAssert.AreEqual(payload, await receiveTask);
    }

    [TestMethod]
    public async Task TruncatedHeaderAndPayload_AreRejected()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The raw truncated-frame fixture uses Windows Named Pipe primitives.");
        }

        await AssertTruncatedFrameAsync([0, 0]);
        await AssertTruncatedFrameAsync([0, 0, 0, 10, 1, 2, 3]);
    }

    [TestMethod]
    public async Task InvalidUtf8Json_IsRejected()
    {
        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = await NamedPipeTransport.ConnectAsync(pipeName);
        await using var server = await serverTask;
        var framed = new FramedChannel(server);

        await client.SendFrameAsync(new byte[] { 0xc3, 0x28 });

        var exception = await CaptureExceptionAsync(
            () => framed.ReceiveJsonAsync(
                RuntimeJsonContext.Default.JsonRpcRequest).AsTask());
        Assert.IsInstanceOfType<JsonException>(exception);
    }

    [TestMethod]
    public async Task ControlAndEventFrameLimits_AcceptExactBoundary()
    {
        await AssertBoundaryRoundTripAsync(NamedPipeTransport.MaxControlMessageBytes);
        await AssertBoundaryRoundTripAsync(NamedPipeTransport.MaxEventMessageBytes);
    }

    [TestMethod]
    public async Task OversizedFrame_IsRejectedBeforeAllocation()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("The raw oversized-frame fixture uses Windows Named Pipe primitives.");
        }

        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        var connectTask = client.ConnectAsync();
        await using var server = await serverTask;
        await connectTask;
        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, (uint)NamedPipeTransport.MaxControlMessageBytes + 1);

        await client.WriteAsync(header);
        await client.FlushAsync();

        var exception = await CaptureExceptionAsync(() => server.ReceiveFrameAsync().AsTask());
        Assert.IsInstanceOfType<InvalidDataException>(exception);
    }

    [TestMethod]
    public async Task ClientDispose_ClosesThePipe()
    {
        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = await NamedPipeTransport.ConnectAsync(pipeName);
        await using var server = await serverTask;

        await client.DisposeAsync();

        var frame = await server.ReceiveFrameAsync();
        Assert.IsEmpty(frame);
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task RuntimeProcess_AuthenticatesAndConnectsBothChannels()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        var pipeName = $"madorin.ai.runtime.{instanceId[..8]}";
        var hostInstanceId = Guid.NewGuid().ToString("N");
        var workspace = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-e2e",
            Guid.NewGuid().ToString("N"));
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        Directory.CreateDirectory(workspace);

        using var process = StartRuntimeProcess(workspace, instanceId, secret);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    pipeName,
                    hostInstanceId,
                    ExpectedRuntimeInstanceId: instanceId,
                    HandshakeSecret: secret));

            await client.ConnectAsync(timeout.Token);
            Assert.AreEqual(RuntimeClientState.Connected, client.State);
            var expectedBytes = Encoding.UTF8.GetBytes(
                "路径=C:\\临时\\a b.txt\nurl=https://example.test/a%20b?x=1%2F2\nprompt=你好");
            await using var input = new MemoryStream(expectedBytes, writable: false);
            var blobId = await client.BlobChannel.WriteAsync(
                input,
                "text/plain; charset=utf-8",
                timeout.Token);
            await using var remote = await client.BlobChannel.OpenReadAsync(blobId, timeout.Token);
            using var copied = new MemoryStream();
            await remote.CopyToAsync(copied, timeout.Token);
            CollectionAssert.AreEqual(expectedBytes, copied.ToArray());
            await client.CancelRunAsync(Guid.NewGuid().ToString("N"), timeout.Token);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            if (Directory.Exists(workspace))
            {
                Directory.Delete(workspace, recursive: true);
            }
        }

        Assert.IsTrue(process.HasExited);
    }

    private static Process StartRuntimeProcess(
        string workspace,
        string instanceId,
        string secret)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        var cliAssemblyPath = Path.Combine(AppContext.BaseDirectory, "madorin.dll");
        if (!File.Exists(cliAssemblyPath))
        {
            throw new FileNotFoundException("The Runtime CLI assembly was not copied to the test output.", cliAssemblyPath);
        }

        startInfo.ArgumentList.Add(cliAssemblyPath);
        startInfo.ArgumentList.Add("serve");
        startInfo.ArgumentList.Add("--workspace");
        startInfo.ArgumentList.Add(workspace);
        startInfo.ArgumentList.Add("--instance");
        startInfo.ArgumentList.Add(instanceId);
        var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("The Runtime test process could not be started.");
        process.StandardInput.WriteLine(secret);
        process.StandardInput.Flush();
        process.StandardInput.Close();
        return process;
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> action)
    {
        try
        {
            await action();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task AssertBoundaryRoundTripAsync(int length)
    {
        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName, length);
        await using var client = await NamedPipeTransport.ConnectAsync(pipeName, length);
        await using var server = await serverTask;
        var payload = new byte[length];
        payload[0] = 1;
        payload[^1] = 2;

        var sendTask = client.SendFrameAsync(payload).AsTask();
        var received = await server.ReceiveFrameAsync();
        await sendTask;

        Assert.HasCount(length, received);
        Assert.AreEqual((byte)1, received[0]);
        Assert.AreEqual((byte)2, received[^1]);
    }

    private static async Task AssertTruncatedFrameAsync(byte[] bytes)
    {
        var pipeName = $"madorin.ai.runtime.test.{Guid.NewGuid():N}";
        var serverTask = NamedPipeTransport.CreateServerAsync(pipeName);
        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.Out,
            PipeOptions.Asynchronous);
        await client.ConnectAsync();
        await using var server = await serverTask;
        var receiveTask = server.ReceiveFrameAsync().AsTask();
        await client.WriteAsync(bytes);
        await client.FlushAsync();
        await client.DisposeAsync();

        await Assert.ThrowsExactlyAsync<EndOfStreamException>(async () => await receiveTask);
    }
}
