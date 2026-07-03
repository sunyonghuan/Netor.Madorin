param(
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$ProjectName = 'CortanaPluginBusClientSample',
    [int]$PluginBusPort = 52841
)

$ErrorActionPreference = 'Stop'

$fullOutputDir = [System.IO.Path]::GetFullPath($OutputDir)
if (Test-Path $fullOutputDir) {
    throw "输出目录已存在：$fullOutputDir"
}

New-Item -ItemType Directory -Path $fullOutputDir -Force | Out-Null

$csproj = @"
<Project Sdk=""Microsoft.NET.Sdk"">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
</Project>
"@

$program = @"
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

var endpoint = args.Length > 0 ? args[0] : "ws://localhost:$PluginBusPort/internal";

await using var client = new CortanaPluginBusClient(endpoint);
await client.ConnectAsync(CancellationToken.None);
await client.SubscribeAsync(["conversation"], CancellationToken.None);

await foreach (var token in client.SendAndStreamAsync("text", CancellationToken.None))
{
    Console.Write(token);
}

public sealed class CortanaPluginBusClient(string endpoint) : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Uri _endpoint = new(endpoint, UriKind.Absolute);

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(_endpoint, cancellationToken);
        while (true)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (message is null) throw new InvalidOperationException("连接在握手前关闭。");
            if (message.Type == "ping") { await SendAsync(new PluginBusMessage { Type = "pong" }, cancellationToken); continue; }
            if (message.Type == "connected") return;
        }
    }

    public Task SubscribeAsync(string[] topics, CancellationToken cancellationToken)
        => SendAsync(new PluginBusMessage { Type = "subscribe", Protocol = "cortana.plugin-bus", Version = "1.4.0", Topics = topics }, cancellationToken);

    public async IAsyncEnumerable<string> SendAndStreamAsync(string text, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await SendAsync(new PluginBusMessage
        {
            Type = "request",
            Protocol = "cortana.plugin-bus",
            Version = "1.4.0",
            Topic = "conversation",
            Op = "conversation.chat.message.send",
            Payload = JsonSerializer.SerializeToElement(new ChatPayload { Type = "send", Data = text }, JsonContext.Default.ChatPayload)
        }, cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var message = await ReceiveAsync(cancellationToken);
            if (message is null) yield break;
            if (message.Type == "ping") { await SendAsync(new PluginBusMessage { Type = "pong" }, cancellationToken); continue; }
            if (message.Type != "event" || message.Payload.ValueKind != JsonValueKind.Object) continue;

            var payload = message.Payload.Deserialize(JsonContext.Default.ChatPayload);
            if (payload?.Type == "token" && payload.Data is not null) { yield return payload.Data; continue; }
            if (payload?.Type == "done") yield break;
            if (payload?.Type == "error") throw new InvalidOperationException(payload.Data ?? "error");
        }
    }

    private async Task SendAsync(PluginBusMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonContext.Default.PluginBusMessage);
        await _socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, cancellationToken);
    }

    private async Task<PluginBusMessage?> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[8192];
        using var stream = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            stream.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        return JsonSerializer.Deserialize(stream.ToArray(), JsonContext.Default.PluginBusMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
            await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        _socket.Dispose();
    }
}

public sealed record PluginBusMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("protocol")] public string? Protocol { get; init; }
    [JsonPropertyName("version")] public string? Version { get; init; }
    [JsonPropertyName("topic")] public string? Topic { get; init; }
    [JsonPropertyName("op")] public string? Op { get; init; }
    [JsonPropertyName("topics")] public string[]? Topics { get; init; }
    [JsonPropertyName("payload")] public JsonElement Payload { get; init; }
}

public sealed record ChatPayload
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }
}

[JsonSerializable(typeof(PluginBusMessage))]
[JsonSerializable(typeof(ChatPayload))]
[JsonSerializable(typeof(string[]))]
+[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class JsonContext : JsonSerializerContext;
"@

Set-Content -Path (Join-Path $fullOutputDir "$ProjectName.csproj") -Value $csproj -Encoding UTF8
Set-Content -Path (Join-Path $fullOutputDir 'Program.cs') -Value $program -Encoding UTF8

Write-Host "已生成 PluginBus WebSocket 客户端样板：$fullOutputDir" -ForegroundColor Green
