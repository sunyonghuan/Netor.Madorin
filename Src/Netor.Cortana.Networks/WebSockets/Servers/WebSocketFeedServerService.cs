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
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.Networks;

/// <summary>
/// 独立端口的对话事实 Feed 服务。承载 conversation-feed（订阅、历史回放与长期记忆控制面）。
/// </summary>
public sealed class WebSocketFeedServerService(
    ILogger<WebSocketFeedServerService> logger,
    SystemSettingsService settingsService,
    CortanaDbContext db,
    IPluginModelCapabilityService modelCapabilityService) : KestrelWebSocketHost, IHostedService, IConversationFeedBroadcaster, IDisposable
{
    private WebApplication? _app;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private readonly WebSocketConnectionManager _feedConnections = new(2048, SendTimeout, TimeSpan.FromSeconds(2), CloseAsync);
    private readonly ConcurrentDictionary<string, byte> _subscriptions = new();
    private readonly WebSocketConnectionManager _modelCapabilityConnections = new(512, SendTimeout, TimeSpan.FromSeconds(2), CloseAsync);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<MemoryContextSupplyPackage?>> _memorySupplyPendingRequests = new();
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan HeartbeatTimeout = TimeSpan.FromSeconds(90);
    private int _restartCount;

    /// <summary>
    /// 当前 Feed 服务器实际监听的本地端口。
    /// </summary>
    public int Port { get; private set; }

    /// <summary>
    /// 指示 Feed Kestrel Host 是否正在运行。
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
    /// 启动独立的 HTTP Listener，并开始接受 conversation-feed 与 model-capability WebSocket 连接。
    /// </summary>
    /// <param name="cancellationToken">宿主停止启动过程时使用的取消令牌。</param>
    /// <returns>表示启动调度已完成的任务。</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Conversation Feed 服务器已启动，端口：{Port}", Port);
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

        var configured = settingsService.GetValue<int>("ConversationFeed.Port", 0);
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
            logger.LogWarning(ex, "Feed 端口 {Port} 绑定失败，回退随机端口", preferredPort);
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
    /// 停止 Feed 服务器，关闭全部 WebSocket 连接并释放连接级发送锁。
    /// </summary>
    /// <param name="cancellationToken">宿主停止服务时传入的取消令牌。</param>
    /// <returns>表示停止过程的任务。</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Conversation Feed 服务器已停止");
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    /// <summary>
    /// 串行重启 Feed 服务器，并返回重启后的实际监听端口。
    /// </summary>
    public async Task<int> RestartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(cancellationToken).ConfigureAwait(false);
            await StartCoreAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _restartCount);
            logger.LogInformation("Conversation Feed 服务器已重启，端口：{Port}", Port);
            return Port;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            logger.LogError(ex, "Conversation Feed 服务器重启失败");
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
        await _feedConnections.CloseAllAsync().ConfigureAwait(false);
        await _modelCapabilityConnections.CloseAllAsync().ConfigureAwait(false);
        _subscriptions.Clear();
        foreach (var (requestId, pending) in _memorySupplyPendingRequests)
        {
            pending.TrySetResult(null);
        }
        _memorySupplyPendingRequests.Clear();
        await DisposeAppAsync(cancellationToken).ConfigureAwait(false);
        _cts?.Dispose();
        _cts = null;
    }

    private WebApplication BuildApp(int port)
    {
        return BuildLocalhostApp(port, app =>
        {
            app.Map(CortanaWsEndpoints.ConversationFeedPath, context => AcceptClientAsync(context, _cts?.Token ?? CancellationToken.None));
            app.Map(ModelCapabilityProtocol.Path, context => AcceptModelCapabilityClientAsync(context, _cts?.Token ?? CancellationToken.None));
        });
    }

    private Task DisposeAppAsync(CancellationToken cancellationToken = default)
    {
        return DisposeAppAsync(
            _app,
            () => _app = null,
            ex => logger.LogDebug(ex, "停止 Conversation Feed Kestrel Host 时出现可忽略异常"),
            cancellationToken);
    }

    /// <summary>
    /// 向所有已订阅 conversation-feed 的客户端广播一条消息。
    /// </summary>
    /// <param name="message">要广播的 JSON 文本消息。</param>
    /// <param name="cancellationToken">取消发送操作的令牌。</param>
    /// <returns>表示广播过程的任务。</returns>
    public async Task BroadcastConversationFeedAsync(string message, CancellationToken cancellationToken = default)
    {
        var tasks = _subscriptions.Keys
            .Select(clientId => SendAsync(clientId, message, cancellationToken));

        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    /// <summary>
    /// 通过独立 conversation-feed 连接向 Memory 插件请求长期记忆供应包。
    /// </summary>
    public async Task<MemoryContextSupplyPackage?> SupplyAsync(
        MemoryContextSupplyRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (_feedConnections.Count == 0)
        {
            logger.LogDebug("长期记忆供应请求跳过：没有已连接的 conversation-feed 客户端。RequestId={RequestId}", request.RequestId);
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
            foreach (var clientId in _feedConnections.ClientIds)
            {
                await SendAsync(clientId, payload, cancellationToken).ConfigureAwait(false);
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

    /// <summary>
    /// 接受 conversation-feed 客户端连接，注册连接状态并发送 connected 控制消息。
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
        var connection = _feedConnections.Add(id, ws, "conversation-feed", ct);
        _subscriptions[id] = 0;

        var welcome = JsonSerializer.Serialize(new ConversationFeedControlMessage
        {
            Type = "connected",
            ClientId = id,
            Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
            Version = CortanaWsEndpoints.ConversationFeedVersion,
            Topics = [CortanaWsEndpoints.ConversationTopic]
        }, WebSocketJsonContext.Default.ConversationFeedControlMessage);
        await SendAsync(id, welcome, ct);
        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (clientId, token) => SendAsync(clientId, WebSocketHeartbeatLoop.CreateFeedPingPayload(clientId, WebSocketHeartbeatLoop.CreateTimestamp()), token),
            clientId => CloseFeedClientAsync(clientId)).StartAsync(connection, ct);
        await ReceiveLoopAsync(id, ws, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 接受宿主模型能力客户端连接，注册连接状态并发送 connected 消息。
    /// </summary>
    /// <param name="http">当前 HTTP 上下文。</param>
    /// <param name="ct">取消握手或发送欢迎消息的令牌。</param>
    /// <returns>表示客户端接入过程的任务。</returns>
    private async Task AcceptModelCapabilityClientAsync(HttpContext http, CancellationToken ct)
    {
        if (!http.WebSockets.IsWebSocketRequest)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var id = Guid.NewGuid().ToString("N");
        var ws = await http.WebSockets.AcceptWebSocketAsync().ConfigureAwait(false);
        var connection = _modelCapabilityConnections.Add(id, ws, "model-capability", ct);

        var welcome = JsonSerializer.Serialize(new ModelCapabilityConnectedMessage
        {
            ClientId = id
        }, WebSocketJsonContext.Default.ModelCapabilityConnectedMessage);
        await SendModelCapabilityAsync(id, welcome, ct);
        _ = new WebSocketHeartbeatLoop(
            HeartbeatInterval,
            HeartbeatTimeout,
            (clientId, token) => SendModelCapabilityAsync(clientId, WebSocketHeartbeatLoop.CreateModelCapabilityPingPayload(clientId, WebSocketHeartbeatLoop.CreateTimestamp()), token),
            clientId => CloseModelCapabilityAsync(clientId)).StartAsync(connection, ct);

        await ReceiveModelCapabilityLoopAsync(id, ws, ct).ConfigureAwait(false);
    }

    private void MarkPongReceived(string id, string channel)
    {
        switch (channel)
        {
            case "conversation-feed":
                _feedConnections.MarkPongReceived(id);
                break;
            case "model-capability":
                _modelCapabilityConnections.MarkPongReceived(id);
                break;
        }
    }

    /// <summary>
    /// 持续接收 conversation-feed 客户端消息，并在连接关闭时清理客户端资源。
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
            logger.LogDebug("Feed 客户端未完成关闭握手即断开：{Client}", id);
        }
        catch (WebSocketException ex) { logger.LogWarning(ex, "Feed 通信异常 {Client}", id); }
        finally { await CloseFeedClientAsync(id); }
    }

    /// <summary>
    /// 持续接收模型能力客户端消息，并在连接关闭时清理模型能力连接资源。
    /// </summary>
    /// <param name="id">模型能力客户端连接标识。</param>
    /// <param name="ws">模型能力客户端 WebSocket 连接。</param>
    /// <param name="ct">取消接收循环的令牌。</param>
    /// <returns>表示接收循环的任务。</returns>
    private async Task ReceiveModelCapabilityLoopAsync(string id, WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[16 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var text = await ReadTextMessageAsync(ws, buf, ct);
                if (text is null) break;
                await HandleModelCapabilityMessageAsync(id, text, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException ex) when (IsRemoteCloseWithoutHandshake(ex))
        {
            logger.LogDebug("Model capability 客户端未完成关闭握手即断开：{Client}", id);
        }
        catch (WebSocketException ex) { logger.LogWarning(ex, "Model capability 通信异常 {Client}", id); }
        finally { await CloseModelCapabilityAsync(id).ConfigureAwait(false); }
    }

    /// <summary>
    /// 处理模型能力调用请求，将请求转发给模型能力服务并返回统一响应。
    /// </summary>
    /// <param name="id">模型能力客户端连接标识。</param>
    /// <param name="json">客户端发送的请求 JSON。</param>
    /// <param name="ct">取消处理和响应发送的令牌。</param>
    /// <returns>表示请求处理过程的任务。</returns>
    private async Task HandleModelCapabilityMessageAsync(string id, string json, CancellationToken ct)
    {
        ModelCapabilityRequest? request = null;
        try
        {
            using (var doc = JsonDocument.Parse(json))
            {
                if (doc.RootElement.TryGetProperty("type", out var typeElement)
                    && string.Equals(typeElement.GetString(), "pong", StringComparison.Ordinal))
                {
                    MarkPongReceived(id, "model-capability");
                    return;
                }
            }

            request = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.ModelCapabilityRequest);
            if (request is null)
            {
                await SendModelCapabilityErrorAsync(id, null, "INVALID_REQUEST", "请求内容为空。", false, ct);
                return;
            }

            var response = await modelCapabilityService.InvokeAsync(request, ct);
            var payload = JsonSerializer.Serialize(response, WebSocketJsonContext.Default.ModelCapabilityResponse);
            await SendModelCapabilityAsync(id, payload, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            await SendModelCapabilityErrorAsync(id, request, "UNAUTHORIZED_CAPABILITY", ex.Message, false, ct);
        }
        catch (TimeoutException ex)
        {
            await SendModelCapabilityErrorAsync(id, request, "TIMEOUT", ex.Message, true, ct);
        }
        catch (InvalidOperationException ex)
        {
            await SendModelCapabilityErrorAsync(id, request, "MODEL_NOT_CONFIGURED", ex.Message, false, ct);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理 model-capability 请求失败：{Json}", json);
            await SendModelCapabilityErrorAsync(id, request, "INTERNAL_ERROR", ex.Message, true, ct);
        }
    }

    /// <summary>
    /// 处理 conversation-feed 控制消息，包括订阅与历史回放请求。
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
                MarkPongReceived(id, "conversation-feed");
                return;
            }

            if (string.Equals(type, "subscribe", StringComparison.Ordinal))
            {
                var protocol = root.TryGetProperty("protocol", out var p) ? p.GetString() ?? string.Empty : string.Empty;
                if (!string.Equals(protocol, CortanaWsEndpoints.ConversationFeedProtocol, StringComparison.Ordinal))
                {
                    await SendErrorAsync(id, "protocol 不匹配", ct); return;
                }
                _subscriptions[id] = 0;
                var ack = JsonSerializer.Serialize(new ConversationFeedControlMessage
                {
                    Type = "subscribed",
                    ClientId = id,
                    Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                    Version = CortanaWsEndpoints.ConversationFeedVersion,
                    Topics = [CortanaWsEndpoints.ConversationTopic]
                }, WebSocketJsonContext.Default.ConversationFeedControlMessage);
                await SendAsync(id, ack, ct); return;
            }
            if (string.Equals(type, "replay", StringComparison.Ordinal))
            {
                var since = root.TryGetProperty("sinceTimestamp", out var s) ? s.GetInt64() : 0L;
                var batch = root.TryGetProperty("batchSize", out var b) ? Math.Clamp(b.GetInt32(), 100, 2000) : 500;
                await SendReplayBatchesAsync(id, since, batch, ct); return;
            }
            if (string.Equals(type, "response", StringComparison.Ordinal)
                && string.Equals(root.TryGetProperty("op", out var opEl) ? opEl.GetString() : string.Empty, MemoryContextSupplyProtocol.SupplyPackageOperation, StringComparison.Ordinal))
            {
                HandleMemorySupplyResponse(json);
                return;
            }
            if (string.Equals(type, "error", StringComparison.Ordinal)
                && string.Equals(root.TryGetProperty("op", out var errorOpEl) ? errorOpEl.GetString() : string.Empty, MemoryContextSupplyProtocol.SupplyErrorOperation, StringComparison.Ordinal))
            {
                HandleMemorySupplyResponse(json);
                return;
            }
            logger.LogWarning("Feed 收到未知消息类型：{Type}", type);
        }
        catch (Exception ex) { logger.LogWarning(ex, "Feed 消息解析失败：{Json}", json); }
    }

    private void HandleMemorySupplyResponse(string json)
    {
        try
        {
            var package = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyPackage);
            if (package is not null && !string.IsNullOrWhiteSpace(package.RequestId))
            {
                CompleteMemorySupplyPackage(package);
                return;
            }

            var error = JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.MemoryContextSupplyError);
            if (error is not null && !string.IsNullOrWhiteSpace(error.RequestId))
            {
                CompleteMemorySupplyError(error);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "处理长期记忆供应响应失败");
        }
    }

    private void CompleteMemorySupplyPackage(MemoryContextSupplyPackage package)
    {
        if (_memorySupplyPendingRequests.TryRemove(package.RequestId, out var pending))
        {
            pending.TrySetResult(package);
        }
    }

    private void CompleteMemorySupplyError(MemoryContextSupplyError error)
    {
        logger.LogDebug(
            "长期记忆供应返回错误：RequestId={RequestId}, Code={Code}, Message={Message}, Retryable={Retryable}",
            error.RequestId,
            error.Code,
            error.Message,
            error.Retryable);

        if (_memorySupplyPendingRequests.TryRemove(error.RequestId!, out var pending))
        {
            pending.TrySetResult(null);
        }
    }

    /// <summary>
    /// 按时间戳分页回放会话消息，并以 conversation.export.batch 事件批量发送给客户端。
    /// </summary>
    /// <param name="clientId">接收回放数据的客户端标识。</param>
    /// <param name="sinceTimestamp">回放起始时间戳。</param>
    /// <param name="batchSize">每批最多回放的消息数量。</param>
    /// <param name="ct">取消查询或发送的令牌。</param>
    /// <returns>表示回放发送过程的任务。</returns>
    private async Task SendReplayBatchesAsync(string clientId, long sinceTimestamp, int batchSize, CancellationToken ct)
    {
        try
        {
            var total = 0; var last = sinceTimestamp;
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
                    cmd => cmd.Parameters.AddWithValue("@Since", last));
                if (rows.Count == 0)
                {
                    var completed = JsonSerializer.Serialize(new ConversationFeedEventMessage
                    {
                        Type = "event",
                        Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                        Version = CortanaWsEndpoints.ConversationFeedVersion,
                        Topic = CortanaWsEndpoints.ConversationTopic,
                        EventType = "conversation.export.completed",
                        Payload = JsonDocument.Parse($"{{\"total\":{total}}}").RootElement
                    }, WebSocketJsonContext.Default.ConversationFeedEventMessage);
                    await SendAsync(clientId, completed, ct); break;
                }
                total += rows.Count; last = rows[^1].CreatedTimestamp + 1;
                var batch = new ConversationExportBatch { BatchId = Guid.NewGuid().ToString("N"), HasMore = true, Items = rows.ToArray() };
                var payload = JsonSerializer.SerializeToElement(batch, WebSocketJsonContext.Default.ConversationExportBatch);
                var msg = JsonSerializer.Serialize(new ConversationFeedEventMessage
                {
                    Type = "event",
                    Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
                    Version = CortanaWsEndpoints.ConversationFeedVersion,
                    Topic = CortanaWsEndpoints.ConversationTopic,
                    EventType = "conversation.export.batch",
                    Payload = payload
                }, WebSocketJsonContext.Default.ConversationFeedEventMessage);
                await SendAsync(clientId, msg, ct);
            }
            logger.LogInformation("Conversation replay 完成：Since={Since}, Total={Total}", sinceTimestamp, total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conversation replay 失败");
            await SendErrorAsync(clientId, $"replay failed: {ex.Message}", ct);
        }
    }

    /// <summary>
    /// 向指定 conversation-feed 客户端串行发送文本消息。
    /// </summary>
    /// <param name="id">目标客户端连接标识。</param>
    /// <param name="text">要发送的文本消息。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private async Task SendAsync(string id, string text, CancellationToken ct)
    {
        await _feedConnections.SendAsync(id, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 向指定模型能力客户端串行发送文本消息。
    /// </summary>
    /// <param name="id">目标模型能力客户端连接标识。</param>
    /// <param name="text">要发送的文本消息。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private async Task SendModelCapabilityAsync(string id, string text, CancellationToken ct)
    {
        await _modelCapabilityConnections.SendAsync(id, text, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 向 conversation-feed 客户端发送统一的错误控制消息。
    /// </summary>
    /// <param name="id">目标客户端连接标识。</param>
    /// <param name="message">错误说明。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private Task SendErrorAsync(string id, string message, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new ConversationFeedControlMessage
        {
            Type = "error",
            ClientId = id,
            Protocol = CortanaWsEndpoints.ConversationFeedProtocol,
            Version = CortanaWsEndpoints.ConversationFeedVersion,
            Message = message
        }, WebSocketJsonContext.Default.ConversationFeedControlMessage);
        return SendAsync(id, payload, ct);
    }

    /// <summary>
    /// 向模型能力客户端发送统一的失败响应。
    /// </summary>
    /// <param name="id">目标模型能力客户端连接标识。</param>
    /// <param name="request">原始模型能力请求；解析失败时可为空。</param>
    /// <param name="code">错误码。</param>
    /// <param name="message">错误说明。</param>
    /// <param name="retryable">指示该错误是否可重试。</param>
    /// <param name="ct">取消发送操作的令牌。</param>
    /// <returns>表示发送过程的任务。</returns>
    private Task SendModelCapabilityErrorAsync(
        string id,
        ModelCapabilityRequest? request,
        string code,
        string message,
        bool retryable,
        CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new ModelCapabilityResponse
        {
            RequestId = request?.RequestId ?? string.Empty,
            Success = false,
            ErrorCode = code,
            ErrorMessage = message
        }, WebSocketJsonContext.Default.ModelCapabilityResponse);

        return SendModelCapabilityAsync(id, payload, ct);
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
    /// 从 feed 客户端集合中移除连接，并关闭释放对应 WebSocket 与发送锁。
    /// </summary>
    private async Task CloseFeedClientAsync(string id)
    {
        _subscriptions.TryRemove(id, out _);
        await _feedConnections.RemoveAndCloseAsync(id).ConfigureAwait(false);
    }

    /// <summary>
    /// 从模型能力客户端集合中移除连接，并关闭释放对应 WebSocket 与发送锁。
    /// </summary>
    /// <param name="id">模型能力客户端连接标识。</param>
    /// <param name="ws">要关闭的 WebSocket 连接。</param>
    /// <returns>表示关闭过程的任务。</returns>
    private async Task CloseModelCapabilityAsync(string id)
    {
        await _modelCapabilityConnections.RemoveAndCloseAsync(id).ConfigureAwait(false);
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
            _memorySupplyPendingRequests.Clear();
            _lifecycleLock.Dispose();
        }
    }
}
