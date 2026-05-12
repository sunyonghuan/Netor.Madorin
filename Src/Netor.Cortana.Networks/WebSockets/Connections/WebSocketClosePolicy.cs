using System.Net.WebSockets;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 连接关闭策略，统一关闭帧、Abort 和 Dispose 顺序。
/// </summary>
internal static class WebSocketClosePolicy
{
    public static async Task CloseAsync(
        WebSocket socket,
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(socket);

        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(2));
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, reason, timeoutCts.Token).ConfigureAwait(false);
            }
            catch
            {
                socket.Abort();
            }
        }
        else if (socket.State is not WebSocketState.Closed and not WebSocketState.Aborted)
        {
            socket.Abort();
        }

        socket.Dispose();
    }

    public static void AbortAndDispose(WebSocket socket)
    {
        ArgumentNullException.ThrowIfNull(socket);

        try
        {
            socket.Abort();
        }
        finally
        {
            socket.Dispose();
        }
    }
}
