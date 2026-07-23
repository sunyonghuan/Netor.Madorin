using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>
/// A small framed byte transport over Windows named pipes or Unix domain sockets.
/// </summary>
public sealed class NamedPipeTransport : IAsyncDisposable
{
    public const int MaxControlMessageBytes = 1024 * 1024;
    public const int MaxEventMessageBytes = 4 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly string? _unixSocketPath;
    private readonly int _maxFrameBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private int _disposed;

    private NamedPipeTransport(
        Stream stream,
        string? unixSocketPath = null,
        int? peerProcessId = null,
        uint? peerUserId = null,
        uint? peerGroupId = null,
        int maxFrameBytes = MaxControlMessageBytes)
    {
        _stream = stream;
        _unixSocketPath = unixSocketPath;
        _maxFrameBytes = maxFrameBytes;
        PeerProcessId = peerProcessId;
        PeerUserId = peerUserId;
        PeerGroupId = peerGroupId;
    }

    /// <summary>The OS-authenticated peer PID when the platform transport exposes it.</summary>
    public int? PeerProcessId { get; }

    /// <summary>The OS-authenticated Unix peer UID, when available.</summary>
    public uint? PeerUserId { get; }

    /// <summary>The OS-authenticated Unix peer GID, when available.</summary>
    public uint? PeerGroupId { get; }

    public int MaxFrameBytes => _maxFrameBytes;

    /// <summary>Creates a server endpoint and waits for one client connection.</summary>
    public static Task<NamedPipeTransport> CreateServerAsync(
        string pipeName,
        CancellationToken ct = default) =>
        CreateServerAsync(pipeName, MaxControlMessageBytes, ct);

    public static async Task<NamedPipeTransport> CreateServerAsync(
        string pipeName,
        int maxFrameBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);

