---
name: websocket-integration
description: '通过 WebSocket 连接 Cortana AI 对话服务。编写 C# 客户端代码，实现发送消息、接收流式回复、附件传输、系统事件监听。触发关键词：WebSocket、WS 连接、远程对话、流式回复、AI 接入、实时通信。'
user-invocable: true
---

# WebSocket Integration

通过 WebSocket 协议连接 Cortana，实现 AI 对话交互。

---

## 协议概要

| 项目 | 值 |
|------|-----|
| 地址 | `ws://<host>:<port>/ws/` |
| 默认端口 | `52841`（可在系统设置中修改） |
| 编码 | UTF-8 JSON 文本帧 |
| 传输 | 标准 WebSocket（RFC 6455） |

连接成功后服务端立即推送 `connected` 消息，包含 `clientId`。

---

## 消息类型速查

### 客户端 → 服务端

| type | data | 说明 |
|------|------|------|
| `send` | 用户消息文本 | 发送对话消息，可附带 `attachments` |
| `stop` | — | 中止当前 AI 回复 |

### 服务端 → 客户端

| type | 字段 | 说明 |
|------|------|------|
| `connected` | `clientId` | 连接成功，分配客户端 ID |
| `token` | `data` | AI 流式回复的文本片段，逐个推送 |
| `done` | `sessionId` | 本轮回复完成 |
| `error` | `data` | 错误信息；`data="cancelled"` 表示被中止 |
| `stt_partial` | `data` | 语音识别中间结果 |
| `stt_final` | `data` | 语音识别最终结果 |
| `stt_stopped` | — | 语音识别已停止 |
| `tts_started` | — | TTS 开始播放 |
| `tts_subtitle` | `data` | TTS 字幕文本 |
| `tts_completed` | — | TTS 播放完成 |
| `chat_completed` | — | 整个对话流程结束（含 TTS） |
| `wakeword_detected` | — | 检测到唤醒词 |

### 附件格式

```json
{
  "type": "send",
  "data": "描述一下这张图",
  "attachments": [
    { "path": "C:\\Photos\\img.jpg", "name": "img.jpg", "type": "image/jpeg" }
  ]
}
```

- `path` 必须是 **Cortana 进程所在机器的本地路径**
- 远程客户端需先将文件传输到服务端

---

## C# 客户端实现指南

### AOT 兼容要求

> **所有 JSON 序列化/反序列化必须使用 Source Generator，禁止使用反射。**

不要这样写（AOT 不兼容）：

```csharp
// ❌ 匿名类型 + 默认序列化器 → AOT 运行时崩溃
var json = JsonSerializer.Serialize(new { type = "send", data = "hello" });
```

必须这样写：

```csharp
// ✅ 强类型 record + Source Generator → AOT 安全
var json = JsonSerializer.Serialize(msg, CortanaJsonContext.Default.WsClientMessage);
```

### 步骤一：定义消息类型

```csharp
using System.Text.Json.Serialization;

/// <summary>客户端发送的消息。</summary>
public sealed record WsClientMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("attachments")]
    public List<WsAttachment>? Attachments { get; init; }
}

/// <summary>服务端返回的消息。</summary>
public sealed record WsServerMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

/// <summary>附件信息。</summary>
public sealed record WsAttachment
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}
```

### 步骤二：创建 JSON Source Generator 上下文

```csharp
using System.Text.Json.Serialization;

[JsonSerializable(typeof(WsClientMessage))]
[JsonSerializable(typeof(WsServerMessage))]
[JsonSerializable(typeof(WsAttachment))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class CortanaJsonContext : JsonSerializerContext;
```

> **关键：** `JsonSerializerContext` 子类必须声明 `partial`，编译器会自动生成序列化代码。
> 不要遗漏任何需要序列化的类型。

### 步骤三：连接并交互

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

