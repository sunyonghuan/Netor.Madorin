# PluginBusSubscriber

PluginBus 1.4.0 端到端联调用的手测订阅者。直接以 `ClientWebSocket` 连接宿主 `/internal` 端点，延迟若干秒后发送 `subscribe` 帧，命中 op 的 `event` 帧打印到控制台。不依赖 `PluginBusClient` SDK，目的是**独立验证宿主侧的 `PluginBusBroadcastDispatcher` 路由是否走通**，与 SDK 实现解耦。

## 启动参数

```text
dotnet run --project Tests/PluginBusSubscriber/PluginBusSubscriber.csproj -c Debug --no-build -- \
    [endpoint] [topic] [op] [subscribeDelaySeconds]
```

| 位置 | 含义 | 默认值 |
| ---- | ---- | ---- |
| 1 | 宿主 PluginBus 端点 | `ws://localhost:12841/internal` |
| 2 | 订阅 topic（写入 subscribe 帧 `topics` 字段） | `demo` |
| 3 | 订阅 op（写入 subscribe 帧 `subscribedOps` 字段，且接收循环按此过滤） | `demo.test.message.v1` |
| 4 | 连接成功后等多久再发 subscribe 帧（秒） | `3` |

## 行为

1. 立即连接 WebSocket，进入接收循环；自动回复宿主的 `ping` 帧。
2. 等待 `subscribeDelaySeconds` 秒后发送 1.4.0 协议格式的 subscribe 帧（`protocol="cortana.plugin-bus"`、`version="1.4.0"`）。
3. `type=event` 且 `op` 命中时打印 `✓ 收到事件 …`，未命中打印 `event 但 op 不匹配`。
4. 接 Ctrl+C 退出。

## 典型联调命令

宿主 UI 启动后，先起订阅者再起发布者（参考 `Tests/PluginBusPublisher/README.md`）：

```bash
dotnet run --project Tests/PluginBusSubscriber/PluginBusSubscriber.csproj -c Debug --no-build -- \
    ws://localhost:12841/internal demo demo.test.message.v1 3
```

## 注意

- 宿主端口非固定，UI 启动时若 12841 被占会回退到随机端口；请以 UI 启动日志中 `PluginBus 服务已启动，端口：xxxxx` 为准。
- 该工具**不在解决方案文件里**，是临时手测脚本。改动 PluginBus 协议或 dispatcher 时复用即可。
