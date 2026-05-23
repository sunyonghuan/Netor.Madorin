# 03 - 内部事件 WebSocket 协议

> 状态：已被 S08 单端点 PluginBus 方案取代  
> 当前主线：`ws://localhost:{pluginBusPort}/internal`，协议 `cortana.plugin-bus`  
> 说明：本文保留为历史设计记录，旧 `/internal`、`PluginBus` 协议和 `memory.context.supply.*` 操作名不再作为当前实现依据。

## 目标

定义宿主向插件分发对话事实流的内部 WebSocket 协议。

## 区分原则

- 不复用旧聊天 `WsMessage`
- 不与旧 `send` / `stop` / `token` / `done` 混用
- 事件对象必须带标准事件元数据
- 协议必须支持版本化

## 当前地址

当前实现已收敛为单端点插件总线：

- `ws://localhost:{pluginBusPort}/internal`：PluginBus 统一内部协议。

历史地址已废弃：

- `ws://localhost:{ChatWsPort}/internal`：不再作为 Memory 插件内部通信入口。
- `ws://localhost:{ChatWsPort}/internal`：已废弃，不再注册独立服务。

## 握手消息

当前实现使用 `cortana.plugin-bus`：

```json
{
	"type": "connected",
	"clientId": "plugin-bus-client-id",
	"topics": ["conversation", "memory", "model", "plugin"],
	"protocol": "cortana.plugin-bus",
	"version": "1.2.0"
}
```

以下为旧协议示例，仅作历史记录：

```json
{
	"type": "connected",
	"clientId": "feed-client-id",
	"topics": ["conversation"],
	"protocol": "PluginBus",
	"version": "1.2.0"
}
```

## 插件订阅请求

当前实现订阅 `conversation`、`memory`、`model`：

```json
{
	"type": "subscribe",
	"topics": ["conversation", "memory", "model"],
	"protocol": "cortana.plugin-bus",
	"version": "1.2.0"
}
```

以下为旧协议示例，仅作历史记录：

```json
{
	"type": "subscribe",
	"topics": ["conversation"],
	"protocol": "PluginBus",
	"version": "1.2.0"
}
```

旧实现只支持 `conversation` topic；当前 PluginBus 已按 topic 订阅分发。

## 订阅确认

当前实现使用 `cortana.plugin-bus`，并回传实际订阅的 topic。以下旧示例仅作历史记录：

```json
{
	"type": "subscribed",
	"clientId": "feed-client-id",
	"topics": ["conversation"],
	"protocol": "PluginBus",
	"version": "1.2.0"
}
```

## 错误消息

当前实现错误帧使用 `protocol = "cortana.plugin-bus"`。结构化 `error` 对象仍属于 S08-25 后续标准化范围。以下旧示例仅作历史记录：

```json
{
	"type": "error",
	"clientId": "feed-client-id",
	"message": "protocol 不匹配",
	"protocol": "PluginBus",
	"version": "1.2.0"
}
```

## 事件消息

当前实时对话事件使用：

```json
{
	"type": "event",
	"protocol": "cortana.plugin-bus",
	"version": "1.2.0",
	"topic": "conversation",
	"op": "conversation.event.publish",
	"source": "host",
	"target": "plugin.memory",
	"payload": {}
}
```

以下为旧协议示例，仅作历史记录：

```json
{
	"type": "event",
	"topic": "conversation",
	"eventType": "conversation.turn.started",
	"payload": {},
	"protocol": "PluginBus",
	"version": "1.2.0"
}
```

## 当前事件类型

- `conversation.turn.started`
- `conversation.user.message`
- `conversation.assistant.delta`
- `conversation.turn.completed`

## 长期记忆上下文供应控制消息

当前长期记忆供应已迁移到 PluginBus memory topic：

- 宿主发送 `memory.context.supply.request`。
- Memory 插件返回 `memory.context.supply.response`。
- 参数缺失、异常或不可处理时返回 `memory.context.supply.error`。
- 宿主按 `requestId` 等待响应，默认短超时，超时或错误时降级为空上下文。

当前代码中的 Memory supply 业务字段仍以顶层 DTO 兼容形式生成；完全迁移到 envelope `payload` 是 S08-25 的剩余标准化工作。以下旧 `memory.context.supply.*` 示例仅作历史记录：

请求示例：

```json
{
	"type": "request",
	"protocol": "memory-context-supply",
	"version": "1.2.0",
	"op": "memory.context.supply.request",
	"requestId": "uuid",
	"agentId": "agent-1",
	"agentName": "默认智能体",
	"workspaceId": "E:\\Netor.me\\Madorin",
	"workspaceDirectory": "E:\\Netor.me\\Madorin",
	"sessionId": "session-1",
	"turnId": "turn-1",
	"messageId": "message-1",
	"scenario": "chat",
	"currentTask": "当前用户问题",
	"recentMessages": [
		{ "messageId": "message-1", "role": "user", "content": "当前用户问题", "createdAt": "2026-05-10T00:00:00Z" }
	],
	"triggerSource": "before-prompt",
	"maxMemoryCount": 8,
	"maxTokenBudget": 1200,
	"timeoutMs": 250,
	"traceId": "trace"
}
```

响应示例：

```json
{
	"type": "response",
	"protocol": "memory-context-supply",
	"version": "1.2.0",
	"op": "memory.context.supply.response",
	"requestId": "uuid",
	"enabled": true,
	"summary": "命中 2 条长期记忆",
	"confidence": 0.82,
	"groups": [],
	"items": [],
	"budget": { "maxMemoryCount": 8, "usedMemoryCount": 0, "maxTokenBudget": 1200, "estimatedTokens": 0 },
	"appliedPolicy": { "supplyEnabled": true, "maxMemoryCount": 8, "recallMinimumConfidence": 0.2, "ranking": "default", "grouping": "kind" },
	"traceId": "trace",
	"producerVersion": "1.0.0"
}
```

错误示例：

```json
{
	"type": "error",
	"protocol": "memory-context-supply",
	"version": "1.2.0",
	"op": "memory.context.supply.error",
	"requestId": "uuid",
	"traceId": "trace",
	"code": "INVALID_ARGUMENT",
	"message": "agentId 不能为空。",
	"retryable": false
}
```

约束：`agentId` 必须由宿主显式携带，插件不得回退到 `default`。供应包保持结构化，最终 prompt 拼接由宿主负责。

## 当前实现落点

- `WebSocketPluginBusServerService`：维护 `/internal` PluginBus、连接、心跳、订阅、路由和长期记忆供应。
- `PluginBusSubscriptionRegistry`：维护 topic 订阅。
- `PluginBusConversationHistoryDispatcher`：处理 `conversation.history.replay`、`conversation.history.batch`、`conversation.history.completed`。
- `PluginBusMemorySupplyDispatcher`：管理 memory supply pending 生命周期。
- `PluginBusModelCapabilityDispatcher`：处理 `model.capability.request/response`。
- `WebSocketConversationFeedRelayService`：名称保留旧语义，但实际广播到 PluginBus conversation topic。
- `LongMemoryContextProvider`：在宿主构建主智能体上下文前请求长期记忆供应包并注入 `AIContext.Instructions`。
- `MemoryIngestService` / `MemoryPluginBusConnection`：Memory 插件持久连接 PluginBus，断开后自动重连。
- `MemoryPluginBusDispatcher` / `MemoryConversationEventHandler` / `MemorySupplyRequestHandler`：插件端按 `type/topic/op` 分发并处理 conversation、memory、model、ping/pong。






