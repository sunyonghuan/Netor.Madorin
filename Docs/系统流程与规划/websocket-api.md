# Cortana WebSocket API 接入指南

> 当前说明：该接口面向当前 AvaloniaUI 主线和整体宿主能力，对外协议与 UI 技术栈解耦。只要宿主运行，客户端即可通过 WebSocket 接入，不依赖旧 WinForms UI。

> 说明补充：本文档描述的是当前对外聊天 WebSocket 协议，不包含宿主后续规划中的“内部对话事件订阅 WebSocket”。内部事件协议将单独建文档，并与当前聊天消息对象明确区分。

## 概述

Cortana 内置 WebSocket 服务，允许外部应用通过标准 WebSocket 协议接入 AI 对话能力。支持：

- 发送文本消息并接收 AI 流式回复
- 发送带附件（图片/文件）的多模态消息
- 中止正在进行的 AI 回复
- 接收语音识别（STT）、语音合成（TTS）、唤醒词等系统事件

## 连接

### 地址

```
ws://<host>:<port>/ws/
```

- **host**：Cortana 运行所在机器的 IP 地址。本机使用 `localhost` 或 `127.0.0.1`，局域网使用机器 IP。
- **port**：默认 `52841`，可在 Cortana「设置 → 系统设置 → 网络 → WebSocket 端口」中修改。

### 连接流程

```
客户端                              Cortana
  │                                    │
  │─── WebSocket 握手 ────────────────▶│
  │                                    │
  │◀── {"type":"connected",            │
  │      "clientId":"abc123..."} ──────│
  │                                    │
```

连接成功后，服务端立即发送一条 `connected` 消息，包含分配给此连接的 `clientId`。客户端应保存此 ID 用于日志排查。

## 消息格式

所有消息均为 **UTF-8 编码的 JSON 文本帧**。

### 客户端 → 服务端

#### 发送消息（send）

```json
{
  "type": "send",
  "data": "今天天气怎么样？",
  "attachments": []
}
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| `type` | string | 是 | 固定值 `"send"` |
| `data` | string | 是 | 用户消息文本 |
| `attachments` | array | 否 | 附件列表，不带附件时可省略或传空数组 |

**附件格式：**

```json
{
  "type": "send",
  "data": "这张图片是什么？",
  "attachments": [
    {
      "path": "C:\\Users\\user\\Pictures\\photo.jpg",
      "name": "photo.jpg",
      "type": "image/jpeg"
    }
  ]
}
```

| 附件字段 | 类型 | 说明 |
|---------|------|------|
| `path` | string | 文件在 Cortana 所在机器上的**完整路径**（注意：是服务端本地路径） |
| `name` | string | 文件名 |
| `type` | string | MIME 类型，如 `image/jpeg`、`text/plain` |

> **注意**：附件的 `path` 必须是 Cortana 进程能访问到的本地文件路径。如果客户端与 Cortana 不在同一台机器上，需要先将文件传输到 Cortana 机器，再提供其本地路径。

#### 中止生成（stop）

```json
{
  "type": "stop"
}
```

发送后，Cortana 会尽快停止当前 AI 回复的生成。

## 服务端 → 客户端

### AI 对话消息

当客户端发送 `send` 消息后，AI 开始流式生成回复，客户端将依次收到以下消息：

#### token — 流式文本片段

```json
{
  "type": "token",
  "data": "今天"
}
```

AI 回复会被拆分为多个 `token` 消息逐个推送。客户端应将所有 `token` 的 `data` 字段**拼接**得到完整回复。

#### done — 回复完成

```json
{
  "type": "done",
  "sessionId": "session_abc123"
}
```

表示本轮 AI 回复已完成。`sessionId` 为会话标识（可能为 `null`）。

#### error — 错误

```json
{
  "type": "error",
  "data": "模型调用失败：API 超时"
}
```

#### cancelled — 回复被取消

```json
{
  "type": "error",
  "data": "cancelled"
}
```

当 AI 回复被中止（用户发送 `stop` 或唤醒词打断）时收到。

### 系统事件（广播）

以下事件会广播给**所有**已连接的客户端，不论消息由谁发起。可按需监听，无需处理的类型直接忽略即可。

#### 语音识别（STT）

| type | data | 说明 |
|------|------|------|
| `stt_partial` | 识别中的文本 | 语音识别中间结果（实时变化） |
| `stt_final` | 最终识别文本 | 一句话识别完成的最终结果 |
| `stt_stopped` | `""` | 语音识别已停止 |

#### 语音合成（TTS）

| type | data | 说明 |
|------|------|------|
| `tts_started` | `""` | TTS 开始播放 |
| `tts_subtitle` | 正在播放的文本 | TTS 播放的字幕文本（逐句推送） |
| `tts_completed` | `""` | TTS 播放完成 |

#### 其他

| type | data | 说明 |
|------|------|------|
| `chat_completed` | `""` | 整个对话流程完成（含 TTS 播放结束） |
| `wakeword_detected` | `""` | 检测到唤醒词 |

## 完整交互时序

```
客户端                                 Cortana
  │                                       │
  │── ws://host:52841/ws/ ──────────────▶│  握手
  │◀── connected {clientId} ─────────────│
  │                                       │
  │── send "帮我写一首诗" ──────────────▶│  发送消息
  │                                       │
  │◀── token "春"  ──────────────────────│  ┐
  │◀── token "眠"  ──────────────────────│  │ 流式回复
  │◀── token "不觉晓" ──────────────────│  │
  │◀── token "，" ───────────────────────│  │
  │◀── token "处处" ─────────────────────│  │
  │◀── token "闻啼鸟" ──────────────────│  │
  │◀── token "。" ───────────────────────│  ┘
  │◀── done {sessionId} ────────────────│  完成
  │                                       │
  │◀── tts_started ─────────────────────│  ┐ TTS 播放
  │◀── tts_subtitle "春眠不觉晓，" ────│  │ （语音朗读）
  │◀── tts_subtitle "处处闻啼鸟。" ────│  │
  │◀── tts_completed ───────────────────│  ┘
  │◀── chat_completed ──────────────────│  对话流程结束
  │                                       │
  │── send "再来一首" ──────────────────▶│  下一轮
  │◀── token ... ────────────────────────│
  │◀── done ... ─────────────────────────│
  │                                       │
  │── stop ─────────────────────────────▶│  中止
  │◀── error "cancelled" ───────────────│
  │                                       │
