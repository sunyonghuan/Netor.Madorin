using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Madorin.AI.Runtime.Contracts;
using Madorin.AI.Runtime.Contracts.Serialization;
using Madorin.AI.Runtime.Transport.Abstractions;

namespace Madorin.AI.Runtime.Transport.NamedPipes;

/// <summary>Runtime-to-host event stream over framed JSON.</summary>
public sealed class FramedEventChannel : IEventChannel
{
    public const int DefaultOutboundCapacity = 1024;

    private readonly NamedPipeTransport _transport;
    private readonly Func<long, CancellationToken, ValueTask>? _acknowledge;
    private readonly Channel<RuntimeEventEnvelope>? _outbound;
    private readonly Task? _outboundPump;
    private readonly int _outboundCapacity;
    private Exception? _outboundError;
    private int _disposed;

    public FramedEventChannel(
        NamedPipeTransport transport,
        Func<long, CancellationToken, ValueTask>? acknowledge = null,
        int outboundCapacity = DefaultOutboundCapacity)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _acknowledge = acknowledge;
        ArgumentOutOfRangeException.ThrowIfNegative(outboundCapacity);
        _outboundCapacity = outboundCapacity;
        if (outboundCapacity > 0)
        {
            _outbound = Channel.CreateBounded<RuntimeEventEnvelope>(
                new BoundedChannelOptions(outboundCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false,
                    AllowSynchronousContinuations = false
                });
            _outboundPump = PumpOutboundAsync();
        }
    }

    public int OutboundCapacity => _outboundCapacity;

    public ValueTask SendAsync(
        RuntimeEventEnvelope envelope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        if (_outboundError is not null)
        {
            return ValueTask.FromException(
                new IOException("The event sender failed.", _outboundError));
        }

        if (_outbound is not null)
        {
            return _outbound.Writer.TryWrite(envelope)
                ? ValueTask.CompletedTask
                : ValueTask.FromException(
                    new IOException("The bounded event queue is full."));
        }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            RuntimeJsonContext.Default.RuntimeEventEnvelope);
        return _transport.SendFrameAsync(bytes, cancellationToken);
    }

    public async IAsyncEnumerable<RuntimeEventEnvelope> ReadAllAsync(
        long eventReplayCursor,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var bytes = await _transport.ReceiveFrameAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length == 0)
            {
                yield break;
            }

            var envelope = JsonSerializer.Deserialize(
                bytes,
                RuntimeJsonContext.Default.RuntimeEventEnvelope)
                ?? throw new InvalidDataException("The event envelope was empty.");
            if (envelope.Gsn > eventReplayCursor)
            {
                yield return envelope;
            }
        }
    }

    public ValueTask AcknowledgeAsync(
        long lastConfirmedGlobalSequence,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(lastConfirmedGlobalSequence);
        return _acknowledge is null
            ? ValueTask.FromException(
                new InvalidOperationException(
                    "This event channel was not configured with an acknowledgement sender."))
            : _acknowledge(lastConfirmedGlobalSequence, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        if (_outbound is not null)
        {
            _outbound.Writer.TryComplete();
        }

        await _transport.DisposeAsync().ConfigureAwait(false);

        if (_outboundPump is not null)
        {
            try
            {
                await _outboundPump.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }
        }

    }

    private async Task PumpOutboundAsync()
    {
        try
        {
            await foreach (var envelope in _outbound!.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(
                    envelope,
                    RuntimeJsonContext.Default.RuntimeEventEnvelope);
                await _transport.SendFrameAsync(bytes).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _outboundError = ex;
            _outbound!.Writer.TryComplete(ex);
            throw;
        }
    }
}
