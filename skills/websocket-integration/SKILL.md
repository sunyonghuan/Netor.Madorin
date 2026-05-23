---
name: websocket-integration
description: 'Madorin PluginBus WebSocket 接入。用于生成 /internal 单端点、cortana.plugin-bus、conversation topic、chat.message.send、chat.generation.stop、system.notice、ping/pong 的 C# 客户端代码与样板。关键词：WebSocket、PluginBus、/internal、chat.message.send、ping、pong。'
user-invocable: true
---

# WebSocket Integration

## Scope

- 端点：`ws://localhost:{pluginBusPort}/internal`
- 协议：`cortana.plugin-bus`
- 版本：`1.2.0`
- 主题：`conversation`，按需订阅 `memory` / `model` / `plugin` / `workflow`

## Init Fields

- `pluginBusEndpoint`
- `pluginBusPort`
- `pluginBusPath`
- `pluginBusProtocol`
- `pluginBusVersion`

优先读取 `pluginBusEndpoint`，其次 `pluginBusPort`。

## Envelope

字段：

- `type`
- `protocol`
- `version`
- `topic`
- `op`
- `requestId`
- `source`
- `target`
- `timestamp`
- `payload`

## Subscribe

```json
{
  "type": "subscribe",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topics": ["conversation"],
  "capabilities": ["conversation.v1"]
}
```

## Send

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "chat.message.send",
  "source": "client",
  "target": "host",
  "payload": {
    "type": "send",
    "data": "text",
    "attachments": []
  }
}
```

## Stop

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

## System Notice

```json
{
  "type": "request",
  "protocol": "cortana.plugin-bus",
  "version": "1.2.0",
  "topic": "conversation",
  "op": "system.notice",
  "payload": {
    "type": "system.notice",
    "data": "text",
    "title": "title",
    "level": "info",
    "source": "source"
  }
}
```

## Receive

- `type=connected`
- `type=subscribed`
- `type=event` + `op=chat.token` + `payload.type=token`
- `type=event` + `op=chat.done` + `payload.type=done`
- `type=event` + `op=chat.error` + `payload.type=error`
- `type=ping` -> reply `pong`

## Rules

- 使用 `ClientWebSocket`
- 使用 `JsonSerializerContext`
- 忽略未知 `type/op`
- 不硬编码端口
- 附件路径必须为本机路径
- 断线后重建客户端实例

## Assets

- `scripts/new-websocket-client.ps1`
- `resources/ws-message-samples.json`
- `resources/client-checklist.md`
- `resources/csharp-client-template.md`