        if (OperatingSystem.IsWindows())
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly,
                maxFrameBytes,
                maxFrameBytes);

            try
            {
                await server.WaitForConnectionAsync(ct).ConfigureAwait(false);
                return new NamedPipeTransport(
                    server,
                    peerProcessId: WindowsNamedPipePeerProcess.GetClientProcessId(server),
                    maxFrameBytes: maxFrameBytes);
            }
            catch
            {
                await server.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var path = GetUnixSocketPath(pipeName);
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var ownsSocketPath = false;
        try
        {
            if (File.Exists(path))
            {
                throw new IOException($"The Unix socket endpoint '{path}' is already occupied.");
            }

            listener.Bind(new UnixDomainSocketEndPoint(path));
            ownsSocketPath = true;
            const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            File.SetUnixFileMode(path, ownerOnly);
            if (File.GetUnixFileMode(path) != ownerOnly)
            {
                throw new UnauthorizedAccessException("The Unix socket permissions are not owner-only.");
            }
            listener.Listen(1);
            var socket = await listener.AcceptAsync(ct).ConfigureAwait(false);
            var peer = UnixPeerProcess.GetPeerIdentity(socket);
            var currentUserId = UnixPeerProcess.GetCurrentUserId();
            if (peer.UserId != currentUserId)
            {
                socket.Dispose();
                throw new UnauthorizedAccessException(
                    $"The Unix socket peer UID {peer.UserId} does not match current UID {currentUserId}.");
            }

            DeleteSocketIfPresent(path);
            ownsSocketPath = false;
            return new NamedPipeTransport(
                new NetworkStream(socket, ownsSocket: true),
                peerProcessId: peer.ProcessId,
                peerUserId: peer.UserId,
                peerGroupId: peer.GroupId,
                maxFrameBytes: maxFrameBytes);
        }
        finally
        {
            listener.Dispose();
            if (ownsSocketPath)
            {
                DeleteSocketIfPresent(path);
            }
        }
    }

    /// <summary>Connects to an existing named pipe or Unix domain socket.</summary>
    public static Task<NamedPipeTransport> ConnectAsync(
        string pipeName,
        CancellationToken ct = default) =>
        ConnectAsync(pipeName, MaxControlMessageBytes, ct);

    public static async Task<NamedPipeTransport> ConnectAsync(
        string pipeName,
        int maxFrameBytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrameBytes);

        if (OperatingSystem.IsWindows())
        {
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
                return new NamedPipeTransport(
                    client,
                    peerProcessId: WindowsNamedPipePeerProcess.GetServerProcessId(client),
                    maxFrameBytes: maxFrameBytes);
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        var path = GetUnixSocketPath(pipeName);
        while (true)
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(path), ct)
                    .ConfigureAwait(false);
                var peer = UnixPeerProcess.GetPeerIdentity(socket);
                var currentUserId = UnixPeerProcess.GetCurrentUserId();
                if (peer.UserId != currentUserId)
                {
                    throw new UnauthorizedAccessException(
                        $"The Unix socket peer UID {peer.UserId} does not match current UID {currentUserId}.");
                }

                return new NamedPipeTransport(
                    new NetworkStream(socket, ownsSocket: true),
                    peerProcessId: peer.ProcessId,
                    peerUserId: peer.UserId,
                    peerGroupId: peer.GroupId,
                    maxFrameBytes: maxFrameBytes);
            }
            catch (SocketException exception) when (
                IsTransientUnixConnectFailure(exception) && !ct.IsCancellationRequested)
            {
                socket.Dispose();
                await Task.Delay(TimeSpan.FromMilliseconds(20), ct).ConfigureAwait(false);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }

    /// <summary>Sends one length-prefixed frame.</summary>
    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> data,
        CancellationToken ct = default)
    {
        ThrowIfDisposed();
        if (data.Length > _maxFrameBytes)
        {
            throw new InvalidDataException($"Frame exceeds {_maxFrameBytes} bytes.");
        }

        var header = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(header, checked((uint)data.Length));
        await _writeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _stream.WriteAsync(header, ct).ConfigureAwait(false);
            await _stream.WriteAsync(data, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Receives one frame. An empty array indicates an orderly EOF.</summary>
    public async ValueTask<byte[]> ReceiveFrameAsync(CancellationToken ct = default)
    {
        ThrowIfDisposed();
        var header = new byte[sizeof(uint)];
        var headerRead = await ReadAtLeastAsync(header, ct).ConfigureAwait(false);
        if (headerRead == 0)
        {
            return [];
        }

        if (headerRead != header.Length)
        {
            throw new EndOfStreamException("The frame header was truncated.");
        }

        var length = BinaryPrimitives.ReadUInt32BigEndian(header);
        if (length > _maxFrameBytes)
        {
            throw new InvalidDataException($"Frame declares {length} bytes, exceeding the limit.");
        }

        if (length == 0)
        {
            return [];
        }

        var payload = new byte[length];
        var payloadRead = await ReadAtLeastAsync(payload, ct).ConfigureAwait(false);
        if (payloadRead != payload.Length)
        {
            throw new EndOfStreamException("The frame payload was truncated.");
        }

        return payload;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        if (_unixSocketPath is not null)
        {
            DeleteSocketIfPresent(_unixSocketPath);
        }
    }

    private async ValueTask<int> ReadAtLeastAsync(
        Memory<byte> buffer,
        CancellationToken ct)
    {
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await _stream.ReadAsync(buffer[total..], ct).ConfigureAwait(false);
            if (read == 0)
            {
                return total;
            }

            total += read;
        }

        return total;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
    }

    private static string GetUnixSocketPath(string pipeName)
    {
        if (pipeName.Contains(Path.DirectorySeparatorChar) || pipeName.Contains(Path.AltDirectorySeparatorChar))
        {
            return pipeName;
        }

        var safeName = string.Concat(pipeName.Select(static c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));
        return Path.Combine(Path.GetTempPath(), $"{safeName}.sock");
    }

    private static bool IsTransientUnixConnectFailure(SocketException exception) =>
        exception.SocketErrorCode is SocketError.AddressNotAvailable or SocketError.ConnectionRefused;

    private static void DeleteSocketIfPresent(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A stale endpoint is best-effort cleanup; bind/connect reports the actionable error.
        }
    }
}
