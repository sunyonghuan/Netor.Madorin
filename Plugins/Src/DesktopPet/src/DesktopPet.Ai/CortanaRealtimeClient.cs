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

    public static Uri CreateDefaultUri(string host = "localhost", int port = 52841)
    {
        return new Uri($"ws://{host}:{port}/ws/");
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _webSocket.ConnectAsync(_uri, cancellationToken).ConfigureAwait(false);

        var message = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
        if (message is not null && string.Equals(message.Type, "connected", StringComparison.Ordinal))
        {
            _clientId = message.ClientId;
        }
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

    private async Task SendMessageAsync(CortanaWsClientMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, CortanaWsJsonContext.Default.CortanaWsClientMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

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

        return JsonSerializer.Deserialize(stream.ToArray(), CortanaWsJsonContext.Default.CortanaWsServerMessage);
    }
}
