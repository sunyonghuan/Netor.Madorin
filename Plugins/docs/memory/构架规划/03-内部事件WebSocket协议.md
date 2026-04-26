# 03 - 内部事件 WebSocket 协议

> 状态：第一版已落地

## 目标

定义宿主向插件分发对话事实流的内部 WebSocket 协议。

## 区分原则

- 不复用旧聊天 `WsMessage`
- 不与旧 `send` / `stop` / `token` / `done` 混用
- 事件对象必须带标准事件元数据
- 协议必须支持版本化

## 当前地址

- `ws://localhost:{ChatWsPort}/ws/`：聊天协议
- `ws://localhost:{ChatWsPort}/internal/conversation-feed/`：内部事件协议

第一版实际采用同端口不同路径，不新增独立 feed 端口。

## 握手消息

```json
{
	"type": "connected",
	"clientId": "feed-client-id",
	"topics": ["conversation"],
	"protocol": "conversation-feed",
	"version": "1.0.0"
}
```

## 插件订阅请求

```json
{
	"type": "subscribe",
	"topics": ["conversation"],
	"protocol": "conversation-feed",
	"version": "1.0.0"
}
```

当前实现只支持 `conversation` topic。

## 订阅确认

```json
{
	"type": "subscribed",
	"clientId": "feed-client-id",
	"topics": ["conversation"],
	"protocol": "conversation-feed",
	"version": "1.0.0"
}
```

## 错误消息

```json
{
	"type": "error",
	"clientId": "feed-client-id",
	"message": "protocol 不匹配",
	"protocol": "conversation-feed",
	"version": "1.0.0"
}
```

## 事件消息

```json
{
	"type": "event",
	"topic": "conversation",
	"eventType": "conversation.turn.started",
	"payload": {},
	"protocol": "conversation-feed",
	"version": "1.0.0"
}
```

## 当前事件类型

- `conversation.turn.started`
- `conversation.user.message`
- `conversation.assistant.delta`
- `conversation.turn.completed`

## 当前实现落点

- `WebSocketServerService`：维护 internal feed 路径、connected/subscribe/subscribed/error 控制消息和订阅客户端集合
- `WebSocketConversationFeedRelayService`：订阅 EventHub Conversation 事件并广播 `event` 消息
- `WebSocketJsonContext`：提供 feed 消息与 Conversation 事件参数的 AOT JSON 源生成支持

