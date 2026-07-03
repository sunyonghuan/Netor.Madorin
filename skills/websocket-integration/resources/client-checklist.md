# PluginBus Checklist

1. 连接 `ws://localhost:{pluginBusPort}/internal`
2. 读取 `pluginBusEndpoint` 或 `pluginBusPort`
3. 等待 `connected`
4. 发送 `subscribe`
5. 使用 `type=request/topic=conversation/op=conversation.chat.message.send`
6. 接收 `event`，从 `payload.type` 读取 `token/done/error`
7. 收到 `ping` 返回 `pong`
8. 使用 `JsonSerializerContext`
9. 忽略未知 `type/op`
10. 断线后重建 `ClientWebSocket`