```

## 代码示例（C#）

> **AOT 注意**：所有 JSON 序列化/反序列化必须使用 Source Generator（`JsonSerializerContext`），禁止匿名类型和反射。
> 以下示例均为 AOT 安全写法。

### 消息类型定义

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

### JSON Source Generator 上下文（AOT 必需）

```csharp
using System.Text.Json.Serialization;

// ✅ AOT 安全：编译器自动生成序列化代码，运行时零反射
[JsonSerializable(typeof(WsClientMessage))]
[JsonSerializable(typeof(WsServerMessage))]
[JsonSerializable(typeof(WsAttachment))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class CortanaJsonContext : JsonSerializerContext;
```

### 基础用法：连接、发送、接收

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://localhost:52841/ws/"), CancellationToken.None);

var buffer = new byte[8192];

// 接收 connected
var result = await ws.ReceiveAsync(buffer, CancellationToken.None);
var connected = JsonSerializer.Deserialize(
    buffer.AsSpan(0, result.Count),
    CortanaJsonContext.Default.WsServerMessage);
Console.WriteLine($"clientId: {connected?.ClientId}");

// ✅ AOT 安全：使用强类型 + Source Generator 序列化
var request = new WsClientMessage { Type = "send", Data = "你好，请介绍一下你自己" };
var json = JsonSerializer.Serialize(request, CortanaJsonContext.Default.WsClientMessage);
await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

// 接收流式回复
var response = new StringBuilder();
while (ws.State == WebSocketState.Open)
{
    result = await ws.ReceiveAsync(buffer, CancellationToken.None);
    var msg = JsonSerializer.Deserialize(
        buffer.AsSpan(0, result.Count),
        CortanaJsonContext.Default.WsServerMessage);

    switch (msg?.Type)
    {
        case "token":
            response.Append(msg.Data);
            Console.Write(msg.Data);
            break;

        case "done":
            Console.WriteLine($"\n--- 回复完成 (session: {msg.SessionId}) ---");
            goto exit;

        case "error":
            Console.WriteLine($"\n错误: {msg.Data}");
            goto exit;

        default:
            // 忽略 stt_*, tts_*, chat_completed 等广播事件
            break;
    }
}
exit:
Console.WriteLine($"完整回复: {response}");
```

### 封装客户端类

```csharp
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

public sealed class CortanaWsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _ws = new();
    private readonly Uri _uri;

    public string? ClientId { get; private set; }

    public CortanaWsClient(string host = "localhost", int port = 52841)
    {
        _uri = new Uri($"ws://{host}:{port}/ws/");
    }

    /// <summary>建立连接并接收 clientId。</summary>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        await _ws.ConnectAsync(_uri, ct);
        var msg = await ReceiveMessageAsync(ct);
        if (msg?.Type == "connected")
            ClientId = msg.ClientId;
    }

    /// <summary>发送文本消息，流式接收 AI 回复。</summary>
    public async IAsyncEnumerable<string> SendAndStreamAsync(
        string text,
        List<WsAttachment>? attachments = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var request = new WsClientMessage
        {
            Type = "send",
            Data = text,
            Attachments = attachments
        };

        // ✅ AOT 安全序列化
        var json = JsonSerializer.Serialize(request, CortanaJsonContext.Default.WsClientMessage);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);

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
        }
    }

    /// <summary>中止当前 AI 回复。</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        var request = new WsClientMessage { Type = "stop" };
        var json = JsonSerializer.Serialize(request, CortanaJsonContext.Default.WsClientMessage);
        await _ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, ct);
    }

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

        // ✅ AOT 安全反序列化
        return JsonSerializer.Deserialize(ms.ToArray(), CortanaJsonContext.Default.WsServerMessage);
    }

    public async ValueTask DisposeAsync()
    {
        if (_ws.State == WebSocketState.Open)
            await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        _ws.Dispose();
    }
}
```

### 使用封装类

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

### csproj 配置（AOT 发布）

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <RuntimeIdentifier>win-x64</RuntimeIdentifier>
    <!-- 编译期检测遗漏的反射序列化调用 -->
    <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
  </PropertyGroup>
</Project>
```

### AOT 常见陷阱

| 陷阱 | 症状 | 修复 |
|------|------|------|
| 匿名类型序列化 `new { type = "send" }` | 运行时 `NotSupportedException` | 改为强类型 record + `JsonSerializerContext` |
| 遗漏 `[JsonSerializable(typeof(T))]` | 序列化输出 `{}` 或反序列化全为 null | 在上下文类上补充注册 |
| 使用 `JsonSerializer.Serialize<T>(obj)` | 走反射路径，AOT 失败 | 改用 `JsonSerializer.Serialize(obj, Context.Default.T)` |
| `dynamic` / `ExpandoObject` | 编译警告 + 运行时崩溃 | 改为强类型 |

## 注意事项

1. **消息定向**：由 WebSocket 客户端发起的 `send` 请求，AI 回复（`token` / `done` / `error`）只会发送给**发起请求的那个客户端**。由主界面或语音发起的对话，AI 回复会广播给所有 WebSocket 客户端。

2. **共享上下文**：所有客户端共享同一个 AI 对话上下文。客户端 A 的对话历史对客户端 B 可见，下一轮对话会基于之前的完整历史。

3. **并发请求**：同一时刻只有一个 AI 请求在处理。如果 AI 正在生成回复，新的 `send` 请求会等待当前回复完成或被 `stop` 中止后才会被处理。

4. **端口配置**：默认端口 `52841`，修改后立即生效但建议重启 Cortana 以确保所有插件同步更新。

5. **未知消息类型**：客户端应忽略无法识别的 `type`，保证向前兼容。后续版本可能新增事件类型。

6. **心跳 / 保活**：当前未实现应用层心跳。如果需要检测连接存活，客户端可依赖 WebSocket 协议层的 Ping/Pong 帧，或定期发送任意消息并忽略未知类型的响应。

7. **附件路径**：`attachments` 中的 `path` 必须是 Cortana 进程所在机器的本地路径，远程客户端需要先将文件传输到服务端机器。
