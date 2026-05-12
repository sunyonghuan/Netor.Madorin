using System.Globalization;
using System.Text.Json;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 应用层心跳循环。
/// </summary>
internal sealed class WebSocketHeartbeatLoop
{
    private readonly TimeSpan _interval;
    private readonly TimeSpan _timeout;
    private readonly Func<string, CancellationToken, Task> _sendPingAsync;
    private readonly Func<string, Task> _closeTimedOutAsync;

    public WebSocketHeartbeatLoop(
        TimeSpan interval,
        TimeSpan timeout,
        Func<string, CancellationToken, Task> sendPingAsync,
        Func<string, Task> closeTimedOutAsync)
    {
        ArgumentNullException.ThrowIfNull(sendPingAsync);
        ArgumentNullException.ThrowIfNull(closeTimedOutAsync);

        _interval = interval;
        _timeout = timeout;
        _sendPingAsync = sendPingAsync;
        _closeTimedOutAsync = closeTimedOutAsync;
    }

    public Task StartAsync(WebSocketConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return Task.Run(() => RunAsync(connection, cancellationToken), CancellationToken.None);
    }

    public static string CreateChatPingPayload(string timestamp)
    {
        return JsonSerializer.Serialize(
            new WsMessage { Type = "ping", Data = timestamp },
            WebSocketJsonContext.Default.WsMessage);
    }

    public static string CreateFeedPingPayload(string clientId, string timestamp)
    {
        return JsonSerializer.Serialize(
            new ConversationFeedControlMessage
            {
                Type = "ping",
                ClientId = clientId,
                Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                Version = CortanaWsEndpoints.ConversationFeedVersion,
                Message = timestamp
            },
            WebSocketJsonContext.Default.ConversationFeedControlMessage);
    }

    public static string CreateModelCapabilityPingPayload(string clientId, string timestamp)
    {
        return JsonSerializer.Serialize(
            new WsMessage { Type = "ping", Data = timestamp, ClientId = clientId },
            WebSocketJsonContext.Default.WsMessage);
    }

    private async Task RunAsync(WebSocketConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(_interval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!connection.IsOpen)
                {
                    return;
                }

                if (connection.IsHeartbeatTimedOut(_timeout))
                {
                    await _closeTimedOutAsync(connection.Id).ConfigureAwait(false);
                    return;
                }

                await _sendPingAsync(connection.Id, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    public static string CreateTimestamp()
    {
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture);
    }
}
