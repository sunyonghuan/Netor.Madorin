using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

int port = 0;
if (args.Length < 1 || !int.TryParse(args[0], out port))
{
    Console.Error.WriteLine("Usage: FeedProbe <port>");
    Environment.Exit(2);
}

var uri = new Uri($"ws://localhost:{port}/internal/conversation-feed/");
using var ws = new ClientWebSocket();

Console.WriteLine($"Connecting to {uri}...");
await ws.ConnectAsync(uri, CancellationToken.None);

string? type = await ReadTypeAsync(ws, TimeSpan.FromSeconds(5));
Console.WriteLine($"GOT: {type}");

// subscribe
var sub = JsonSerializer.Serialize(new { type = "subscribe", topics = new[] { "conversation" }, protocol = "conversation-feed", version = "1.0.0" });
await ws.SendAsync(Encoding.UTF8.GetBytes(sub), WebSocketMessageType.Text, true, CancellationToken.None);
type = await ReadTypeAsync(ws, TimeSpan.FromSeconds(5));
Console.WriteLine($"GOT: {type}");

// request replay
var replay = JsonSerializer.Serialize(new { type = "replay", sinceTimestamp = 0, batchSize = 10 });
await ws.SendAsync(Encoding.UTF8.GetBytes(replay), WebSocketMessageType.Text, true, CancellationToken.None);

int batches = 0; bool completed = false;
for (int i = 0; i < 50 && !completed; i++)
{
    var line = await ReadRawAsync(ws, TimeSpan.FromSeconds(1));
    if (line is null) continue;
    using var doc = JsonDocument.Parse(line);
    var root = doc.RootElement;
    if (root.TryGetProperty("eventType", out var et))
    {
        var ev = et.GetString();
        if (ev == "conversation.export.batch") { batches++; }
        if (ev == "conversation.export.completed") { completed = true; }
    }
}

Console.WriteLine($"Batches={batches}, Completed={completed}");

static async Task<string?> ReadTypeAsync(ClientWebSocket ws, TimeSpan timeout)
{
    var s = await ReadRawAsync(ws, timeout);
    if (s is null) return null;
    using var doc = JsonDocument.Parse(s);
    return doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
}

static async Task<string?> ReadRawAsync(ClientWebSocket ws, TimeSpan timeout)
{
    using var cts = new CancellationTokenSource(timeout);
    var buffer = new byte[16 * 1024];
    try
    {
        var res = await ws.ReceiveAsync(buffer, cts.Token);
        if (res.MessageType == WebSocketMessageType.Close) return null;
        return Encoding.UTF8.GetString(buffer, 0, res.Count);
    }
    catch (OperationCanceledException) { return null; }
}
