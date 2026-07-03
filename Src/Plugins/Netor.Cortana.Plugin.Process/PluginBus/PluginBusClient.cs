using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Netor.Cortana.Plugin.PluginBus;

/// <summary>
/// PluginBus 1.4.0 共享客户端 SDK：信封式 type=event 发布 + op 级订阅。
/// JsonElement-only 严格 AOT，不依赖反射，载荷由调用方自带 JsonSerializerContext 序列化。
/// 详见 docs/已完成功能规划/插件事件广播/README.md §3.6.2。
/// </summary>
public sealed class PluginBusClient : IHostedService, IAsyncDisposable
{
    private static readonly byte[] PongFrame = Encoding.UTF8.GetBytes("""{"type":"pong"}""");

    private readonly PluginSettings _settings;
    private readonly ILogger<PluginBusClient> _logger;
    private readonly string? _sourcePluginId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Func<JsonElement, Task>> _handlers = new(StringComparer.Ordinal);
    private ClientWebSocket? _socket;
    private CancellationTokenSource? _receiveCts;
    private Task? _receiveTask;
    private bool _disposed;

    /// <summary>构造客户端；不设置 sourcePluginId（事件帧不带该字段）。</summary>
    public PluginBusClient(PluginSettings settings, ILogger<PluginBusClient>? logger = null)
        : this(settings, sourcePluginId: null, logger)
    {
    }

    /// <summary>构造客户端并指定事件帧的 sourcePluginId（推荐传 plugin.json 的 id）。</summary>
    public PluginBusClient(PluginSettings settings, string? sourcePluginId, ILogger<PluginBusClient>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = settings;
        _sourcePluginId = string.IsNullOrWhiteSpace(sourcePluginId) ? null : sourcePluginId;
        _logger = logger ?? NullLogger<PluginBusClient>.Instance;
    }

    /// <summary>IHostedService.StartAsync 占位；实际连接按需懒建立，跟随首个 Publish/Subscribe 调用。</summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>IHostedService.StopAsync：关闭底层 WebSocket 连接和接收循环。</summary>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await CloseAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// 发布 PluginBus 事件。空 op 直接抛 ArgumentException；连接缺失时静默跳过（与历史行为一致）。
    /// payload 可为 default(JsonElement)，此时按 null 落帧。
    /// </summary>
    public async Task PublishEventAsync(string op, JsonElement payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);
        ObjectDisposedException.ThrowIf(_disposed, this);

