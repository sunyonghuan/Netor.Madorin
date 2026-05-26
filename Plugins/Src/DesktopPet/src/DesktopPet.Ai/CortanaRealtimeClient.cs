using DesktopPet.Behaviors;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace DesktopPet.Ai;

public sealed class CortanaRealtimeClient : IAsyncDisposable
{
    private readonly ClientWebSocket _webSocket = new();
    private readonly PetRealtimeEventMapper _mapper;
    private readonly Uri _uri;
    private string? _clientId;

    public CortanaRealtimeClient(Uri uri, PetRealtimeEventMapper? mapper = null)
    {
        _uri = uri;
        _mapper = mapper ?? new PetRealtimeEventMapper();
    }

    public string? ClientId => _clientId;

    /// <summary>
    /// 默认连接到 Cortana 宿主 PluginBus 端点。
    /// 路径固定为 /internal（PluginBus v1.2.0 协议端点）。
    /// </summary>
    public static Uri CreateDefaultUri(string host = "localhost", int port = 52841)
    {
        return new Uri($"ws://{host}:{port}/internal");
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);

        // 服务端连接后会主动推送 connected 控制帧
        var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        if (message is not null && string.Equals(message.Type, "connected", StringComparison.Ordinal))
        {
            _clientId = message.ClientId;
        }

        // PluginBus 协议要求：连接后必须发 subscribe 帧才能收到广播事件
        await SendSubscribeAsync(cancellationToken).ConfigureAwait(false);

        // 等待服务端回复 subscribed 确认帧（忽略超时，不阻塞）
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            var subscribed = await ReceiveMessageAsync(cts.Token).ConfigureAwait(false);
            _ = subscribed; // 只等不处理，连接已建立
        }
        catch (OperationCanceledException) { }
    }

    public async Task SendAsync(
        string text,
        IReadOnlyList<CortanaWsAttachment>? attachments = null,
        CancellationToken cancellationToken = default)
    {
        var message = new CortanaWsClientMessage
        {
            Type = "send",
            Data = text,
            Attachments = attachments
        };

        await SendMessageAsync(message, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        return SendMessageAsync(new CortanaWsClientMessage { Type = "stop" }, cancellationToken);
    }

    public async IAsyncEnumerable<PetEvent> ReadPetEventsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested && _webSocket.State == WebSocketState.Open)
        {
            var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
            if (message is null)
            {
                yield break;
            }

            var petEvent = _mapper.Map(message);
            if (petEvent is not null)
            {
                yield return petEvent;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_webSocket.State == WebSocketState.Open)
        {
            await _webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
        }

        _webSocket.Dispose();
    }

    /// <summary>
    /// 发送 PluginBus subscribe 帧，订阅 conversation topic 以接收 AI 广播事件。
    /// </summary>
    private async Task SendSubscribeAsync(CancellationToken cancellationToken)
    {
        var json = """
            {
              "type": "subscribe",
              "protocol": "cortana.plugin-bus",
              "version": "1.2.0",
              "topics": ["conversation"]
            }
            """;
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendMessageAsync(CortanaWsClientMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, CortanaWsJsonContext.Default.CortanaWsClientMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 接收一条消息并解析。
    /// 兼容两种格式：
    ///   1. PluginBus 事件帧：{ "type":"event", ..., "payload": { "type":"tts_started", "data":"..." } }
    ///      → 从 payload 取内层消息
    ///   2. 控制帧：{ "type":"connected"/"subscribed"/"error", "clientId":"...", ... }
    ///      → 直接映射为 CortanaWsServerMessage
    /// </summary>
    private async Task<CortanaWsServerMessage?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _webSocket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            stream.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        var bytes = stream.ToArray();

        using var doc = JsonDocument.Parse(bytes);
        var root = doc.RootElement;

        var type = root.TryGetProperty("type", out var typeProp) ? typeProp.GetString() ?? string.Empty : string.Empty;

        // PluginBus 事件帧：type="event"，真正的消息在 payload 里
        if (string.Equals(type, "event", StringComparison.Ordinal))
        {
            if (root.TryGetProperty("payload", out var payload) && payload.ValueKind == JsonValueKind.Object)
            {
                var innerType = payload.TryGetProperty("type", out var it) ? it.GetString() : null;
                var innerData = payload.TryGetProperty("data", out var id) ? id.GetString() : null;
                var clientId  = payload.TryGetProperty("clientId", out var cid) ? cid.GetString() : null;
                var sessionId = payload.TryGetProperty("sessionId", out var sid) ? sid.GetString() : null;
                return new CortanaWsServerMessage
                {
                    Type      = innerType ?? string.Empty,
                    Data      = innerData,
                    ClientId  = clientId,
                    SessionId = sessionId,
                };
            }
            return null;
        }

        // 控制帧（connected / subscribed / error / pong）直接解析
        return new CortanaWsServerMessage
        {
            Type      = type,
            Data      = root.TryGetProperty("data", out var d) ? d.GetString() : null,
            ClientId  = root.TryGetProperty("clientId", out var c) ? c.GetString() : null,
            SessionId = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null,
        };
    }
}
