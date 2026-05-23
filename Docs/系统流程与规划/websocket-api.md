# Madorin WebSocket / PluginBus 接入指南

> 状态：按当前代码实现整理。宿主 WebSocket 已收口为单端点 PluginBus，不再区分旧 `/ws/` 聊天端点和 `/internal/conversation-feed/` 内部事件端点。

## 概述

Madorin 内置 Kestrel WebSocket 服务，统一承载：

- 外部客户端发送聊天消息、停止生成、发送 `system.notice`
- `conversation` topic 的聊天 token / done / error 事件
- `conversation.history.replay` 历史会话回放
- `memory` topic 的长期记忆供应请求与响应
- `model` topic 的模型能力请求与响应
- `workflow` topic 的任务事件发布与历史回放

端点和协议常量定义在：

```text
Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs
```

## 当前端点

```text
ws://<host>:<port>/internal
```

| 项 | 当前值 |
| --- | --- |
| 默认端口 | `52841` |
| 端点常量 | `CortanaWsEndpoints.PluginBusPath` |
| 端点路径 | `/internal` |
| 协议 | `cortana.plugin-bus` |
| 协议版本 | `1.2.0` |

端口来自系统设置 `WebSocket.Port`。如果设置值为空或端口绑定失败，服务会回退到随机可用端口。

## 连接握手

连接成功后，服务端会立即发送 `connected` 控制消息：

```json
{
  "type": "connected",
  "clientId": "3f1f2c7f3e2b4d4c9e1a7b8c9d0e1f2a",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topics": ["conversation", "memory", "model", "plugin"]
}
```

`clientId` 是当前 WebSocket 连接标识。当前 connected 消息列出 `conversation`、`memory`、`model`、`plugin`，代码常量中还包含 `workflow` topic，客户端可按需订阅。

## 订阅 topic

客户端通过 `subscribe` 消息声明需要接收的 topic：

```json
{
  "type": "subscribe",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topics": ["conversation"],
  "capabilities": ["conversation.v1"]
}
```

服务端返回：

```json
{
  "type": "subscribed",
  "clientId": "3f1f2c7f3e2b4d4c9e1a7b8c9d0e1f2a",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topics": ["conversation"]
}
```

当前已知能力 token：

| capability | 说明 |
| --- | --- |
| `conversation.v1` | 对话事件与历史回放 |
| `memory.v1` | 长期记忆供应 |
| `workflow.v1` | workflow 事件与历史回放 |

未知 capability 会被忽略。订阅 `workflow` 但未声明 `workflow.v1` 时，当前实现只记录 warning，不阻断订阅。

## 发送聊天消息

当前服务兼容旧聊天消息格式：

```json
{
  "type": "send",
  "data": "帮我总结当前项目",
  "attachments": []
}
```

也支持 PluginBus envelope：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "chat.message.send",
  "payload": {
    "type": "send",
    "data": "帮我总结当前项目",
    "attachments": []
  }
}
```

附件格式仍沿用聊天消息载荷：

```json
{
  "path": "C:\\Users\\user\\Pictures\\photo.jpg",
  "name": "photo.jpg",
  "type": "image/jpeg"
}
```

`path` 必须是 Madorin 进程所在机器可访问的本地路径。

## 停止生成

旧格式：

```json
{
  "type": "stop"
}
```

PluginBus envelope：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "chat.generation.stop",
  "payload": {
    "type": "stop"
  }
}
```

## 系统通知

当前代码支持 `system.notice`，用于把临时系统信息送入聊天输入通道：

```json
{
  "type": "system.notice",
  "title": "构建完成",
  "level": "info",
  "source": "external-tool",
  "data": "Release 包已生成。"
}
```

也可以用 envelope：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "system.notice",
  "payload": {
    "type": "system.notice",
    "title": "构建完成",
    "level": "info",
    "source": "external-tool",
    "data": "Release 包已生成。"
  }
}
```

## 服务端聊天事件

AI 输出统一包装为 PluginBus event：

```json
{
  "type": "event",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "chat.token",
  "source": "host",
  "target": "3f1f2c7f3e2b4d4c9e1a7b8c9d0e1f2a",
  "timestamp": 1770000000000,
  "eventType": "chat.token",
  "payload": {
    "type": "token",
    "data": "当前"
  }
}
```

常见 `op`：

| op | payload.type | 说明 |
| --- | --- | --- |
| `chat.token` | `token` | 流式文本片段 |
| `chat.done` | `done` | 本轮回复完成，`payload.sessionId` 可能为空 |
| `chat.error` | `error` | 生成失败或取消 |
| `chat.stt_partial` 等 | 对应旧事件 type | 语音、TTS、唤醒等广播事件 |

由某个 WebSocket 客户端发起的聊天输出会定向给该客户端；宿主广播事件会发给订阅 `conversation` topic 的客户端。

## 对话历史回放

旧简写：

```json
{
  "type": "replay",
  "sinceTimestamp": 0,
  "batchSize": 500
}
```

PluginBus envelope：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "conversation.history.replay",
  "requestId": "replay-001",
  "payload": {
    "sinceTimestamp": 0,
    "batchSize": 500
  }
}
```

