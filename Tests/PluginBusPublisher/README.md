# PluginBusPublisher

PluginBus 1.4.0 端到端联调用的手测发布者。直接以 `ClientWebSocket` 连接宿主 `/internal` 端点，延迟若干秒后按 1.4.0 信封式（`type=event` + `op` + `sourcePluginId` + `payload`）发送 N 条事件。不依赖 `PluginBusClient` SDK，目的是**独立验证宿主侧 `PluginBusBroadcastDispatcher` 接收 / 路由 / 弱声明告警链路**，与 SDK 实现解耦。

## 启动参数

```text
dotnet run --project Tests/PluginBusPublisher/PluginBusPublisher.csproj -c Debug --no-build -- \
    [endpoint] [op] [count] [startDelaySeconds]
```

| 位置 | 含义 | 默认值 |
| ---- | ---- | ---- |
| 1 | 宿主 PluginBus 端点 | `ws://localhost:12841/internal` |
| 2 | 发布的 op | `demo.test.message.v1` |
| 3 | 发布事件数量 | `5` |
| 4 | 连接成功后等多久再开始发布（秒） | `5` |

## 行为

1. 立即连接 WebSocket，进入后台接收循环（自动回 `ping` → `pong`，不解析其它帧）。
2. 等待 `startDelaySeconds` 秒后开始发布，相邻两条间隔 1 秒。
3. 每条 payload 形如 `{ index, message, sentAt }`，`sourcePluginId="demo_publisher"`。
4. 全部发完后等 1 秒，主动 `CloseAsync(NormalClosure)` 退出。

## 典型联调命令

订阅者先起来后再起发布者，确保订阅帧已注册到宿主：

```bash
dotnet run --project Tests/PluginBusPublisher/PluginBusPublisher.csproj -c Debug --no-build -- \
    ws://localhost:12841/internal demo.test.message.v1 5 6
```

## 弱声明告警

`demo_publisher` 没有 `plugin.json`，宿主的 `PluginBusBroadcastDispatcher.ValidatePublisherDeclaration` 每条都会打 `Event publisher not declared in plugin.json. PluginId=demo_publisher Op=…` 警告。这是**v1 弱声明的预期行为**，仅 LogWarning，不阻断广播。看到这条告警 + 订阅者端 `✓ 收到事件` 才算路由完整跑通。

## 注意

- 宿主端口非固定，以 UI 启动日志为准（参考 `Tests/PluginBusSubscriber/README.md`）。
- 该工具**不在解决方案文件里**，是临时手测脚本。
