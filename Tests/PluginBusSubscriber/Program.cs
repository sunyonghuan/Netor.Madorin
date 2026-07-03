using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

var endpoint = args.Length > 0 ? args[0] : "ws://localhost:12841/internal";
var topic = args.Length > 1 ? args[1] : "demo";
var op = args.Length > 2 ? args[2] : "demo.test.message.v1";
var subscribeDelaySeconds = args.Length > 3 && int.TryParse(args[3], out var d) ? d : 3;

string Now() => DateTimeOffset.Now.ToString("HH:mm:ss.fff");
void Log(string msg) => Console.WriteLine($"[{Now()}] [Subscriber] {msg}");

Log($"endpoint={endpoint}");
Log($"topic={topic}, op={op}, 订阅前等待 {subscribeDelaySeconds}s");

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

var receiveTask = Task.Run(async () =>
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
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    Log("服务端关闭连接");
                    return;
                }
                ms.Write(buf, 0, r.Count);
            }
            while (!r.EndOfMessage);

            var text = Encoding.UTF8.GetString(ms.ToArray());
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            var opStr = root.TryGetProperty("op", out var o) ? o.GetString() : null;

            if (type == "ping")
            {
                await SendAsync(ws, """{"type":"pong"}""", cts.Token);
                continue;
            }

            if (type == "connected")
            {
                Log("收到 connected 帧");
                continue;
            }

            if (type == "event" && opStr == op)
            {
                var payload = root.TryGetProperty("payload", out var p) ? p.GetRawText() : "<none>";
                var src = root.TryGetProperty("sourcePluginId", out var s) ? s.GetString() : null;
                Log($"✓ 收到事件 op={opStr} sourcePluginId={src ?? "<none>"} payload={payload}");
                continue;
            }

            if (type == "event")
            {
                Log($"event 但 op 不匹配：{opStr}");
                continue;
            }

            Log($"其他帧 type={type} op={opStr}");
        }
    }
    catch (OperationCanceledException) { }
    catch (Exception ex)
    {
        Log($"接收循环异常：{ex.Message}");
    }
});

try
{
    await Task.Delay(TimeSpan.FromSeconds(subscribeDelaySeconds), cts.Token);
}
catch (OperationCanceledException) { return 0; }

var subFrame = JsonSerializer.Serialize(new
{
    type = "subscribe",
    protocol = "cortana.plugin-bus",
    version = "1.4.0",
    topics = new[] { topic },
    subscribedOps = new[] { op }
});
await SendAsync(ws, subFrame, cts.Token);
Log($"已发送 subscribe 帧：{subFrame}");
Log("等待事件… (Ctrl+C 退出)");

await receiveTask;
return 0;

static async Task SendAsync(ClientWebSocket ws, string json, CancellationToken ct)
    => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
