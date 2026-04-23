using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 文字输入通道。监听 WebSocket 客户端发送的消息，
/// 将用户输入转发到 AI 引擎统一处理。
/// </summary>
public sealed class WebSocketInputChannel(
    ILogger<WebSocketInputChannel> logger,
    IChatTransport transport,
    IAiChatEngine chatEngine,
    IPublisher publisher,
    WebSocketRequestContext requestContext) : IAiInputChannel, IHostedService, IDisposable
{
    private bool _disposed;

    /// <inheritdoc />
    public string Name => "WebSocket";

    /// <summary>
    /// 启动 WebSocket 输入通道：订阅客户端消息事件。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        transport.OnMessageReceived += HandleClientMessageAsync;
        logger.LogInformation("WebSocket 输入通道已启动");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 WebSocket 输入通道。
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        transport.OnMessageReceived -= HandleClientMessageAsync;
        logger.LogInformation("WebSocket 输入通道已停止");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 处理来自 WebSocket 客户端的消息。
    /// </summary>
    private async Task HandleClientMessageAsync(string clientId, string type, string data, List<AttachmentInfo> attachments)
    {
        switch (type)
        {
            case "send":
                publisher.Publish(
                    Events.OnWebSocketUserMessageReceived,
                    new WebSocketUserMessageReceivedArgs(clientId, data, [.. attachments]));

                requestContext.ActiveClientId = clientId;
                try
                {
                    await chatEngine.SendMessageAsync(data, CancellationToken.None, attachments);
                }
                finally
                {
                    requestContext.ActiveClientId = null;
                }
                break;

            case "stop":
                chatEngine.Stop();
                break;

            default:
                logger.LogWarning("未知的客户端消息类型：{Type}", type);
                break;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        transport.OnMessageReceived -= HandleClientMessageAsync;
    }
}