        try
        {
            var socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (socket is null)
            {
                return;
            }

            var envelope = new PluginBusEventEnvelope
            {
                Type = "event",
                Op = op,
                SourcePluginId = _sourcePluginId,
                Payload = payload
            };

            var json = JsonSerializer.Serialize(envelope, PluginBusClientJsonContext.Default.PluginBusEventEnvelope);
            await SendRawAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PluginBus 事件发布失败：{Op}", op);
            await CloseAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// 订阅指定 op 的事件。重复订阅同一 op 会覆盖旧 handler。
    /// 首次调用时向宿主发 subscribe 帧（topic 取 op 首段，subscribedOps 即所有已注册 op）。
    /// handler 在后台接收循环里按串行调用，handler 内部抛异常仅记录日志，不终止接收。
    /// </summary>
    public async Task SubscribeEventAsync(string op, Func<JsonElement, Task> handler, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(op);
        ArgumentNullException.ThrowIfNull(handler);
        ObjectDisposedException.ThrowIf(_disposed, this);

        _handlers[op] = handler;
        await PushSubscribeFrameAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task PushSubscribeFrameAsync(CancellationToken cancellationToken)
    {
        var ops = _handlers.Keys.ToArray();
        if (ops.Length == 0)
        {
            return;
        }

        var topics = ops
            .Select(static op => op.Split('.', 2)[0])
            .Where(static seg => !string.IsNullOrEmpty(seg))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        try
        {
            var socket = await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
            if (socket is null)
            {
                return;
            }

            var frame = new PluginBusSubscribeFrame
            {
                Topics = topics,
                SubscribedOps = ops
            };

            var json = JsonSerializer.Serialize(frame, PluginBusClientJsonContext.Default.PluginBusSubscribeFrame);
            await SendRawAsync(json, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "PluginBus 订阅帧发送失败");
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task<ClientWebSocket?> EnsureConnectedAsync(CancellationToken cancellationToken)
    {
        if (_socket is { State: WebSocketState.Open } socket)
        {
            return socket;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket is { State: WebSocketState.Open } existing)
            {
                return existing;
            }

            var endpoint = ResolveEndpoint();
            if (string.IsNullOrWhiteSpace(endpoint))
            {
                _logger.LogDebug("PluginBus Endpoint 缺失，跳过事件 IO。");
                return null;
            }

            await CloseAsync().ConfigureAwait(false);

            socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri(endpoint), cancellationToken).ConfigureAwait(false);
            _socket = socket;
            StartReceiveLoop(socket);

            _logger.LogInformation("PluginBus 已连接：{Endpoint}", endpoint);
            return socket;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private string ResolveEndpoint()
    {
        if (_settings.Extensions.TryGetValue("pluginBusEndpoint", out var endpoint)
            && !string.IsNullOrWhiteSpace(endpoint))
        {
            return endpoint;
        }

        return _settings.WsPort > 0
            ? $"ws://localhost:{_settings.WsPort}/internal"
            : string.Empty;
    }

    private async Task SendRawAsync(string payload, CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                Encoding.UTF8.GetBytes(payload),
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task SendPongAsync(CancellationToken cancellationToken)
    {
        var socket = _socket;
        if (socket is null || socket.State != WebSocketState.Open)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(
                PongFrame,
                WebSocketMessageType.Text,
                true,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private void StartReceiveLoop(ClientWebSocket socket)
    {
        _receiveCts?.Cancel();
        _receiveCts?.Dispose();
        _receiveCts = new CancellationTokenSource();
        var token = _receiveCts.Token;
        _receiveTask = Task.Run(() => ReceiveLoopAsync(socket, token), CancellationToken.None);
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        try
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        await CloseAsync().ConfigureAwait(false);
                        return;
                    }

                    if (result.Count > 0)
                    {
                        message.Write(buffer, 0, result.Count);
                    }
                }
                while (!result.EndOfMessage);

                await DispatchAsync(message.ToArray(), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PluginBus 接收循环结束。");
            await CloseAsync().ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(byte[] frameBytes, CancellationToken cancellationToken)
    {
        PluginBusInboundFrame? frame;
        try
        {
            frame = JsonSerializer.Deserialize(frameBytes, PluginBusClientJsonContext.Default.PluginBusInboundFrame);
        }
        catch (JsonException ex)
        {
            _logger.LogDebug(ex, "PluginBus 入帧解析失败，已丢弃。");
            return;
        }

        if (frame is null || string.IsNullOrEmpty(frame.Type))
        {
            return;
        }

        if (string.Equals(frame.Type, "ping", StringComparison.Ordinal))
        {
            await SendPongAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(frame.Type, "event", StringComparison.Ordinal))
        {
            return;
        }

        var op = frame.Op;
        if (string.IsNullOrEmpty(op))
        {
            return;
        }

        if (!_handlers.TryGetValue(op, out var handler))
        {
            return;
        }

        try
        {
            await handler(frame.Payload).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PluginBus 订阅 handler 抛出异常：{Op}", op);
        }
    }

    private async Task CloseAsync()
    {
        var cts = _receiveCts;
        _receiveCts = null;
        _receiveTask = null;
        try { cts?.Cancel(); } catch { /* ignore */ }
        cts?.Dispose();

        var socket = _socket;
        _socket = null;
        if (socket is null)
        {
            return;
        }

        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    /// <summary>关闭连接并释放同步原语；幂等。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await CloseAsync().ConfigureAwait(false);
        _sendLock.Dispose();
        _connectLock.Dispose();
    }
}
