using System.Security.Cryptography;
using System.Text.Json;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>Blob upload/download operations multiplexed over an authenticated control connection.</summary>
public sealed class FramedBlobChannel : IBlobChannel
{
    private const int ChunkBytes = 256 * 1024;
    private readonly AuthenticatedFrameChannel? _channel;
    private readonly FramedControlChannel? _peer;
    private int _disposed;

    public FramedBlobChannel(AuthenticatedFrameChannel channel)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
    }

    /// <summary>Creates a Blob channel multiplexed through the control receive pump.</summary>
    public FramedBlobChannel(FramedControlChannel peer)
    {
        _peer = peer ?? throw new ArgumentNullException(nameof(peer));
    }

    public async ValueTask<string> WriteAsync(
        Stream content,
        string contentType,
        CancellationToken cancellationToken = default)
    {
        var reference = await WriteReferenceAsync(
            content,
            contentType,
            runId: "unscoped",
            accessScope: "session",
            TimeSpan.FromMinutes(10),
            cancellationToken).ConfigureAwait(false);
        return reference.BlobId;
    }

    public async ValueTask<BlobReference> WriteReferenceAsync(
        Stream content,
        string contentType,
        string runId,
        string accessScope,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentType);
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentException.ThrowIfNullOrWhiteSpace(accessScope);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lease, TimeSpan.Zero);

        var uploadId = Guid.NewGuid().ToString("N");
        await ExchangeAsync(
            BlobFrameProtocol.Encode(
                BlobFrameOperation.Begin,
                new BlobBeginRequest(
                    uploadId,
                    runId,
                    contentType,
                    accessScope,
                    DateTimeOffset.UtcNow.Add(lease),
                    content.CanSeek ? checked(content.Length - content.Position) : null),
                RuntimeJsonContext.Default.BlobBeginRequest),
            cancellationToken).ConfigureAwait(false);

        var buffer = new byte[ChunkBytes];
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long length = 0;
        try
        {
            while (true)
            {
                var read = await content.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                length = checked(length + read);
                hash.AppendData(buffer, 0, read);
                await ExchangeAsync(
                    BlobFrameProtocol.Encode(
                        BlobFrameOperation.Chunk,
                        new BlobChunkRequest(uploadId),
                        RuntimeJsonContext.Default.BlobChunkRequest,
                        buffer.AsMemory(0, read)),
                    cancellationToken).ConfigureAwait(false);
            }

            var sha256 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var response = await ExchangeAsync(
                BlobFrameProtocol.Encode(
                    BlobFrameOperation.Complete,
                    new BlobCompleteRequest(uploadId, length, sha256),
                    RuntimeJsonContext.Default.BlobCompleteRequest),
                cancellationToken).ConfigureAwait(false);
            return response.Reference
                ?? throw new InvalidDataException("The Blob completion response has no reference.");
        }
        catch
        {
            try
            {
                await ExchangeAsync(
                    BlobFrameProtocol.Encode(
                        BlobFrameOperation.Abort,
                        new BlobChunkRequest(uploadId),
                        RuntimeJsonContext.Default.BlobChunkRequest),
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
            }

            throw;
        }
        finally
        {
            hash.Dispose();
        }
    }

    public async ValueTask<Stream> OpenReadAsync(
        string blobId,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(blobId);
        var response = await ExchangeAsync(
            BlobFrameProtocol.Encode(
                BlobFrameOperation.Read,
                new BlobReadRequest(blobId, 0, 0),
                RuntimeJsonContext.Default.BlobReadRequest),
            cancellationToken).ConfigureAwait(false);
        var reference = response.Reference
            ?? throw new InvalidDataException("The Blob read response has no reference.");
        return new RemoteBlobReadStream(this, reference);
    }

    internal async ValueTask<ReadOnlyMemory<byte>> ReadChunkAsync(
        BlobReference reference,
        long offset,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        var response = await ExchangeAsync(
            BlobFrameProtocol.Encode(
                BlobFrameOperation.Read,
                new BlobReadRequest(reference.BlobId, offset, maxBytes),
                RuntimeJsonContext.Default.BlobReadRequest),
            cancellationToken).ConfigureAwait(false);
        if (response.Offset != offset)
        {
            throw new InvalidDataException("The Blob read response offset did not match the request.");
        }

        return response.Data;
    }

    public ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        return ValueTask.CompletedTask;
    }

    private async ValueTask<BlobFrameResponse> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken)
    {
        var responseBytes = _peer is not null
            ? await _peer.ExchangeBlobAsync(request, cancellationToken).ConfigureAwait(false)
            : await _channel!.ExchangeAsync(request, cancellationToken).ConfigureAwait(false);
        if (responseBytes.Length == 0)
        {
            throw new EndOfStreamException("The control channel closed during a Blob operation.");
        }

        var frame = BlobFrameProtocol.Decode(responseBytes);
        if (!frame.IsResponse)
        {
            throw new InvalidDataException("The Blob response flag is missing.");
        }

        var response = BlobFrameProtocol.DeserializeMetadata(
            frame,
            RuntimeJsonContext.Default.BlobOperationResponse);
        if (!response.Success)
        {
            throw new InvalidOperationException(response.Error ?? "The Blob operation failed.");
        }

        return new BlobFrameResponse(response, frame.Data);
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);

    private readonly record struct BlobFrameResponse(
        BlobOperationResponse Metadata,
        ReadOnlyMemory<byte> Data)
    {
        public BlobReference? Reference => Metadata.Reference;

        public long Offset => Metadata.Offset;
    }

    private sealed class RemoteBlobReadStream(
        FramedBlobChannel owner,
        BlobReference reference) : Stream
    {
        private readonly FramedBlobChannel _owner = owner;
        private readonly BlobReference _reference = reference;
        private long _position;
        private bool _disposed;

        public override bool CanRead => !_disposed;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _reference.Length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException("Blob streams are not seekable.");
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

        public override int Read(Span<byte> buffer)
        {
            var temporary = new byte[buffer.Length];
            var read = ReadAsync(temporary).AsTask().GetAwaiter().GetResult();
            temporary.AsSpan(0, read).CopyTo(buffer);
            return read;
        }

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (buffer.Length == 0 || _position >= _reference.Length)
            {
                return 0;
            }

            var requested = Math.Min(buffer.Length, ChunkBytes);
            var bytes = await _owner.ReadChunkAsync(
                _reference,
                _position,
                requested,
                cancellationToken).ConfigureAwait(false);
            bytes.CopyTo(buffer);
            _position = checked(_position + bytes.Length);
            return bytes.Length;
        }

        protected override void Dispose(bool disposing)
        {
            _disposed = true;
            base.Dispose(disposing);
        }

        public override void Flush() => throw new NotSupportedException();

        public override Task FlushAsync(CancellationToken cancellationToken) =>
            Task.FromException(new NotSupportedException());

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
