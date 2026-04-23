using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ReminderPlugin;

/// <summary>
/// 轻量 WebSocket 客户端，连接 Cortana 发送提醒消息。
/// </summary>
public sealed class CortanaWsClient(int wsPort, ILogger<CortanaWsClient> logger)
{
    private readonly Uri _uri = new($"ws://localhost:{wsPort}/ws/");

    /// <summary>
    /// 向 Cortana 发送一条提醒文本，等待 done 后断开。
    /// </summary>
    /// <returns>true 表示消息已成功送达；false 表示发送失败。</returns>
    public async Task<bool> SendReminderAsync(string text, CancellationToken ct = default)
    {
        using var ws = new ClientWebSocket();
        try
        {
            logger.LogInformation("开始连接宿主 WebSocket：{Uri}", _uri);
            await ws.ConnectAsync(_uri, ct);
            logger.LogInformation("宿主 WebSocket 已连接：{Uri}", _uri);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "连接宿主 WebSocket 失败：{Uri}", _uri);
            return false;
        }

        try
        {
            // 等待 connected 消息
            var connected = await ReadUntilTypeAsync(ws, "connected", ct);
            if (!connected)
            {
                logger.LogWarning("未收到宿主 WebSocket connected 握手消息：{Uri}", _uri);
                return false;
            }

            // 发送 send 消息
            var msg = new WsClientMessage { Type = "send", Data = text };
            var json = JsonSerializer.Serialize(msg, PluginJsonContext.Default.WsClientMessage);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
            logger.LogInformation("已通过宿主 WebSocket 发送提醒消息，长度：{Length}", text.Length);

            // 等待 done
            var completed = await ReadUntilTypeAsync(ws, "done", ct);
            if (!completed)
            {
                logger.LogWarning("宿主 WebSocket 未返回 done 消息：{Uri}", _uri);
            }

            // 发送 stop 关闭会话
            var stop = new WsClientMessage { Type = "stop" };
            var stopJson = JsonSerializer.Serialize(stop, PluginJsonContext.Default.WsClientMessage);
            await ws.SendAsync(Encoding.UTF8.GetBytes(stopJson), WebSocketMessageType.Text, true, ct);

            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, ct);

            logger.LogInformation("宿主 WebSocket 会话已关闭：{Uri}", _uri);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // 主取消令牌触发，向上传播
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "发送提醒消息失败：{Uri}", _uri);

            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
            }
            catch { /* 忽略关闭错误 */ }

            return false;
        }
    }

    private static async Task<bool> ReadUntilTypeAsync(ClientWebSocket ws, string targetType, CancellationToken ct)
    {
        var buffer = new byte[4096];
        var timeout = TimeSpan.FromSeconds(60);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        try
        {
            while (true)
            {
                var result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close) return false;
                var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                var server = JsonSerializer.Deserialize(text, PluginJsonContext.Default.WsServerMessage);
                if (server?.Type == targetType) return true;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return false; // 内部超时，返回 false 而非抛异常
        }
    }
}
