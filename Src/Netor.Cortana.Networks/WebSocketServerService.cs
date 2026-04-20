using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.Networks;

/// <summary>
/// 轻量级 WebSocket 服务器，用于向前端推送 AI 流式响应。
/// 基于 <see cref="HttpListener"/> 实现，使用随机端口，支持多客户端连接。
/// 实现 <see cref="IHostedService"/> 作为后台服务自动启动/停止。
/// </summary>
/// <remarks>
/// 消息协议（服务端 → 客户端）：
/// <list type="bullet">
///   <item>token: 流式输出的文本片段</item>
///   <item>done: 一次回复完成</item>
///   <item>error: 发生错误</item>
/// </list>
/// 消息协议（客户端 → 服务端）：
/// <list type="bullet">
///   <item>send: 发送用户消息</item>
///   <item>stop: 中止当前生成</item>
/// </list>
/// </remarks>
public sealed class WebSocketServerService(
    ILogger<WebSocketServerService> logger,
    SystemSettingsService settingsService,
    IPublisher publisher) : IHostedService, IChatTransport, IDisposable
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, WebSocket> _clients = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _clientLocks = new();

    /// <summary>
    /// 服务器监听端口。
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// 当收到客户端发送的消息时触发。
    /// 参数：clientId, type, data, attachments（文件路径列表）。
    /// </summary>
    public event Func<string, string, string, List<AttachmentInfo>, Task>? OnMessageReceived;

    /// <summary>
    /// 当客户端连接时触发。
    /// 参数：clientId。
    /// </summary>
    public event Action<string>? OnClientConnected;

    /// <summary>
    /// 当客户端断开时触发。
    /// 参数：clientId。
    /// </summary>
    public event Action<string>? OnClientDisconnected;

    /// <summary>
    /// 启动 WebSocket 服务器。在独立后台线程上运行 HttpListener，
    /// 避免与 WinForms STA 线程的 COM 模式冲突。
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configuredPort = settingsService.GetValue<int>("WebSocket.Port", 52841);
        Port = configuredPort > 0 ? configuredPort : GetRandomPort();

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // 在独立后台线程上启动 HttpListener，避免 STA 线程 COM 模式冲突
        var thread = new Thread(() =>
        {
            _listener = new HttpListener();

            try
            {
                _listener.Prefixes.Add($"http://localhost:{Port}/ws/");
                _listener.Start();
            }
            catch (HttpListenerException ex)
            {
                logger.LogWarning(ex, "端口 {Port} 绑定失败，回退到随机端口", Port);
                _listener.Close();
                _listener = new HttpListener();
                Port = GetRandomPort();
                _listener.Prefixes.Add($"http://localhost:{Port}/ws/");
                _listener.Start();
            }

            logger.LogInformation("WebSocket 服务器已启动，端口：{Port}", Port);

            AcceptConnectionsAsync(_cts.Token).GetAwaiter().GetResult();
        })
        {
            IsBackground = true,
            Name = "WebSocketServer"
        };

        thread.Start();
        return Task.CompletedTask;
    }

    /// <summary>
    /// 停止 WebSocket 服务器，关闭所有客户端连接。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        // 关闭所有客户端连接
        foreach (var (clientId, socket) in _clients)
        {
            await CloseClientAsync(clientId, socket, "服务器关闭");
        }

        _clients.Clear();
        _clientLocks.Clear();

        _listener?.Close();
        logger.LogInformation("WebSocket 服务器已停止");
    }

    /// <summary>
    /// 向指定客户端发送文本片段（token）。
    /// </summary>
    public async Task SendTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new WsMessage { Type = "token", Data = token }, WebSocketJsonContext.Default.WsMessage);
        await SendToClientAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 通知指定客户端本次回复已完成。
    /// </summary>
    public async Task SendDoneAsync(string clientId, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new WsMessage { Type = "done", SessionId = sessionId }, WebSocketJsonContext.Default.WsMessage);
        await SendToClientAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 向指定客户端发送错误消息。
    /// </summary>
    public async Task SendErrorAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new WsMessage { Type = "error", Data = message }, WebSocketJsonContext.Default.WsMessage);
        await SendToClientAsync(clientId, payload, cancellationToken);
    }

    /// <summary>
    /// 向所有已连接的客户端广播消息。
    /// </summary>
    public async Task BroadcastAsync(string type, string data, CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new WsMessage { Type = type, Data = data }, WebSocketJsonContext.Default.WsMessage);

        foreach (var (clientId, _) in _clients)
        {
            await SendToClientAsync(clientId, payload, cancellationToken);
        }
    }

    /// <summary>
    /// 接受客户端连接，为每个新客户端分配唯一 ID。
    /// </summary>
    private async Task AcceptConnectionsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var httpContext = await _listener.GetContextAsync().WaitAsync(cancellationToken);

                if (!httpContext.Request.IsWebSocketRequest)
                {
                    httpContext.Response.StatusCode = 400;
                    httpContext.Response.Close();
                    continue;
                }

                var remoteEndPoint = httpContext.Request.RemoteEndPoint;
                var remoteIp = remoteEndPoint?.Address.ToString() ?? "unknown";
                var remotePort = remoteEndPoint?.Port ?? 0;

                var wsContext = await httpContext.AcceptWebSocketAsync(null);
                var clientId = Guid.NewGuid().ToString("N");
                var socket = wsContext.WebSocket;

                _clients.TryAdd(clientId, socket);
                _clientLocks.TryAdd(clientId, new SemaphoreSlim(1, 1));

                OnClientConnected?.Invoke(clientId);
                publisher.Publish(
                    Events.OnWebSocketClientConnectionChanged,
                    new WebSocketClientConnectionChangedArgs(clientId, remoteIp, remotePort, true));
                logger.LogInformation(
                    "WebSocket 客户端已连接：{ClientId}，远端：{RemoteEndpoint}，当前连接数：{Count}",
                    clientId,
                    remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
                    _clients.Count);

                // 将 clientId 发送给客户端，便于后续消息关联
                var welcome = JsonSerializer.Serialize(new WsMessage { Type = "connected", ClientId = clientId }, WebSocketJsonContext.Default.WsMessage);
                var bytes = Encoding.UTF8.GetBytes(welcome);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);

                _ = ReceiveMessagesAsync(clientId, socket, remoteIp, remotePort, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "接受 WebSocket 连接时出错");
            }
        }
    }

    /// <summary>
    /// 接收指定客户端的消息循环。
    /// </summary>
    private async Task ReceiveMessagesAsync(
        string clientId,
        WebSocket socket,
        string remoteIp,
        int remotePort,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var segment = new ArraySegment<byte>(buffer);
                var result = await socket.ReceiveAsync(segment, cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var json = Encoding.UTF8.GetString(buffer, 0, result.Count);
                    await HandleClientMessageAsync(clientId, json);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "WebSocket 通信异常，客户端：{ClientId}", clientId);
        }
        finally
        {
            RemoveClient(clientId);
            OnClientDisconnected?.Invoke(clientId);
            publisher.Publish(
                Events.OnWebSocketClientConnectionChanged,
                new WebSocketClientConnectionChangedArgs(clientId, remoteIp, remotePort, false));
            logger.LogInformation(
                "WebSocket 客户端已断开：{ClientId}，远端：{RemoteEndpoint}，剩余连接数：{Count}",
                clientId,
                remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
                _clients.Count);
        }
    }

    /// <summary>
    /// 处理指定客户端发来的 JSON 消息。
    /// </summary>
    private async Task HandleClientMessageAsync(string clientId, string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.GetProperty("type").GetString() ?? "";
            var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";

            var attachments = new List<AttachmentInfo>();
            if (root.TryGetProperty("attachments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var path = item.TryGetProperty("path", out var p) ? p.GetString() ?? "" : "";
                    var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var mimeType = item.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

                    if (!string.IsNullOrEmpty(path))
                    {
                        attachments.Add(new AttachmentInfo(path, name, mimeType));
                    }
                }
            }

            if (OnMessageReceived is not null)
            {
                await OnMessageReceived.Invoke(clientId, type, data, attachments);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "解析客户端消息失败：{ClientId}，{Json}", clientId, json);
        }
    }

    /// <summary>
    /// 向指定客户端发送原始文本。
    /// </summary>
    private async Task SendToClientAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        if (!_clients.TryGetValue(clientId, out var socket) || socket.State != WebSocketState.Open)
        {
            return;
        }

        if (!_clientLocks.TryGetValue(clientId, out var sendLock))
        {
            return;
        }

        await sendLock.WaitAsync(cancellationToken);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(message);
            await socket.SendAsync(
                new ArraySegment<byte>(bytes),
                WebSocketMessageType.Text,
                endOfMessage: true,
                cancellationToken);
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "发送 WebSocket 消息失败，客户端：{ClientId}", clientId);
        }
        finally
        {
            sendLock.Release();
        }
    }

    /// <summary>
    /// 关闭指定客户端的 WebSocket 连接。
    /// </summary>
    private static async Task CloseClientAsync(string clientId, WebSocket socket, string reason)
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                await socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    reason,
                    CancellationToken.None);
            }
            catch (WebSocketException)
            {
                // 客户端可能已断开
            }
        }

        socket.Dispose();
    }

    /// <summary>
    /// 从客户端字典中移除指定客户端并释放其锁。
    /// </summary>
    private void RemoveClient(string clientId)
    {
        _clients.TryRemove(clientId, out _);

        if (_clientLocks.TryRemove(clientId, out var sendLock))
        {
            sendLock.Dispose();
        }
    }

    /// <summary>
    /// 获取一个可用的随机端口。
    /// </summary>
    private static int GetRandomPort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();

        return port;
    }

    /// <summary>
    /// 释放资源，关闭服务器和所有客户端连接。
    /// </summary>
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();

        foreach (var (clientId, socket) in _clients)
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                socket.CloseAsync(
                    WebSocketCloseStatus.NormalClosure,
                    "服务器关闭",
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            socket.Dispose();
        }

        _clients.Clear();

        foreach (var (_, sendLock) in _clientLocks)
        {
            sendLock.Dispose();
        }

        _clientLocks.Clear();
        _listener?.Close();
    }
}