public sealed class CortanaWsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;
    private string? _clientId;

    public CortanaWsClient(string host = "localhost", int port = 52841)
    {
        _uri = new Uri($"ws://{host}:{port}/ws/");
    }

    /// <summary>建立连接并接收 clientId。</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_uri, ct);

        // 服务端立即推送 connected 消息
        var msg = await ReceiveMessageAsync(ct);
        if (msg?.Type == "connected")
            _clientId = msg.ClientId;
    }

    /// <summary>发送文本消息，流式接收回复。</summary>
    public async IAsyncEnumerable<string> SendAndStreamAsync(
        string text,
        List<WsAttachment>? attachments = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new WsClientMessage
        {
            Type = "send",
            Data = text,
            Attachments = attachments
        };

        // ✅ AOT 安全：使用 Source Generator 序列化
        var json = JsonSerializer.Serialize(request, CortanaJsonContext.Default.WsClientMessage);
        var bytes = Encoding.UTF8.GetBytes(json);
        await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);

        // 循环接收 token 直到 done 或 error
        while (!ct.IsCancellationRequested)
        {
            var msg = await ReceiveMessageAsync(ct);
            if (msg is null) yield break;

            switch (msg.Type)
            {
                case "token":
                    if (msg.Data is not null)
                        yield return msg.Data;
                    break;

                case "done":
                    yield break;

                case "error":
                    throw new InvalidOperationException(msg.Data ?? "未知错误");
            }
            // 忽略 tts_*, stt_*, chat_completed 等广播事件
        }
    }

    /// <summary>发送中止指令。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var request = new WsClientMessage { Type = "stop" };
        var json = JsonSerializer.Serialize(request, CortanaJsonContext.Default.WsClientMessage);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>接收并反序列化一条消息。</summary>
    private async Task<WsServerMessage?> ReceiveMessageAsync(CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();

        WebSocketReceiveResult result;
        do
        {
            result = await _ws.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);

        // ✅ AOT 安全：使用 Source Generator 反序列化
        return JsonSerializer.Deserialize(
            ms.ToArray(),
            CortanaJsonContext.Default.WsServerMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        _ws.Dispose();
    }
}
```

### 步骤四：使用示例

```csharp
await using var client = new CortanaWsClient("localhost", 52841);
await client.ConnectAsync();

// 流式打印 AI 回复
await foreach (var token in client.SendAndStreamAsync("帮我写一首七言绝句"))
{
    Console.Write(token);
}
Console.WriteLine();

// 带附件的消息
var attachments = new List<WsAttachment>
{
    new() { Path = @"C:\Photos\cat.jpg", Name = "cat.jpg", Type = "image/jpeg" }
};
await foreach (var token in client.SendAndStreamAsync("描述这张图片", attachments))
{
    Console.Write(token);
}

// 中止生成
await client.StopAsync();
```

---

## 完整 csproj 参考（AOT 发布）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
</Project>
```

> `JsonSerializerIsReflectionEnabledByDefault=false` 可以在编译期捕获遗漏的反射序列化调用。

---

## AOT 常见陷阱

| 陷阱 | 症状 | 修复 |
|------|------|------|
| 匿名类型序列化 | 运行时 `NotSupportedException` | 改为强类型 record + `JsonSerializerContext` |
| 遗漏 `[JsonSerializable]` | 序列化输出 `{}` 或反序列化全为 null | 在 `JsonSerializerContext` 上添加 `[JsonSerializable(typeof(T))]` |
| 使用 `JsonSerializer.Serialize<T>(obj)` | 走反射路径 | 改用 `JsonSerializer.Serialize(obj, Context.Default.T)` |
| `dynamic` / `ExpandoObject` | 编译警告 + 运行时失败 | 改为强类型 |
| 泛型集合未注册 | `List<T>` 序列化为空 | 在上下文中注册 `[JsonSerializable(typeof(List<T>))]` |

---

## 行为约束

1. **消息定向**：由 WS 客户端发起的 `send`，AI 回复只发给该客户端；由 UI/语音发起的对话回复会广播所有 WS 客户端。
2. **共享上下文**：所有客户端共享同一个 AI 对话历史。
3. **串行处理**：同一时刻只有一个 AI 请求在处理。新请求需等待当前请求完成或被 `stop` 中止。
4. **未知消息类型**：客户端必须忽略未识别的 `type`，保证向前兼容。
5. **附件路径**：`path` 必须是 Cortana 进程所在机器的本地路径。
6. **心跳**：当前无应用层心跳，可依赖 WebSocket 协议层 Ping/Pong。

---

## 交互时序

```
客户端                                 Cortana
  │                                       │
  │── ws://host:52841/ws/ ──────────────▶│  握手
  │◀── connected {clientId} ─────────────│
  │                                       │
  │── send {data, attachments?} ────────▶│  发送消息
  │                                       │
  │◀── token {data} ─────────────────────│  ┐
  │◀── token {data} ─────────────────────│  │ 流式回复（N 条）
  │◀── token {data} ─────────────────────│  ┘
  │◀── done {sessionId} ────────────────│  回复完成
  │                                       │
  │◀── tts_started ─────────────────────│  ┐
  │◀── tts_subtitle {data} ────────────│  │ TTS 广播
  │◀── tts_completed ───────────────────│  ┘
  │◀── chat_completed ──────────────────│  流程结束
  │                                       │
  │── stop ─────────────────────────────▶│  中止
  │◀── error "cancelled" ───────────────│
```
