using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 文字输出通道。将 AI 流式回复发送到 WebSocket 客户端。
/// 当请求来自 WebSocket 客户端时，定向发送给该客户端；
/// 当请求来自其他来源（主界面、语音）时，广播给所有客户端。
/// </summary>
public sealed class WebSocketChatOutputChannel(
    ILogger<WebSocketChatOutputChannel> logger,
    IChatTransport transport,
    WebSocketRequestContext requestContext) : IAiOutputChannel
{
    /// <inheritdoc />
    public string Name => "WebSocket";

    /// <inheritdoc />
    public bool IsActive => true;

    /// <inheritdoc />
    public async Task OnTokenAsync(string token, CancellationToken cancellationToken = default)
    {
        if (requestContext.ActiveClientId is { } clientId)
            await transport.SendTokenAsync(clientId, token, cancellationToken);
        else
            await transport.BroadcastAsync("token", token, cancellationToken);
    }

    /// <inheritdoc />
    public async Task OnDoneAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        if (requestContext.ActiveClientId is { } clientId)
            await transport.SendDoneAsync(clientId, sessionId, cancellationToken);
        else
            await transport.BroadcastAsync("done", sessionId, cancellationToken);

        logger.LogDebug("WebSocket 输出通道完成，Session：{SessionId}", sessionId);
    }

    /// <inheritdoc />
    public async Task OnCancelledAsync()
    {
        if (requestContext.ActiveClientId is { } clientId)
            await transport.SendErrorAsync(clientId, "cancelled");
        else
            await transport.BroadcastAsync("cancelled", string.Empty);

        logger.LogDebug("WebSocket 输出通道已取消");
    }

    /// <inheritdoc />
    public async Task OnErrorAsync(string message, CancellationToken cancellationToken = default)
    {
        if (requestContext.ActiveClientId is { } clientId)
            await transport.SendErrorAsync(clientId, message, cancellationToken);
        else
            await transport.BroadcastAsync("error", message, cancellationToken);

        logger.LogWarning("WebSocket 输出通道收到错误：{Message}", message);
    }
}
