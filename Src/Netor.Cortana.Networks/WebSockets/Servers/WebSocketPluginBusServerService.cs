using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.Memory;
using Netor.Cortana.AI.Providers;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Memory;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.Networks;

/// <summary>
/// 独立端口的内部插件总线服务。承载对话事实、历史回放、长期记忆供应与模型能力控制面。
/// </summary>
public sealed class WebSocketPluginBusServerService : KestrelWebSocketHost, IHostedService, IPluginBusBroadcaster, ILongMemorySupplyClient, IChatTransport, IDisposable
{
    private readonly ILogger<WebSocketPluginBusServerService> logger;
    private readonly SystemSettingsService settingsService;
    private readonly CortanaDbContext db;
    private readonly IPluginModelCapabilityService modelCapabilityService;
    private readonly IPublisher publisher;
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly WebSocketConnectionManager _pluginBusConnections = new(2048, SendTimeout, TimeSpan.FromSeconds(2), CloseAsync);
    private readonly PluginBusSubscriptionRegistry _subscriptions = new();
    private readonly PluginBusChatDispatcher _chatDispatcher;
    private readonly PluginBusMemorySupplyDispatcher _memorySupplyDispatcher;
    private readonly PluginBusConversationHistoryDispatcher _historyDispatcher;
    private readonly PluginBusModelCapabilityDispatcher _modelCapabilityDispatcher;
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);
    private int _restartCount;

    /// <summary>
    /// 当前插件总线服务器实际监听的本地端口。
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// 指示插件总线 Kestrel Host 是否正在运行。
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

    /// <inheritdoc />
    public event Func<string, string, string, List<AttachmentInfo>, Task>? OnMessageReceived;

    /// <inheritdoc />
    public event Func<WebSocketClientMessage, Task>? OnClientMessageReceived;

    /// <summary>
    /// 初始化统一 PluginBus WebSocket 服务。
    /// </summary>
    public WebSocketPluginBusServerService(
        ILogger<WebSocketPluginBusServerService> logger,
        SystemSettingsService settingsService,
        CortanaDbContext db,
        IPluginModelCapabilityService modelCapabilityService,
        IPublisher publisher)
    {
        this.logger = logger;
        this.settingsService = settingsService;
        this.db = db;
        this.modelCapabilityService = modelCapabilityService;
        this.publisher = publisher;
        _chatDispatcher = new PluginBusChatDispatcher(_pluginBusConnections, _subscriptions);
        _memorySupplyDispatcher = new PluginBusMemorySupplyDispatcher(logger);
        _historyDispatcher = new PluginBusConversationHistoryDispatcher(db, logger, SendAsync);
        _modelCapabilityDispatcher = new PluginBusModelCapabilityDispatcher(modelCapabilityService, logger, SendAsync);
    }

    /// <summary>
    /// 当客户端连接到 PluginBus 时触发。
    /// </summary>
    public event Action<string>? OnClientConnected;

    /// <summary>
    /// 当客户端从 PluginBus 断开时触发。
    /// </summary>
    public event Action<string>? OnClientDisconnected;

    /// <summary>
    /// 启动独立的 HTTP Listener，并开始接受插件总线 WebSocket 连接。
    /// </summary>
    /// <param name="cancellationToken">宿主停止启动过程时使用的取消令牌。</param>
    /// <returns>表示启动调度已完成的任务。</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("PluginBus 服务器已启动，端口：{Port}, Endpoint={Endpoint}", Port, CortanaWsEndpoints.BuildPluginBusEndpoint(Port));
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

        var configured = settingsService.GetValue<int>("WebSocket.Port", 0);
        var preferredPort = configured > 0 ? configured : GetRandomPort();
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
            logger.LogWarning(ex, "PluginBus 端口 {Port} 绑定失败，回退随机端口", preferredPort);
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
    /// 停止插件总线服务器，关闭全部 WebSocket 连接并释放连接级发送锁。
    /// </summary>
    /// <param name="cancellationToken">宿主停止服务时传入的取消令牌。</param>
    /// <returns>表示停止过程的任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("PluginBus 服务器已停止");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 串行重启 PluginBus 服务器，并返回重启后的实际监听端口。
    /// </summary>
    public async Task<int> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _restartCount);
            logger.LogInformation("PluginBus 服务器已重启，端口：{Port}", Port);
            return Port;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "PluginBus 服务器重启失败");
            throw;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    private async Task StopCoreAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { }
        await _pluginBusConnections.CloseAllAsync().ConfigureAwait(false);
        _subscriptions.Clear();
        _memorySupplyDispatcher.CancelAll();
        await DisposeAppAsync(cancellationToken).ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    private WebApplication BuildApp(int port)
    {
        return BuildLocalhostApp(port, app =>
        {
            app.Map(CortanaWsEndpoints.PluginBusPath, context => AcceptClientAsync(context, _cts?.Token ?? CancellationToken.None));
        });
    }

    private Task DisposeAppAsync(CancellationToken cancellationToken = default)
    {
        return DisposeAppAsync(
            _app,
            () => _app = null,
            ex => logger.LogDebug(ex, "停止 PluginBus Kestrel Host 时出现可忽略异常"),
            cancellationToken);
    }

    /// <summary>
    /// 向所有已订阅 plugin-bus 的客户端广播一条消息。
    /// </summary>
    /// <param name="message">要广播的 JSON 文本消息。</param>
    /// <param name="cancellationToken">取消发送操作的令牌。</param>
    /// <returns>表示广播过程的任务。</returns>
    public async Task BroadcastPluginBusAsync(string topic, string message, CancellationToken cancellationToken = default)
    {
        var tasks = _subscriptions.GetSubscribers(topic)
            .Select(clientId => SendAsync(clientId, message, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public Task SendTokenAsync(string clientId, string token, CancellationToken cancellationToken = default)
    {
        return _chatDispatcher.SendTokenAsync(clientId, token, cancellationToken);
    }

    /// <inheritdoc />
    public Task SendDoneAsync(string clientId, string? sessionId = null, CancellationToken cancellationToken = default)
    {
        return _chatDispatcher.SendDoneAsync(clientId, sessionId, cancellationToken);
    }

    /// <inheritdoc />
    public Task SendErrorAsync(string clientId, string message, CancellationToken cancellationToken = default)
    {
        return _chatDispatcher.SendErrorAsync(clientId, message, cancellationToken);
    }

    /// <inheritdoc />
    public async Task BroadcastAsync(string type, string data, CancellationToken cancellationToken = default)
    {
        await _chatDispatcher.BroadcastAsync(type, data, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过插件总线连接向 Memory 插件请求长期记忆供应包。
    /// </summary>
    public async Task<MemoryContextSupplyPackage?> SupplyAsync(
        MemoryContextSupplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var memorySubscribers = _subscriptions.GetSubscribers(CortanaWsEndpoints.MemoryTopic).ToArray();
        if (memorySubscribers.Length == 0)
        {
            logger.LogDebug("长期记忆供应请求跳过：没有已订阅的 PluginBus 客户端。RequestId={RequestId}", request.RequestId);
            return null;
        }

        var requestId = string.IsNullOrWhiteSpace(request.RequestId)
            ? Guid.NewGuid().ToString("N")
            : request.RequestId;
        if (!string.Equals(request.RequestId, requestId, StringComparison.Ordinal))
        {
            request = request with { RequestId = requestId };
        }

        var pending = _memorySupplyDispatcher.CreatePending(requestId);
        if (pending is null)
        {
            return null;
        }

        var payload = JsonSerializer.Serialize(request, WebSocketJsonContext.Default.MemoryContextSupplyRequest);
        var sent = false;
        try
        {
            foreach (var clientId in memorySubscribers)
            {
                await SendAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
                sent = true;
            }

            if (!sent)
            {
                _memorySupplyDispatcher.CancelPending(requestId);
                return null;
            }

            var timeoutMs = Math.Clamp(request.TimeoutMs <= 0 ? 30_000 : request.TimeoutMs, 50, 30_000);
            return await _memorySupplyDispatcher.WaitAsync(requestId, pending, timeoutMs, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _memorySupplyDispatcher.CancelPending(requestId);
            throw;
        }
    }

    /// <summary>
    /// 接受插件总线客户端连接，注册连接状态并发送 connected 控制消息。
    /// </summary>
    /// <param name="http">当前 HTTP 上下文。</param>
    /// <param name="ct">取消握手或发送欢迎消息的令牌。</param>
    /// <returns>表示客户端接入过程的任务。</returns>
    private async Task AcceptClientAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = _pluginBusConnections.Add(id, ws, "plugin-bus", ct);

        OnClientConnected?.Invoke(id);
        publisher.Publish(
            Events.OnWebSocketClientConnectionChanged,
            new WebSocketClientConnectionChangedArgs(id, http.Connection.RemoteIpAddress?.ToString() ?? "unknown", http.Connection.RemotePort, true));

        await SendAsync(id, PluginBusMessageFactory.CreateConnected(id), ct);
        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (clientId, token) => SendAsync(clientId, WebSocketHeartbeatLoop.CreatePluginBusPingPayload(clientId, WebSocketHeartbeatLoop.CreateTimestamp()), token),
            clientId => ClosePluginBusClientAsync(clientId)).StartAsync(connection, ct);
        await ReceiveLoopAsync(id, ws, ct).ConfigureAwait(false);
    }

    private void MarkPongReceived(string id, string channel)
    {
        switch (channel)
        {
            case "plugin-bus":
                _pluginBusConnections.MarkPongReceived(id);
                break;
        }
    }

    /// <summary>
    /// 持续接收 PluginBus 客户端消息，并在连接关闭时清理客户端资源。
    /// </summary>
    /// <param name="id">客户端连接标识。</param>
    /// <param name="ws">客户端 WebSocket 连接。</param>
    /// <param name="ct">取消接收循环的令牌。</param>
    /// <returns>表示接收循环的任务。</returns>
    private async Task ReceiveLoopAsync(string id, WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var json = await ReadTextMessageAsync(ws, buf, ct);
                if (json is null) break;
                await HandleMessageAsync(id, json, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (IsRemoteCloseWithoutHandshake(ex))
        {
            logger.LogDebug("PluginBus 客户端未完成关闭握手即断开：{Client}", id);
        }
        catch (WebSocketException ex) { logger.LogWarning(ex, "PluginBus 通信异常 {Client}", id); }
        finally
        {
            await ClosePluginBusClientAsync(id);
            OnClientDisconnected?.Invoke(id);
            publisher.Publish(
                Events.OnWebSocketClientConnectionChanged,
                new WebSocketClientConnectionChangedArgs(id, string.Empty, 0, false));
        }
    }

    /// <summary>
    /// 处理 PluginBus 控制消息，包括订阅与历史回放请求。
    /// </summary>
    /// <param name="id">客户端连接标识。</param>
    /// <param name="json">客户端发送的控制消息 JSON。</param>
    /// <param name="ct">取消处理和响应发送的令牌。</param>
    /// <returns>表示消息处理过程的任务。</returns>
    private async Task HandleMessageAsync(string id, string json, CancellationToken ct)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? string.Empty : string.Empty;
            if (string.Equals(type, "pong", StringComparison.Ordinal))
            {
                MarkPongReceived(id, "plugin-bus");
                return;
            }

            var op = root.TryGetProperty("op", out var opElement) ? opElement.GetString() ?? string.Empty : string.Empty;

            if (string.Equals(type, "send", StringComparison.Ordinal)
                || string.Equals(op, "chat.message.send", StringComparison.Ordinal))
            {
                await HandleChatMessageAsync(id, root, "send").ConfigureAwait(false);
                return;
            }

            if (string.Equals(type, "stop", StringComparison.Ordinal)
                || string.Equals(op, "chat.generation.stop", StringComparison.Ordinal))
            {
                await HandleChatMessageAsync(id, root, "stop").ConfigureAwait(false);
                return;
            }

            if (string.Equals(type, "system.notice", StringComparison.Ordinal)
                || string.Equals(op, "system.notice", StringComparison.Ordinal))
            {
                await HandleChatMessageAsync(id, root, "system.notice").ConfigureAwait(false);
                return;
            }

            if (string.Equals(type, "request", StringComparison.Ordinal)
                && string.Equals(op, CortanaWsEndpoints.ModelCapabilityRequestOperation, StringComparison.Ordinal))
            {
                await _modelCapabilityDispatcher.HandleAsync(id, json, ct);
                return;
            }

            if (string.Equals(type, "subscribe", StringComparison.Ordinal))
            {
                var protocol = root.TryGetProperty("protocol", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(protocol, CortanaWsEndpoints.PluginBusProtocol, StringComparison.Ordinal))
                {
                    await SendPluginBusErrorAsync(id, "protocol 不匹配", ct); return;
                }
                var topics = root.TryGetProperty("topics", out var topicsElement) && topicsElement.ValueKind == JsonValueKind.Array
                    ? topicsElement.EnumerateArray()
                        .Select(static topic => topic.GetString())
                        .Where(static topic => !string.IsNullOrWhiteSpace(topic))
                        .Cast<string>()
                        .Distinct(StringComparer.Ordinal)
                        .ToArray()
                    : [CortanaWsEndpoints.ConversationTopic];

                var subscribed = _subscriptions.Subscribe(id, topics);
                await SendAsync(id, PluginBusMessageFactory.CreateSubscribed(id, subscribed), ct); return;
            }

            if (string.Equals(type, "replay", StringComparison.Ordinal)
                || (string.Equals(type, "request", StringComparison.Ordinal)
                    && string.Equals(op, CortanaWsEndpoints.ConversationHistoryReplayOperation, StringComparison.Ordinal)))
            {
                var payload = root.TryGetProperty("payload", out var payloadElement) && payloadElement.ValueKind == JsonValueKind.Object
                    ? payloadElement
                    : root;
                var since = payload.TryGetProperty("sinceTimestamp", out var s) ? s.GetInt64() : 0L;
                var batch = payload.TryGetProperty("batchSize", out var b) ? Math.Clamp(b.GetInt32(), 100, 2000) : 500;
                var requestId = root.TryGetProperty("requestId", out var replayRequestId) ? replayRequestId.GetString() : null;
                await _historyDispatcher.ReplayAsync(id, requestId, since, batch, ct); return;
            }
            if (string.Equals(type, "response", StringComparison.Ordinal)
                && string.Equals(op, MemoryContextSupplyProtocol.SupplyPackageOperation, StringComparison.Ordinal))
            {
                HandleMemorySupplyResponse(json);
                return;
            }
            if (string.Equals(type, "error", StringComparison.Ordinal)
                && string.Equals(op, MemoryContextSupplyProtocol.SupplyErrorOperation, StringComparison.Ordinal))
            {
                HandleMemorySupplyResponse(json);
                return;
            }
            logger.LogWarning("PluginBus 收到未知消息类型：{Type}", type);
        }
        catch (Exception ex) { logger.LogWarning(ex, "PluginBus 消息解析失败：{Json}", json); }
    }

    /// <summary>
    /// 处理通过 PluginBus 收到的聊天输入消息，并转发给 AI 输入通道订阅者。
    /// </summary>
    private async Task HandleChatMessageAsync(string clientId, JsonElement root, string fallbackType)
    {
        var message = PluginBusChatDispatcher.ReadMessage(clientId, root, fallbackType);

        if (OnMessageReceived is not null)
        {
            await OnMessageReceived.Invoke(message.ClientId, message.Type, message.Data, message.Attachments).ConfigureAwait(false);
        }

        if (OnClientMessageReceived is not null)
        {
            await OnClientMessageReceived.Invoke(new WebSocketClientMessage(message.ClientId, message.Type, message.Data, message.Attachments, message.Title, message.Level, message.Source)).ConfigureAwait(false);
        }
    }

    private void HandleMemorySupplyResponse(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyPackage);
            if (package is not null && !string.IsNullOrWhiteSpace(package.RequestId))
            {
                _memorySupplyDispatcher.CompletePackage(package);
                return;
            }

            var error = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyError);
            if (error is not null && !string.IsNullOrWhiteSpace(error.RequestId))
            {
                _memorySupplyDispatcher.CompleteError(error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理长期记忆供应响应失败");
        }
    }

    /// <summary>
    /// 向指定 PluginBus 客户端串行发送文本消息。
    /// </summary>
    /// <param name="id">目标客户端连接标识。</param>
    /// <param name="text">要发送的文本消息。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private async Task SendAsync(string id, string text, CancellationToken ct)
    {
        await _pluginBusConnections.SendAsync(id, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 向 PluginBus 客户端发送统一的错误控制消息。
    /// </summary>
    /// <param name="id">目标客户端连接标识。</param>
    /// <param name="message">错误说明。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private Task SendPluginBusErrorAsync(string id, string message, CancellationToken ct)
    {
        return SendAsync(id, PluginBusMessageFactory.CreateControlError(id, message), ct);
    }

    /// <summary>
    /// 正常关闭并释放 WebSocket 连接。
    /// </summary>
    /// <param name="id">连接标识。</param>
    /// <param name="ws">要关闭的 WebSocket 连接。</param>
    /// <returns>表示关闭过程的任务。</returns>
    private static async Task CloseAsync(string id, WebSocket ws)
    {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "服务器关闭", CancellationToken.None); } catch { }
        }
        ws.Dispose();
    }

    /// <summary>
    /// 从 PluginBus 客户端集合中移除连接，并关闭释放对应 WebSocket 与发送锁。
    /// </summary>
    private async Task ClosePluginBusClientAsync(string id)
    {
        _subscriptions.Remove(id);
        await _pluginBusConnections.RemoveAndCloseAsync(id).ConfigureAwait(false);
    }

    /// <summary>
    /// 读取一个完整的 WebSocket 文本消息，支持跨帧拼接。
    /// </summary>
    /// <param name="ws">要读取的 WebSocket 连接。</param>
    /// <param name="buffer">复用的接收缓冲区。</param>
    /// <param name="ct">取消读取操作的令牌。</param>
    /// <returns>完整文本消息；收到关闭帧时返回 <see langword="null"/>。</returns>
    private static async Task<string?> ReadTextMessageAsync(WebSocket ws, byte[] buffer, CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.Count > 0) message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return Encoding.UTF8.GetString(message.ToArray());
    }

    /// <summary>
    /// 判断 WebSocket 异常是否表示远端未完成关闭握手就提前断开。
    /// </summary>
    /// <param name="ex">待判断的 WebSocket 异常。</param>
    /// <returns>如果异常表示远端提前关闭连接，则为 <see langword="true"/>；否则为 <see langword="false"/>。</returns>
    private static bool IsRemoteCloseWithoutHandshake(WebSocketException ex) =>
        ex.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely
        || (ex.HResult == unchecked((int)0x80004005)
            && ex.Message.Contains("without completing the close handshake", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 从本地回环地址申请一个当前可用的随机 TCP 端口。
    /// </summary>
    /// <returns>可用于监听的随机端口号。</returns>
    private static int GetRandomPort()
    {
        var l = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0); l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p;
    }

    /// <summary>
    /// 释放服务器持有的监听器、取消源、WebSocket 连接与发送锁资源。
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
            _subscriptions.Clear();
            _memorySupplyDispatcher.CancelAll();
            _lifecycleLock.Dispose();
        }
    }
}
