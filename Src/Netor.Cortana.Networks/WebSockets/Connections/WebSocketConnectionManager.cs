using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 连接集合管理器，统一维护连接注册、查找、广播和关闭。
/// </summary>
internal sealed class WebSocketConnectionManager : IAsyncDisposable
{
    private readonly ConcurrentDictionary<string, WebSocketConnection> _connections = new();
    private readonly int _queueCapacity;
    private readonly TimeSpan _sendTimeout;
    private readonly TimeSpan _enqueueTimeout;
    private readonly Func<string, WebSocket, Task> _closeAsync;

    public WebSocketConnectionManager(
        int queueCapacity,
        TimeSpan sendTimeout,
        TimeSpan enqueueTimeout,
        Func<string, WebSocket, Task> closeAsync)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(queueCapacity);
        ArgumentNullException.ThrowIfNull(closeAsync);

        _queueCapacity = queueCapacity;
        _sendTimeout = sendTimeout;
        _enqueueTimeout = enqueueTimeout;
        _closeAsync = closeAsync;
    }

    public int Count => _connections.Count;

    public IEnumerable<string> ClientIds => _connections.Keys;

    public WebSocketConnection Add(string clientId, WebSocket socket, string channel, CancellationToken cancellationToken)
    {
        var connection = new WebSocketConnection(
            clientId,
            socket,
            channel,
            new WebSocketSendQueue(
                socket,
                _queueCapacity,
                _sendTimeout,
                _enqueueTimeout,
                cancellationToken,
                () => RemoveAndCloseAsync(clientId)),
            _closeAsync);

        if (!_connections.TryAdd(clientId, connection))
        {
            throw new InvalidOperationException($"WebSocket connection already exists: {clientId}");
        }

        return connection;
    }

    public bool TryGet(string clientId, out WebSocketConnection connection)
    {
        return _connections.TryGetValue(clientId, out connection!);
    }

    public bool HasOpen(string clientId)
    {
        return _connections.TryGetValue(clientId, out var connection) && connection.IsOpen;
    }

    public void MarkPongReceived(string clientId)
    {
        if (_connections.TryGetValue(clientId, out var connection))
        {
            connection.MarkPongReceived();
        }
    }

    public bool IsHeartbeatTimedOut(string clientId, TimeSpan timeout)
    {
        return _connections.TryGetValue(clientId, out var connection)
            && connection.IsHeartbeatTimedOut(timeout);
    }

    public async Task SendAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        if (!_connections.TryGetValue(clientId, out var connection) || !connection.IsOpen)
        {
            return;
        }

        try
        {
            await connection.SendAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            await RemoveAndCloseAsync(clientId).ConfigureAwait(false);
        }
    }

    public async Task BroadcastAsync(string message, CancellationToken cancellationToken = default)
    {
        var tasks = _connections.Keys.Select(clientId => SendAsync(clientId, message, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task RemoveAndCloseAsync(string clientId)
    {
        if (_connections.TryRemove(clientId, out var connection))
        {
            await connection.CloseAsync().ConfigureAwait(false);
        }
    }

    public async Task CloseAllAsync()
    {
        var ids = _connections.Keys.ToArray();
        foreach (var id in ids)
        {
            await RemoveAndCloseAsync(id).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAllAsync().ConfigureAwait(false);
    }
}
