using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Netor.Cortana.Networks;

/// <summary>
/// 单个 WebSocket 连接的串行发送队列。
/// </summary>
internal sealed class WebSocketSendQueue : IAsyncDisposable
{
    private readonly WebSocket _socket;
    private readonly Channel<string> _messages;
    private readonly CancellationTokenSource _cts;
    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _enqueueTimeout;
    private readonly Func<Task> _onFaultedAsync;
    private readonly Task _worker;
    private int _disposed;

    public WebSocketSendQueue(
        WebSocket socket,
        int capacity,
        TimeSpan sendTimeout,
        TimeSpan enqueueTimeout,
        CancellationToken cancellationToken,
        Func<Task> onFaultedAsync)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ArgumentNullException.ThrowIfNull(onFaultedAsync);

        _socket = socket;
        _sendTimeout = sendTimeout;
        _enqueueTimeout = enqueueTimeout;
        _onFaultedAsync = onFaultedAsync;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
        _worker = Task.Run(ProcessAsync);
    }

    public async Task EnqueueAsync(string message, CancellationToken cancellationToken = default)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token, cancellationToken);
        linkedCts.CancelAfter(_enqueueTimeout);
        if (!await _messages.Writer.WaitToWriteAsync(linkedCts.Token).ConfigureAwait(false))
        {
            throw new InvalidOperationException("WebSocket send queue has been completed.");
        }

        if (!_messages.Writer.TryWrite(message))
        {
            throw new TimeoutException("WebSocket send queue is full.");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _messages.Writer.TryComplete();
        await _cts.CancelAsync().ConfigureAwait(false);

        try
        {
            await _worker.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var message in _messages.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await SendCoreAsync(message, _cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            _ = Task.Run(_onFaultedAsync);
        }
    }

    private async Task SendCoreAsync(string message, CancellationToken cancellationToken)
    {
        if (_socket.State != WebSocketState.Open)
        {
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_sendTimeout);
        var bytes = Encoding.UTF8.GetBytes(message);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("WebSocket send timed out.");
        }
    }
}
