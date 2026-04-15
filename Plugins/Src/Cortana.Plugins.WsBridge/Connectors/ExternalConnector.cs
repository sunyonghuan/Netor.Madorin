using System.Net.WebSockets;
using System.Text;

using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.WsBridge.Connectors;

/// <summary>
/// 外部应用 WebSocket 连接器（右端）。
/// 负责与外部应用 WebSocket 的原始消息收发，协议转换由适配器完成。
/// </summary>
public sealed class ExternalConnector(string wsUrl, ILogger logger, string? authToken = null) : IAsyncDisposable
{
    private readonly Uri _uri = new(wsUrl);
    private ClientWebSocket? _ws;

    public bool IsConnected => _ws?.State == WebSocketState.Open;

    /// <summary>连接外部应用 WebSocket。</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        if (!string.IsNullOrEmpty(authToken))
            _ws.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");
        try
        {
            await _ws.ConnectAsync(_uri, ct);
            logger.LogInformation("外部应用已连接: {Uri}", _uri);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "连接外部应用失败: {Uri}", _uri);
            return false;
        }
    }

    /// <summary>向外部应用发送消息。</summary>
    public async Task SendAsync(string message, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(message);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    /// <summary>接收一条外部应用原始消息。</summary>
    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return null;
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws is null) return;
        if (_ws.State == WebSocketState.Open)
        {
            try { await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); }
            catch { /* ignore */ }
        }
        _ws.Dispose();
        _ws = null;
    }
}
