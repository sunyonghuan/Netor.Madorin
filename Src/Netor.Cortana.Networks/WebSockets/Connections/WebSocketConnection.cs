using System.Net.WebSockets;

namespace Netor.Cortana.Networks;

/// <summary>
/// 表示一个已接入的 WebSocket 连接及其发送队列。
/// </summary>
internal sealed class WebSocketConnection : IAsyncDisposable
{
    private readonly Func<string, WebSocket, Task> _closeAsync;
    private int _closed;

    public WebSocketConnection(
        string id,
        WebSocket socket,
        string channel,
        WebSocketSendQueue sendQueue,
        Func<string, WebSocket, Task> closeAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentException.ThrowIfNullOrWhiteSpace(channel);
        ArgumentNullException.ThrowIfNull(sendQueue);
        ArgumentNullException.ThrowIfNull(closeAsync);

        Id = id;
        Socket = socket;
        Channel = channel;
        SendQueue = sendQueue;
        _closeAsync = closeAsync;
        LastPongTimestamp = Environment.TickCount64;
    }

    public string Id { get; }

    public WebSocket Socket { get; }

    public string Channel { get; }

    public WebSocketSendQueue SendQueue { get; }

    public long LastPongTimestamp { get; private set; }

    public bool IsOpen => Socket.State == WebSocketState.Open;

    public void MarkPongReceived()
    {
        LastPongTimestamp = Environment.TickCount64;
    }

    public bool IsHeartbeatTimedOut(TimeSpan timeout)
    {
        return Environment.TickCount64 - LastPongTimestamp > timeout.TotalMilliseconds;
    }

    public Task SendAsync(string message, CancellationToken cancellationToken = default)
    {
        return SendQueue.EnqueueAsync(message, cancellationToken);
    }

    public async Task CloseAsync()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        await SendQueue.DisposeAsync().ConfigureAwait(false);
        await _closeAsync(Id, Socket).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
    }
}