`batchSize` 在代码中会被限制到 `100` 到 `2000` 之间。服务端会返回 `conversation.history.batch` 和 `conversation.history.completed`。

## Workflow 事件

Workflow 通过 `workflow` topic 接入：

| op | 说明 |
| --- | --- |
| `workflow.event.publish` | 发布任务事件 |
| `workflow.history.replay` | 请求 workflow 历史回放 |
| `workflow.history.batch` | 历史批次响应 |
| `workflow.history.completed` | 历史回放完成 |

示例：

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "workflow",
  "op": "workflow.history.replay",
  "requestId": "workflow-replay-001",
  "payload": {
    "sinceTimestamp": 0,
    "batchSize": 500
  }
}
```

## 长期记忆供应

宿主侧 `LongMemoryContextProvider` 在需要注入长期记忆时，会通过 `ILongMemorySupplyClient` 向订阅 `memory` topic 的插件发送：

```text
memory.context.supply.request
```

记忆插件返回：

```text
memory.context.supply.response
```

失败时返回：

```text
memory.context.supply.error
```

这部分由 `WebSocketPluginBusServerService`、`MemoryContextSupplyProtocol` 和 `Cortana.Plugins.Memory` 共同实现，已经是当前运行链路，不再是规划项。

## 模型能力控制面

模型能力请求使用 `model` topic：

```text
model.capability.request
model.capability.response
```

当前由 `PluginBusModelCapabilityDispatcher` 转发给宿主模型能力服务。

## 心跳

服务端会定期发送 PluginBus ping。客户端收到后应回复：

```json
{
  "type": "pong"
}
```

如果客户端长时间不响应，服务端会关闭该连接。

## C# 最小示例

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

using var ws = new ClientWebSocket();
await ws.ConnectAsync(new Uri("ws://localhost:52841/internal"), CancellationToken.None);

static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
{
    var buffer = new byte[16 * 1024];
    using var ms = new MemoryStream();
    WebSocketReceiveResult result;
    do
    {
        result = await ws.ReceiveAsync(buffer, ct);
        if (result.MessageType == WebSocketMessageType.Close) return string.Empty;
        ms.Write(buffer, 0, result.Count);
    } while (!result.EndOfMessage);

    return Encoding.UTF8.GetString(ms.ToArray());
}

var connected = JsonNode.Parse(await ReceiveTextAsync(ws, CancellationToken.None));
Console.WriteLine($"clientId: {connected?["clientId"]}");

var subscribe = """
{
  "type": "subscribe",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topics": ["conversation"],
  "capabilities": ["conversation.v1"]
}
""";

await ws.SendAsync(
    Encoding.UTF8.GetBytes(subscribe),
    WebSocketMessageType.Text,
    true,
    CancellationToken.None);

var send = """
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "chat.message.send",
  "payload": {
    "type": "send",
    "data": "你好，请介绍一下当前宿主能力",
    "attachments": []
  }
}
""";

await ws.SendAsync(
    Encoding.UTF8.GetBytes(send),
    WebSocketMessageType.Text,
    true,
    CancellationToken.None);

while (ws.State == WebSocketState.Open)
{
    var text = await ReceiveTextAsync(ws, CancellationToken.None);
    if (string.IsNullOrWhiteSpace(text)) break;

    var message = JsonNode.Parse(text);
    if (message?["type"]?.GetValue<string>() == "ping")
    {
        await ws.SendAsync(
            Encoding.UTF8.GetBytes("""{"type":"pong"}"""),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None);
        continue;
    }

    if (message?["topic"]?.GetValue<string>() != "conversation") continue;

    var op = message?["op"]?.GetValue<string>();
    var payload = message?["payload"];
    switch (op)
    {
        case "chat.token":
            Console.Write(payload?["data"]?.GetValue<string>());
            break;
        case "chat.done":
            Console.WriteLine();
            return;
        case "chat.error":
            throw new InvalidOperationException(payload?["data"]?.GetValue<string>());
    }
}
```

正式 Native AOT 客户端建议改用强类型 record 和 `JsonSerializerContext`，避免运行时反射序列化。

## 迁移说明

| 历史写法 | 当前写法 |
| --- | --- |
| `ws://localhost:52841/ws/` | `ws://localhost:52841/internal` |
| `ws://localhost:52841/internal/conversation-feed/` | `ws://localhost:52841/internal` + `subscribe` |
| 裸 `token` / `done` / `error` 输出 | PluginBus `type=event` + `topic=conversation` + `op=chat.*` |
| 多端点内部事件 | 单端点 PluginBus topic / op |

## 注意事项

1. 客户端应忽略未知 `type`、`topic`、`op`，保证向前兼容。
2. 需要接收服务端广播事件时，必须先订阅对应 topic。
3. 附件路径必须是宿主进程可访问的本地路径。
4. `/ws/` 和 `/internal/conversation-feed/` 不是当前代码注册的端点。



