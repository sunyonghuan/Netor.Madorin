using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>Applies sequence validation and HMAC authentication to post-handshake frames.</summary>
public sealed class AuthenticatedFrameChannel : IDisposable
{
    private const int HeaderLength = 12;
    private const int MacLength = 32;
    private static ReadOnlySpan<byte> Magic => "MAF1"u8;

    private readonly NamedPipeTransport _transport;
    private readonly byte[] _sessionKey;
    private readonly byte[] _sendRole;
    private readonly byte[] _receiveRole;
    private readonly SemaphoreSlim _exchangeGate = new(1, 1);
    private long _sendSequence;
    private long _receiveSequence;
    private int _disposed;

    public AuthenticatedFrameChannel(
        NamedPipeTransport transport,
        string sessionKey,
        string sendRole,
        string receiveRole)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(sendRole);
        ArgumentException.ThrowIfNullOrWhiteSpace(receiveRole);
        _sessionKey = Convert.FromBase64String(sessionKey);
        if (_sessionKey.Length != 32)
        {
            CryptographicOperations.ZeroMemory(_sessionKey);
            throw new ArgumentException("The session key must contain 32 bytes.", nameof(sessionKey));
        }

        _sendRole = Encoding.UTF8.GetBytes(sendRole);
        _receiveRole = Encoding.UTF8.GetBytes(receiveRole);
    }

    public async ValueTask<ReadOnlyMemory<byte>> ExchangeAsync(
        ReadOnlyMemory<byte> request,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _exchangeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await SendFrameAsync(request, cancellationToken).ConfigureAwait(false);
            return await ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _exchangeGate.Release();
        }
    }

    public async ValueTask SendFrameAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sequence = checked(++_sendSequence);
        var envelope = new byte[checked(HeaderLength + payload.Length + MacLength)];
        Magic.CopyTo(envelope);
        BinaryPrimitives.WriteInt64BigEndian(envelope.AsSpan(4, sizeof(long)), sequence);
        payload.Span.CopyTo(envelope.AsSpan(HeaderLength, payload.Length));
        var mac = ComputeMac(_sendRole, sequence, payload.Span);
        try
        {
            mac.CopyTo(envelope.AsSpan(HeaderLength + payload.Length));
            await _transport.SendFrameAsync(envelope, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(mac);
        }
    }

    public async ValueTask<ReadOnlyMemory<byte>> ReceiveFrameAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var envelope = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
        if (envelope.Length == 0)
        {
            return ReadOnlyMemory<byte>.Empty;
        }

        if (envelope.Length < HeaderLength + MacLength
            || !envelope.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            throw new InvalidDataException("The authenticated frame header is invalid.");
        }

        var sequence = BinaryPrimitives.ReadInt64BigEndian(envelope.AsSpan(4, sizeof(long)));
        var expectedSequence = checked(_receiveSequence + 1);
        if (sequence != expectedSequence)
        {
            throw new InvalidDataException(
                $"Authenticated frame sequence {sequence} was received; expected {expectedSequence}.");
        }

        var payloadLength = envelope.Length - HeaderLength - MacLength;
        var payload = envelope.AsSpan(HeaderLength, payloadLength);
        var actualMac = envelope.AsSpan(HeaderLength + payloadLength, MacLength);
        var expectedMac = ComputeMac(_receiveRole, sequence, payload);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedMac, actualMac))
            {
                throw new InvalidDataException("The authenticated frame MAC is invalid.");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedMac);
        }

        _receiveSequence = sequence;
        return payload.ToArray();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CryptographicOperations.ZeroMemory(_sessionKey);
        CryptographicOperations.ZeroMemory(_sendRole);
        CryptographicOperations.ZeroMemory(_receiveRole);
        _exchangeGate.Dispose();
    }

    private byte[] ComputeMac(ReadOnlySpan<byte> role, long sequence, ReadOnlySpan<byte> payload)
    {
        var input = new byte[checked(role.Length + 1 + sizeof(long) + payload.Length)];
        role.CopyTo(input);
        input[role.Length] = 0;
        BinaryPrimitives.WriteInt64BigEndian(
            input.AsSpan(role.Length + 1, sizeof(long)),
            sequence);
        payload.CopyTo(input.AsSpan(role.Length + 1 + sizeof(long)));
        try
        {
            return HMACSHA256.HashData(_sessionKey, input);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(input);
        }
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed != 0, this);
}
