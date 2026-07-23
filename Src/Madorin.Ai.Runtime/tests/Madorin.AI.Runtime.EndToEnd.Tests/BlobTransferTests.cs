using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Madorin.AI.Runtime.Client;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Entities;
using Madorin.AI.Runtime.Providers.Abstractions;
using Madorin.AI.Runtime.Server;
using Madorin.AI.Runtime.Transport.NamedPipes;

namespace Madorin.AI.Runtime.EndToEnd.Tests;

[TestClass]
public sealed class BlobTransferTests
{
    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BlobChannel_RoundTripsUnicodeAndEmptyContent_WithAtomicPublication()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.blob.{Guid.NewGuid():N}",
                    HandshakeSecret = secret
                });
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId));
            await client.ConnectAsync();

            var expected = "路径=https://example.test/商品/蓝色，提示词=你好\n"u8.ToArray();
            await using var input = new MemoryStream(expected);
            var blobId = await client.BlobChannel.WriteAsync(input, "text/plain; charset=utf-8");
            await using var output = await client.BlobChannel.OpenReadAsync(blobId);
            await using var copied = new MemoryStream();
            await output.CopyToAsync(copied);
            CollectionAssert.AreEqual(expected, copied.ToArray());

            await using var emptyInput = new MemoryStream();
            var emptyBlobId = await client.BlobChannel.WriteAsync(emptyInput, "application/octet-stream");
            await using var emptyOutput = await client.BlobChannel.OpenReadAsync(emptyBlobId);
            Assert.AreEqual(0L, emptyOutput.Length);
            Assert.AreEqual(0, await emptyOutput.ReadAsync(new byte[1]));

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task BlobChannel_RejectsQuotaAndRemovesStagingFile()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.blob.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    BlobLimits = new Madorin.AI.Runtime.Contracts.RuntimeLimits(MaxBlobBytes: 4)
                });
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId));
            await client.ConnectAsync();

            await using var input = new MemoryStream("12345"u8.ToArray());
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.BlobChannel.WriteAsync(input, "text/plain"));

            var staging = Path.Combine(workspace, ".madorin", "staging");
            Assert.IsFalse(Directory.EnumerateFiles(staging, "*.part").Any());
            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            TryDelete(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BlobChannel_BoundaryCancellationAndSparsePreflight_CleanStaging()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.blob.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    BlobLimits = new RuntimeLimits(MaxBlobBytes: 4)
                });
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId));
            await client.ConnectAsync();

            await using var boundary = new MemoryStream("1234"u8.ToArray());
            var boundaryId = await client.BlobChannel.WriteAsync(boundary, "text/plain");
            await using var boundaryOutput = await client.BlobChannel.OpenReadAsync(boundaryId);
            Assert.AreEqual(4L, boundaryOutput.Length);

            await using var pausing = new PausingReadStream("12"u8.ToArray());
            using var uploadCancellation = new CancellationTokenSource();
            var uploadTask = client.BlobChannel.WriteAsync(
                pausing,
                "application/octet-stream",
                uploadCancellation.Token).AsTask();
            await pausing.Paused.WaitAsync(TimeSpan.FromSeconds(5));
            uploadCancellation.Cancel();
            await Assert.ThrowsAsync<OperationCanceledException>(async () => await uploadTask);

            await using var sparse = new SparseLengthStream(1024L * 1024 * 1024);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                async () => await client.BlobChannel.WriteAsync(
                    sparse,
                    "application/octet-stream"));
            Assert.AreEqual(0, sparse.ReadCount);
            Assert.IsFalse(Directory.EnumerateFiles(
                Path.Combine(workspace, ".madorin", "staging"),
                "*.part").Any());

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task BlobChannel_HashMismatchIsRejectedAndAbortRemovesStaging()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.blob.{Guid.NewGuid():N}",
                    HandshakeSecret = secret
                });
            var serverTask = server.RunAsync(shutdown.Token);
            var (transport, authenticated) = await ConnectAuthenticatedAsync(
                server,
                secret,
                "hash-mismatch-host");
            await using var transportScope = transport;
            using var authenticatedScope = authenticated;
            var uploadId = Guid.NewGuid().ToString("N");
            var begin = await ExchangeBlobAsync(
                authenticated,
                BlobFrameProtocol.Encode(
                    BlobFrameOperation.Begin,
                    new BlobBeginRequest(
                        uploadId,
                        "hash-run",
                        "text/plain",
                        "run",
                        DateTimeOffset.UtcNow.AddMinutes(5),
                        3),
                    RuntimeJsonContext.Default.BlobBeginRequest));
            Assert.IsTrue(begin.Success);
            var chunk = await ExchangeBlobAsync(
                authenticated,
                BlobFrameProtocol.Encode(
                    BlobFrameOperation.Chunk,
                    new BlobChunkRequest(uploadId),
                    RuntimeJsonContext.Default.BlobChunkRequest,
                    "abc"u8.ToArray()));
            Assert.IsTrue(chunk.Success);
            var complete = await ExchangeBlobAsync(
                authenticated,
                BlobFrameProtocol.Encode(
                    BlobFrameOperation.Complete,
                    new BlobCompleteRequest(uploadId, 3, new string('0', 64)),
                    RuntimeJsonContext.Default.BlobCompleteRequest));
            Assert.IsFalse(complete.Success);
            StringAssert.Contains(complete.Error, "SHA-256");
            var abort = await ExchangeBlobAsync(
                authenticated,
                BlobFrameProtocol.Encode(
                    BlobFrameOperation.Abort,
                    new BlobChunkRequest(uploadId),
                    RuntimeJsonContext.Default.BlobChunkRequest));
            Assert.IsTrue(abort.Success);
            Assert.IsFalse(Directory.EnumerateFiles(
                Path.Combine(workspace, ".madorin", "staging"),
                "*.part").Any());

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [Timeout(30_000, CooperativeCancellation = true)]
    public async Task OversizedInlineText_IsUploadedAndReplacedWithBlobReference()
    {
        var workspace = CreateTemporaryDirectory();
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var provider = new CapturingProvider();
        using var shutdown = new CancellationTokenSource();
        try
        {
            await using var server = await RuntimeServer.StartAsync(
                new RuntimeServerOptions(workspace)
                {
                    InstanceId = Guid.NewGuid().ToString("N"),
                    PipePrefix = $"madorin.blob.{Guid.NewGuid():N}",
                    HandshakeSecret = secret,
                    BlobLimits = new RuntimeLimits(MaxInlineContentBytes: 8),
                    MemoryUserHome = Path.Combine(workspace, "home"),
                    ProviderResolver = _ => provider
                });
            var serverTask = server.RunAsync(shutdown.Token);
            await using var client = new RuntimeClient(
                new RuntimeClientOptions(
                    server.PipeName,
                    Guid.NewGuid().ToString("N"),
                    HandshakeSecret: secret,
                    ExpectedRuntimeInstanceId: server.InstanceId));
            await client.ConnectAsync();
            const string expected = "路径=https://example.test/商品，提示词=你好";
            await client.StartNewSessionRunAsync(CreateRequest(expected));

            var reference = await provider.ReferenceObserved.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(Encoding.UTF8.GetByteCount(expected), reference.Length);
            await using var output = await client.BlobChannel.OpenReadAsync(reference.BlobId);
            using var reader = new StreamReader(output, Encoding.UTF8);
            Assert.AreEqual(expected, await reader.ReadToEndAsync());

            shutdown.Cancel();
            await serverTask;
        }
        finally
        {
            shutdown.Cancel();
            TryDelete(workspace);
        }
    }

    private static NewSessionRunRequest CreateRequest(string input) =>
        new(
            Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"),
            RuntimeMode.Expert,
            new NextTurnSelection(
                1,
                RuntimeMode.Expert,
                new DefaultSelection("capture", "capture-model"),
                new ExpertModeOptions(new AgentRef("agent", "v1", "capture"))),
            [new TextContentBlock(input)]);

    private static async Task<(NamedPipeTransport Transport, AuthenticatedFrameChannel Channel)>
        ConnectAuthenticatedAsync(RuntimeServer server, string secret, string hostInstanceId)
    {
        var transport = await NamedPipeTransport.ConnectAsync(server.PipeName);
        try
        {
            var framed = new FramedChannel(transport);
            var nonceC = HandshakeProtocol.CreateNonce();
            var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await framed.SendJsonAsync(
                new HandshakeClientHello(
                    hostInstanceId,
                    Convert.ToBase64String(nonceC),
                    timestamp));
            var hello = await framed.ReceiveJsonAsync(
                RuntimeJsonContext.Default.HandshakeServerHello)
                ?? throw new EndOfStreamException("Runtime closed during the test handshake.");
            var nonceS = Convert.FromBase64String(hello.NonceS);
            Assert.IsTrue(HandshakeProtocol.VerifyRuntimeResponse(
                secret,
                server.InstanceId,
                timestamp,
                nonceC,
                nonceS,
                hello.RuntimeResponse));
            var sessionKey = HandshakeProtocol.DeriveSessionKey(
                secret,
                hostInstanceId,
                server.InstanceId,
                nonceC,
                nonceS);
            await framed.SendJsonAsync(
                new HandshakeClientConfirmation(
                    hostInstanceId,
                    HandshakeProtocol.ComputeSessionProof(
                        sessionKey,
                        "control-channel",
                        hostInstanceId,
                        server.InstanceId)));
            return (
                transport,
                new AuthenticatedFrameChannel(
                    transport,
                    sessionKey,
                    "host-control",
                    "runtime-control"));
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static async Task<BlobOperationResponse> ExchangeBlobAsync(
        AuthenticatedFrameChannel channel,
        ReadOnlyMemory<byte> request)
    {
        var response = await channel.ExchangeAsync(request);
        var frame = BlobFrameProtocol.Decode(response);
        Assert.IsTrue(frame.IsResponse);
        return BlobFrameProtocol.DeserializeMetadata(
            frame,
            RuntimeJsonContext.Default.BlobOperationResponse);
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "madorin-runtime-blob-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class CapturingProvider : IRuntimeProviderAdapter
    {
        private readonly TaskCompletionSource<BlobReference> _referenceObserved = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public string ProviderId => "capture";

        public ProviderCapabilities Capabilities { get; } = new(Streaming: true, Files: true);

        public IReadOnlyList<ProviderModel> Models { get; } =
            [new("capture-model", "Capture Model")];

        public Task<BlobReference> ReferenceObserved => _referenceObserved.Task;

        public async IAsyncEnumerable<RuntimeProviderEvent> CompleteStreamingAsync(
            RuntimeProviderRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            var reference = request.Messages
                .SelectMany(static message => message.Content)
                .OfType<BlobRefContentBlock>()
                .FirstOrDefault()?.Blob;
            if (reference is not null)
            {
                _referenceObserved.TrySetResult(reference);
            }

            await Task.Yield();
            yield return new InvocationCompletedProviderEvent(request.InvocationId, "stop");
        }

        public Task ValidateCapabilitiesAsync(
            ContentBlock[] input,
            CancellationToken ct = default)
        {
            var reference = input.OfType<BlobRefContentBlock>().FirstOrDefault()?.Blob;
            if (reference is not null)
            {
                _referenceObserved.TrySetResult(reference);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PausingReadStream(byte[] firstChunk) : Stream
    {
        private bool _returnedFirstChunk;

        public Task Paused => _paused.Task;

        private readonly TaskCompletionSource _paused = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (!_returnedFirstChunk)
            {
                _returnedFirstChunk = true;
                firstChunk.CopyTo(buffer);
                return firstChunk.Length;
            }

            _paused.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
    }

    private sealed class SparseLengthStream(long length) : Stream
    {
        public int ReadCount { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => true;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ReadCount++;
            throw new InvalidOperationException("Sparse data should have been rejected before reading.");
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ReadCount++;
            return ValueTask.FromException<int>(
                new InvalidOperationException("Sparse data should have been rejected before reading."));
        }

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
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
}
