using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var endpoint = args.Length > 0 ? args[0] : "ws://localhost:12841/internal";
var op = args.Length > 1 ? args[1] : "demo.test.message.v1";
var count = args.Length > 2 && int.TryParse(args[2], out var c) ? c : 5;
var startDelaySeconds = args.Length > 3 && int.TryParse(args[3], out var d) ? d : 5;

string Now() => DateTimeOffset.Now.ToString("HH:mm:ss.fff");
void Log(string msg) => Console.WriteLine($"[{Now()}] [Publisher] {msg}");

Log($"endpoint={endpoint}");
Log($"op={op}, count={count}, 发布前等待 {startDelaySeconds}s");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

using var ws = new ClientWebSocket();
try
{
    await ws.ConnectAsync(new Uri(endpoint), cts.Token);
    Log("WebSocket 已连接");
}
catch (Exception ex)
{
    Log($"连接失败：{ex.Message}");
    return 1;
}

_ = Task.Run(async () =>
{
    var buf = new byte[16384];
    try
    {
        while (!cts.IsCancellationRequested && ws.State == WebSocketState.Open)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult r;
            do
            {
                r = await ws.ReceiveAsync(buf, cts.Token);
                if (r.MessageType == WebSocketMessageType.Close) return;
                ms.Write(buf, 0, r.Count);
            }
            while (!r.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(text);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ping")
            {
                await SendAsync(ws, """{"type":"pong"}""", cts.Token);
            }
            else if (type == "connected")
            {
                Log("收到 connected 帧");
            }
        }
    }
    catch (OperationCanceledException) { }
    catch { }
});

try
{
    await Task.Delay(TimeSpan.FromSeconds(startDelaySeconds), cts.Token);
}
catch (OperationCanceledException) { return 0; }

Log("延迟到，开始发布事件");

for (var i = 1; i <= count; i++)
{
    if (cts.IsCancellationRequested) break;

    var payload = new
    {
        index = i,
        message = $"hello from publisher #{i}",
        sentAt = DateTimeOffset.UtcNow.ToString("O")
    };
    var frame = JsonSerializer.Serialize(new
    {
        type = "event",
        op,
        sourcePluginId = "demo_publisher",
        payload
    });
    try
    {
        await SendAsync(ws, frame, cts.Token);
        Log($"→ 已发送 #{i} payload.index={i}");
    }
    catch (Exception ex)
    {
        Log($"发送 #{i} 失败：{ex.Message}");
        break;
    }

    if (i < count)
    {
        try { await Task.Delay(1000, cts.Token); }
        catch (OperationCanceledException) { break; }
    }
}

Log("全部发布完成，1 秒后关闭连接");
try { await Task.Delay(1000, cts.Token); } catch { }
try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None); } catch { }
return 0;

static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
    => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
