using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Cortana.Plugins.WsBridge.Models;

using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.WsBridge.Connectors;

/// <summary>
/// Cortana 固定侧 WebSocket 连接器（左端）。
/// 负责与 Cortana WebSocket 服务端的 send/stop 及 token/done/error 收发。
/// </summary>
public sealed class CortanaConnector(int wsPort, ILogger logger) : IAsyncDisposable
{
    private readonly Uri _uri = new($"ws://localhost:{wsPort}/ws/");
    private ClientWebSocket? _ws;

    public bool IsConnected => _ws?.State == WebSocketState.Open;
    public string? ClientId { get; private set; }

    /// <summary>连接 Cortana WebSocket 并完成 connected 握手。</summary>
    public async Task<bool> ConnectAsync(CancellationToken ct)
    {
        _ws = new ClientWebSocket();
        try
        {
            await _ws.ConnectAsync(_uri, ct);
            var msg = await ReceiveMessageAsync(ct);
            if (msg?.Type != "connected") return false;
            ClientId = msg.ClientId;
            logger.LogInformation("Cortana 已连接，ClientId={ClientId}", ClientId);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "连接 Cortana 失败: {Uri}", _uri);
            return false;
        }
    }

    /// <summary>向 Cortana 发送用户消息。</summary>
    public async Task SendAsync(string text, CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var msg = new CortanaClientMessage { Type = "send", Data = text };
        await SendRawAsync(JsonSerializer.Serialize(msg, PluginJsonContext.Default.CortanaClientMessage), ct);
    }

    /// <summary>向 Cortana 发送 stop 指令。</summary>
    public async Task SendStopAsync(CancellationToken ct)
    {
        if (_ws?.State != WebSocketState.Open) return;
        var msg = new CortanaClientMessage { Type = "stop" };
        await SendRawAsync(JsonSerializer.Serialize(msg, PluginJsonContext.Default.CortanaClientMessage), ct);
    }

    /// <summary>接收一条 Cortana 服务端消息。</summary>
    public async Task<CortanaServerMessage?> ReceiveMessageAsync(CancellationToken ct)
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
        var text = Encoding.UTF8.GetString(ms.ToArray());
        return JsonSerializer.Deserialize(text, PluginJsonContext.Default.CortanaServerMessage);
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

    private async Task SendRawAsync(string json, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws!.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }
}
