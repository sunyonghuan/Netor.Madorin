using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Memory;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Memory;
using Netor.Cortana.Entitys.ModelCapability;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.Networks;

/// <summary>
/// 轻量级 WebSocket 服务器，用于向前端推送 AI 流式响应。
/// 基于 Kestrel 实现，使用配置端口或随机端口，支持多客户端连接。
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
public sealed class WebSocketInteractionServerService(
    ILogger<WebSocketInteractionServerService> logger,
    SystemSettingsService settingsService,
    IPublisher publisher,
    CortanaDbContext db,
    IPluginModelCapabilityService modelCapabilityService) : KestrelWebSocketHost, IHostedService, IChatTransport, ILongMemorySupplyClient, IConversationFeedBroadcaster, IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly WebSocketConnectionManager _chatConnections = new(1024, SendTimeout, TimeSpan.FromSeconds(2), (clientId, socket) => CloseClientAsync(clientId, socket, "服务器关闭"));
    private readonly WebSocketConnectionManager _conversationFeedConnections = new(2048, SendTimeout, TimeSpan.FromSeconds(2), (clientId, socket) => CloseClientAsync(clientId, socket, "服务器关闭"));
    private readonly ConcurrentDictionary<string, byte> _conversationFeedSubscriptions = new();
    private readonly WebSocketConnectionManager _modelCapabilityConnections = new(512, SendTimeout, TimeSpan.FromSeconds(2), (clientId, socket) => CloseClientAsync(clientId, socket, "服务器关闭"));
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MemoryContextSupplyPackage?>> _memorySupplyPendingRequests = new();
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);
    private int _restartCount;

    /// <summary>
    /// 服务器监听端口。
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// 指示交互 WebSocket Kestrel Host 是否正在运行。
    /// </summary>
    public bool IsRunning => _app is not null;

    /// <summary>
    /// 最近一次启动或重启失败的错误信息。
    /// </summary>
    public string LastError { get; private set; } = string.Empty;

    /// <summary>
    /// 服务已执行的重启次数。
    /// </summary>
    public int RestartCount => Volatile.Read(ref _restartCount);

    /// <summary>
    /// 当收到客户端发送的消息时触发。
    /// 参数：clientId, type, data, attachments（文件路径列表）。
    /// </summary>
    public event Func<string, string, string, List<AttachmentInfo>, Task>? OnMessageReceived;

    /// <summary>
    /// 当收到客户端发送的完整消息时触发，用于读取扩展字段。
    /// </summary>
    public event Func<WebSocketClientMessage, Task>? OnClientMessageReceived;

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
    /// 启动交互 WebSocket 服务器。
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("交互 WebSocket 服务器已启动，端口：{Port}", Port);
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (_app is not null)
        {
            return;
        }

        var configuredPort = settingsService.GetValue<int>("WebSocket.Port", 52841);
        var preferredPort = configuredPort > 0 ? configuredPort : GetRandomPort();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _app = BuildApp(preferredPort);
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            Port = preferredPort;
            LastError = string.Empty;
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "交互 WebSocket 端口 {Port} 绑定失败，回退到随机端口", preferredPort);
            await DisposeAppAsync().ConfigureAwait(false);
            Port = GetRandomPort();
            _app = BuildApp(Port);
            await _app.StartAsync(cancellationToken).ConfigureAwait(false);
            LastError = string.Empty;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            await DisposeAppAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// 停止 WebSocket 服务器，关闭所有客户端连接。
    /// </summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("交互 WebSocket 服务器已停止");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 串行重启交互 WebSocket 服务器，并返回重启后的实际监听端口。
    /// </summary>
    public async Task<int> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _restartCount);
            logger.LogInformation("交互 WebSocket 服务器已重启，端口：{Port}", Port);
            return Port;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "交互 WebSocket 服务器重启失败");
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        _cts?.Cancel();

        await _chatConnections.CloseAllAsync().ConfigureAwait(false);

        await _conversationFeedConnections.CloseAllAsync().ConfigureAwait(false);

        await _modelCapabilityConnections.CloseAllAsync().ConfigureAwait(false);

        _conversationFeedSubscriptions.Clear();
        await DisposeAppAsync(cancellationToken).ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    private WebApplication BuildApp(int port)
    {
        return BuildLocalhostApp(port, app =>
        {
            app.Map(CortanaWsEndpoints.ChatPath, context => AcceptChatClientAsync(context, _cts?.Token ?? CancellationToken.None));
            app.Map(CortanaWsEndpoints.ConversationFeedPath, context => AcceptConversationFeedClientAsync(context, _cts?.Token ?? CancellationToken.None));
            app.Map(ModelCapabilityProtocol.Path, context => AcceptModelCapabilityClientAsync(context, _cts?.Token ?? CancellationToken.None));
        });
    }

    private Task DisposeAppAsync(CancellationToken cancellationToken = default)
    {
        return DisposeAppAsync(
            _app,
            () => _app = null,
            ex => logger.LogDebug(ex, "停止交互 WebSocket Kestrel Host 时出现可忽略异常"),
            cancellationToken);
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

        var tasks = _chatConnections.ClientIds
            .Select(clientId => SendToClientAsync(clientId, payload, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 向所有已订阅 conversation topic 的内部 feed 客户端广播原始事件消息。
    /// </summary>
    public async Task BroadcastConversationFeedAsync(string message, CancellationToken cancellationToken = default)
    {
        var tasks = _conversationFeedSubscriptions.Keys
            .Select(clientId => SendToConversationFeedClientAsync(clientId, message, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<MemoryContextSupplyPackage?> SupplyAsync(
        MemoryContextSupplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_conversationFeedConnections.Count == 0)
        {
            logger.LogDebug("长期记忆供应请求跳过：没有已连接的内部对话 feed 客户端。RequestId={RequestId}", request.RequestId);
            return null;
        }

        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;
        if (!string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
        {
            request = request with { RequestId = requestId };
        }

        var pending = new TaskCompletionSource<MemoryContextSupplyPackage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_memorySupplyPendingRequests.TryAdd(requestId, pending))
        {
            logger.LogDebug("长期记忆供应请求 requestId 冲突，已降级为空。RequestId={RequestId}", requestId);
            return null;
        }

        try
        {
            var payload = JsonSerializer.Serialize(request, WebSocketJsonContext.Default.MemoryContextSupplyRequest);
            var sent = false;
            foreach (var clientId in _conversationFeedConnections.ClientIds)
            {
                await SendToConversationFeedClientAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
                sent = true;
            }

            if (!sent)
            {
                return null;
            }

            var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 250 : request.TimeoutMs, 50, 2_000);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));
            await using var registration = timeoutCts.Token.Register(static state =>
            {
                var source = (TaskCompletionSource<MemoryContextSupplyPackage?>)state!;
                source.TrySetResult(null);
            }, pending);

            return await pending.Task.ConfigureAwait(false);
        }
        finally
        {
            _memorySupplyPendingRequests.TryRemove(requestId, out _);
        }
    }

    private async Task AcceptChatClientAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var remotePort = httpContext.Connection.RemotePort;
        var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var clientId = Guid.NewGuid().ToString("N");
        var connection = _chatConnections.Add(clientId, socket, "chat", cancellationToken);

        OnClientConnected?.Invoke(clientId);
        publisher.Publish(
            Events.OnWebSocketClientConnectionChanged,
            new WebSocketClientConnectionChangedArgs(clientId, remoteIp, remotePort, true));
        logger.LogInformation(
            "WebSocket 客户端已连接：{ClientId}，远端：{RemoteEndpoint}，当前连接数：{Count}",
            clientId,
            remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
            _chatConnections.Count);

        var welcome = JsonSerializer.Serialize(new WsMessage { Type = "connected", ClientId = clientId }, WebSocketJsonContext.Default.WsMessage);
        await SendToClientAsync(clientId, welcome, cancellationToken).ConfigureAwait(false);

        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (id, token) => SendToClientAsync(id, WebSocketHeartbeatLoop.CreateChatPingPayload(WebSocketHeartbeatLoop.CreateTimestamp()), token),
            id => CloseChatClientAsync(id)).StartAsync(connection, cancellationToken);

        await ReceiveMessagesAsync(clientId, socket, remoteIp, remotePort, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptConversationFeedClientAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var remotePort = httpContext.Connection.RemotePort;
        var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var clientId = Guid.NewGuid().ToString("N");
        var connection = _conversationFeedConnections.Add(clientId, socket, "conversation-feed", cancellationToken);

        logger.LogInformation(
            "内部对话 feed 客户端已连接：{ClientId}，远端：{RemoteEndpoint}，当前连接数：{Count}",
            clientId,
            remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
            _conversationFeedConnections.Count);

        var welcome = JsonSerializer.Serialize(new ConversationFeedControlMessage
        {
            Type = "connected",
            ClientId = clientId,
            Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
            Version = CortanaWsEndpoints.ConversationFeedVersion,
            Topics = [CortanaWsEndpoints.ConversationTopic]
        }, WebSocketJsonContext.Default.ConversationFeedControlMessage);

        await SendToConversationFeedClientAsync(clientId, welcome, cancellationToken);
        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (id, token) => SendToConversationFeedClientAsync(id, WebSocketHeartbeatLoop.CreateFeedPingPayload(id, WebSocketHeartbeatLoop.CreateTimestamp()), token),
            id => CloseConversationFeedClientAsync(id)).StartAsync(connection, cancellationToken);

        await ReceiveConversationFeedMessagesAsync(clientId, socket, remoteIp, remotePort, cancellationToken).ConfigureAwait(false);
    }

    private async Task AcceptModelCapabilityClientAsync(
        HttpContext httpContext,
        CancellationToken cancellationToken)
    {
        if (!httpContext.WebSockets.IsWebSocketRequest)
        {
            httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        var remotePort = httpContext.Connection.RemotePort;
        var socket = await httpContext.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var clientId = Guid.NewGuid().ToString("N");
        var connection = _modelCapabilityConnections.Add(clientId, socket, "model-capability", cancellationToken);

        logger.LogInformation(
            "内部模型能力客户端已连接：{ClientId}，远端：{RemoteEndpoint}，当前连接数：{Count}",
            clientId,
            remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
            _modelCapabilityConnections.Count);

        var welcome = JsonSerializer.Serialize(new ModelCapabilityConnectedMessage
        {
            ClientId = clientId
        }, WebSocketJsonContext.Default.ModelCapabilityConnectedMessage);

        await SendToModelCapabilityClientAsync(clientId, welcome, cancellationToken);
        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (id, token) => SendToModelCapabilityClientAsync(id, WebSocketHeartbeatLoop.CreateModelCapabilityPingPayload(id, WebSocketHeartbeatLoop.CreateTimestamp()), token),
            id => CloseModelCapabilityClientAsync(id)).StartAsync(connection, cancellationToken);

        await ReceiveModelCapabilityMessagesAsync(clientId, socket, remoteIp, remotePort, cancellationToken).ConfigureAwait(false);
    }

    private void MarkPongReceived(string clientId, string channel)
    {
        switch (channel)
        {
            case "chat":
                _chatConnections.MarkPongReceived(clientId);
                break;
            case "conversation-feed":
                _conversationFeedConnections.MarkPongReceived(clientId);
                break;
            case "model-capability":
                _modelCapabilityConnections.MarkPongReceived(clientId);
                break;
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
                var json = await ReadTextMessageAsync(socket, buffer, cancellationToken);
                if (json is null) break;
                await HandleClientMessageAsync(clientId, json);
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
            await CloseChatClientAsync(clientId).ConfigureAwait(false);
            OnClientDisconnected?.Invoke(clientId);
            publisher.Publish(
                Events.OnWebSocketClientConnectionChanged,
                new WebSocketClientConnectionChangedArgs(clientId, remoteIp, remotePort, false));
            logger.LogInformation(
                "WebSocket 客户端已断开：{ClientId}，远端：{RemoteEndpoint}，剩余连接数：{Count}",
                clientId,
                remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
                _chatConnections.Count);
        }
    }

    private async Task ReceiveConversationFeedMessagesAsync(
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
                var json = await ReadTextMessageAsync(socket, buffer, cancellationToken);
                if (json is null) break;
                await HandleConversationFeedMessageAsync(clientId, json, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "内部对话 feed 通信异常，客户端：{ClientId}", clientId);
        }
        finally
        {
            await CloseConversationFeedClientAsync(clientId).ConfigureAwait(false);
            logger.LogInformation(
                "内部对话 feed 客户端已断开：{ClientId}，远端：{RemoteEndpoint}，剩余连接数：{Count}",
                clientId,
                remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
                _conversationFeedConnections.Count);
        }
    }

    private async Task ReceiveModelCapabilityMessagesAsync(
        string clientId,
        WebSocket socket,
        string remoteIp,
        int remotePort,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];

        try
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var text = await ReadTextMessageAsync(socket, buffer, cancellationToken);
                if (text is null) break;
                await HandleModelCapabilityMessageAsync(clientId, text, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException ex)
        {
            logger.LogWarning(ex, "内部模型能力通信异常，客户端：{ClientId}", clientId);
        }
        finally
        {
            await CloseModelCapabilityClientAsync(clientId).ConfigureAwait(false);
            logger.LogInformation(
                "内部模型能力客户端已断开：{ClientId}，远端：{RemoteEndpoint}，剩余连接数：{Count}",
                clientId,
                remotePort > 0 ? $"{remoteIp}:{remotePort}" : remoteIp,
                _modelCapabilityConnections.Count);
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
            if (string.Equals(type, "pong", StringComparison.Ordinal))
            {
                MarkPongReceived(clientId, "chat");
                return;
            }

            var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            var title = root.TryGetProperty("title", out var titleElement) ? titleElement.GetString() : null;
            var level = root.TryGetProperty("level", out var levelElement) ? levelElement.GetString() : null;
            var source = root.TryGetProperty("source", out var sourceElement) ? sourceElement.GetString() : null;

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

            if (OnClientMessageReceived is not null)
            {
                await OnClientMessageReceived.Invoke(new WebSocketClientMessage(
                    clientId,
                    type,
                    data,
                    attachments,
                    title,
                    level,
                    source));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "解析客户端消息失败：{ClientId}，{Json}", clientId, json);
        }
    }

    private async Task HandleConversationFeedMessageAsync(string clientId, string json, CancellationToken cancellationToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var typeElement)
                ? typeElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.Equals(type, "pong", StringComparison.Ordinal))
            {
                MarkPongReceived(clientId, "conversation-feed");
                return;
            }

            var op = root.TryGetProperty("op", out var opElement)
                ? opElement.GetString() ?? string.Empty
                : string.Empty;

            if (string.Equals(type, "response", StringComparison.Ordinal)
                && string.Equals(op, MemoryContextSupplyProtocol.SupplyPackageOperation, StringComparison.Ordinal))
            {
                CompleteMemorySupplyPackage(json);
                return;
            }

            if (string.Equals(type, "error", StringComparison.Ordinal)
                && string.Equals(op, MemoryContextSupplyProtocol.SupplyErrorOperation, StringComparison.Ordinal))
            {
                CompleteMemorySupplyError(json);
                return;
            }

            if (string.Equals(type, "subscribe", StringComparison.Ordinal))
            {
                var protocol = root.TryGetProperty("protocol", out var protocolElement)
                    ? protocolElement.GetString() ?? string.Empty
                    : string.Empty;
                if (!string.Equals(protocol, CortanaWsEndpoints.ConversationFeedProtocol, StringComparison.Ordinal))
                {
                    await SendConversationFeedErrorAsync(clientId, "protocol 不匹配", cancellationToken);
                    return;
                }

                var requestedTopics = root.TryGetProperty("topics", out var topicsElement) && topicsElement.ValueKind == JsonValueKind.Array
                    ? topicsElement.EnumerateArray()
                        .Select(static t => t.GetString())
                        .Where(static t => !string.IsNullOrWhiteSpace(t))
                        .Cast<string>()
                        .ToArray()
                    : [CortanaWsEndpoints.ConversationTopic];

                if (!requestedTopics.Contains(CortanaWsEndpoints.ConversationTopic, StringComparer.Ordinal))
                {
                    await SendConversationFeedErrorAsync(clientId, "当前仅支持 topic=conversation", cancellationToken);
                    return;
                }

                _conversationFeedSubscriptions[clientId] = 0;

                var ack = JsonSerializer.Serialize(new ConversationFeedControlMessage
                {
                    Type = "subscribed",
                    ClientId = clientId,
                    Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                    Version = CortanaWsEndpoints.ConversationFeedVersion,
                    Topics = [CortanaWsEndpoints.ConversationTopic]
                }, WebSocketJsonContext.Default.ConversationFeedControlMessage);

                await SendToConversationFeedClientAsync(clientId, ack, cancellationToken);
                return;
            }

            if (string.Equals(type, "replay", StringComparison.Ordinal))
            {
                var since = root.TryGetProperty("sinceTimestamp", out var s) ? s.GetInt64() : 0L;
                var batchSize = root.TryGetProperty("batchSize", out var b) ? Math.Clamp(b.GetInt32(), 100, 2000) : 500;

                await SendReplayBatchesAsync(clientId, since, batchSize, cancellationToken);
                return;
            }

            logger.LogWarning("内部对话 feed 收到未知消息类型：{Type}", type);
            return;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "解析内部对话 feed 消息失败：{ClientId}，{Json}", clientId, json);
        }
    }

    private void CompleteMemorySupplyPackage(string json)
    {
        var package = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyPackage);
        if (package is null || string.IsNullOrWhiteSpace(package.RequestId))
        {
            return;
        }

        if (_memorySupplyPendingRequests.TryRemove(package.RequestId, out var pending))
        {
            pending.TrySetResult(package);
        }
    }

    private void CompleteMemorySupplyError(string json)
    {
        var error = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyError);
        if (error is null || string.IsNullOrWhiteSpace(error.RequestId))
        {
            return;
        }

        logger.LogDebug(
            "长期记忆供应返回错误：RequestId={RequestId}, Code={Code}, Message={Message}, Retryable={Retryable}",
            error.RequestId,
            error.Code,
            error.Message,
            error.Retryable);

        if (_memorySupplyPendingRequests.TryRemove(error.RequestId, out var pending))
        {
            pending.TrySetResult(null);
        }
    }

    private async Task HandleModelCapabilityMessageAsync(string clientId, string json, CancellationToken cancellationToken)
    {
        ModelCapabilityRequest? request = null;
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("type", out var typeElement)
                    && string.Equals(typeElement.GetString(), "pong", StringComparison.Ordinal))
                {
                    MarkPongReceived(clientId, "model-capability");
                    return;
                }
            }

            request = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.ModelCapabilityRequest);
            if (request is null)
            {
                await SendModelCapabilityErrorAsync(clientId, null, "INVALID_REQUEST", "请求内容为空。", false, cancellationToken);
                return;
            }

            var response = await modelCapabilityService.InvokeAsync(request, cancellationToken);
            var payload = JsonSerializer.Serialize(response, WebSocketJsonContext.Default.ModelCapabilityResponse);
            await SendToModelCapabilityClientAsync(clientId, payload, cancellationToken);
        }
        catch (UnauthorizedAccessException ex)
        {
            await SendModelCapabilityErrorAsync(clientId, request, "UNAUTHORIZED_CAPABILITY", ex.Message, false, cancellationToken);
        }
        catch (TimeoutException ex)
        {
            await SendModelCapabilityErrorAsync(clientId, request, "TIMEOUT", ex.Message, true, cancellationToken);
        }
        catch (InvalidOperationException ex)
        {
            await SendModelCapabilityErrorAsync(clientId, request, "MODEL_NOT_CONFIGURED", ex.Message, false, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 model-capability 请求失败：{Json}", json);
            await SendModelCapabilityErrorAsync(clientId, request, "INTERNAL_ERROR", ex.Message, true, cancellationToken);
        }
    }

    private async Task SendReplayBatchesAsync(string clientId, long sinceTimestamp, int batchSize, CancellationToken ct)
    {
        try
        {
            var total = 0;
            var lastTimestamp = sinceTimestamp;
            while (true)
            {
                var rows = db.Query(
                    $"""
                    SELECT
                        m.Id,
                        m.SessionId,
                        m.Role,
                        m.Content,
                        m.CreatedTimestamp,
                        m.ModelName,
                        m.AgentId AS MessageAgentId,
                        m.AgentName AS MessageAgentName,
                        s.Categorize AS WorkspaceId,
                        s.AgentId AS SessionAgentId,
                        s.RawDiscription
                    FROM ChatMessages m
                    LEFT JOIN ChatSessions s ON s.Id = m.SessionId
                    WHERE m.CreatedTimestamp >= @Since
                    ORDER BY m.CreatedTimestamp
                    LIMIT {batchSize}
                    """,
                    ConversationExportRecordMapper.Read,
                    cmd => cmd.Parameters.AddWithValue("@Since", lastTimestamp));

                if (rows.Count == 0)
                {
                    // completed
                    var completed = JsonSerializer.Serialize(new ConversationFeedEventMessage
                    {
                        Type = "event",
                        Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                        Version = CortanaWsEndpoints.ConversationFeedVersion,
                        Topic = CortanaWsEndpoints.ConversationTopic,
                        EventType = "conversation.export.completed",
                        Payload = JsonDocument.Parse($"{{\"total\":{total}}}").RootElement
                    }, WebSocketJsonContext.Default.ConversationFeedEventMessage);
                    await SendToConversationFeedClientAsync(clientId, completed, ct);
                    break;
                }

                total += rows.Count;
                lastTimestamp = rows[^1].CreatedTimestamp + 1; // 下一批从后一毫秒开始

                var batch = new ConversationExportBatch
                {
                    BatchId = Guid.NewGuid().ToString("N"),
                    HasMore = true,
                    Items = rows.ToArray()
                };

                var payload = JsonSerializer.SerializeToElement(batch, WebSocketJsonContext.Default.ConversationExportBatch);
                var message = JsonSerializer.Serialize(new ConversationFeedEventMessage
                {
                    Type = "event",
                    Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                    Version = CortanaWsEndpoints.ConversationFeedVersion,
                    Topic = CortanaWsEndpoints.ConversationTopic,
                    EventType = "conversation.export.batch",
                    Payload = payload
                }, WebSocketJsonContext.Default.ConversationFeedEventMessage);

                await SendToConversationFeedClientAsync(clientId, message, ct);
            }
            logger.LogInformation("Conversation replay 完成：Since={Since}, Total={Total}", sinceTimestamp, total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conversation replay 失败");
            var err = JsonSerializer.Serialize(new ConversationFeedControlMessage
            {
                Type = "error",
                ClientId = clientId,
                Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                Version = CortanaWsEndpoints.ConversationFeedVersion,
                Message = $"replay failed: {ex.Message}"
            }, WebSocketJsonContext.Default.ConversationFeedControlMessage);
            await SendToConversationFeedClientAsync(clientId, err, ct);
        }
    }

    /// <summary>
    /// 向指定客户端发送原始文本。
    /// </summary>
    private async Task SendToClientAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        await _chatConnections.SendAsync(clientId, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendToConversationFeedClientAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        await _conversationFeedConnections.SendAsync(clientId, message, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendToModelCapabilityClientAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        await _modelCapabilityConnections.SendAsync(clientId, message, cancellationToken).ConfigureAwait(false);
    }

    private Task SendConversationFeedErrorAsync(string clientId, string message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ConversationFeedControlMessage
        {
            Type = "error",
            ClientId = clientId,
            Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
            Version = CortanaWsEndpoints.ConversationFeedVersion,
            Message = message
        }, WebSocketJsonContext.Default.ConversationFeedControlMessage);

        return SendToConversationFeedClientAsync(clientId, payload, cancellationToken);
    }

    private Task SendModelCapabilityErrorAsync(
        string clientId,
        ModelCapabilityRequest? request,
        string code,
        string message,
        bool retryable,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new ModelCapabilityResponse
        {
            RequestId = request?.RequestId ?? string.Empty,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        }, WebSocketJsonContext.Default.ModelCapabilityResponse);

        return SendToModelCapabilityClientAsync(clientId, payload, cancellationToken);
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

    private Task CloseChatClientAsync(string clientId)
    {
        return _chatConnections.RemoveAndCloseAsync(clientId);
    }

    private Task CloseConversationFeedClientAsync(string clientId)
    {
        _conversationFeedSubscriptions.TryRemove(clientId, out _);
        return _conversationFeedConnections.RemoveAndCloseAsync(clientId);
    }

    private Task CloseModelCapabilityClientAsync(string clientId)
    {
        return _modelCapabilityConnections.RemoveAndCloseAsync(clientId);
    }

    private static async Task<string?> ReadTextMessageAsync(WebSocket socket, byte[] buffer, CancellationToken cancellationToken)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.Count > 0) message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
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
        try
        {
            StopAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            _conversationFeedSubscriptions.Clear();
            _lifecycleLock.Dispose();
        }
    }
}