# Madorin v1.4.0 发布说明

> 状态：正式发布文档。
> 日期：2026-06-14
> 用途：记录 v1.4.0 正式发布内容、PluginBus 协议升级、破坏性变更与第三方迁移指南。

## 发布概览

v1.4.0 重点围绕 **PluginBus 协议从 1.3.0 升级到 1.4.0、引入 plugin→plugin 事件广播、chat.\* op 重命名、voice 三件套信封式收敛、DoS / 误投 / 死链三类防御** 展开。

本版本主要包含以下能力：

1. PluginBus 协议版本从 `1.3.0` 升至 `1.4.0`。
2. `type=event` 帧扩展为支持 plugin→plugin 异步广播（原仅支持 host→plugin）。
3. 新增 `[PublishesEvent]` / `[SubscribesEvent]` attribute 与 plugin.json `publishedOps` / `subscribedOps` 字段。
4. 新增 `PluginBusBroadcastDispatcher` 宿主转发器与 `VoiceEventBridge` 桥接器。
5. **破坏性变更**：所有 `chat.*` op 全部重命名为 `conversation.chat.*`。
6. 仓库内 voice 三件套（KWS / STT / TTS）发包统一收敛为信封式（`type=event` + `op` 字段）。
7. 引入 IsProtectedOp 黑名单 / 单帧 1MB 上限 / 死链主动清理三道入口防御。
8. `subscribe` 帧新增 `subscribedOps` 字段，支持 op 级订阅过滤（缺省全收，向下兼容）。
9. 新增共享 `PluginBusClient` SDK（`Netor.Cortana.Plugin.Process` 程序集），暴露 `PublishEventAsync` / `SubscribeEventAsync` 两个 API；voice 三件套全部迁移到该 SDK。

一句话：**本版本把 PluginBus 升级为支持 plugin→plugin 广播的事件总线，引入显式事件契约声明，同时清理 chat.\* 历史包袱并加固防御层。**

---

## 破坏性变更

### chat.* op 完整重命名为 conversation.chat.*

所有按 `chat.*` 字面量实现的第三方 PluginBus 客户端 **必须升级**。op 首段须等于其归属 topic（v1.5 OQ3 A2 决议）。

| 旧 op (1.3.x) | 新 op (1.4.0) | 方向 |
| --- | --- | --- |
| `chat.token` | `conversation.chat.token` | host → plugin |
| `chat.done` | `conversation.chat.done` | host → plugin |
| `chat.error` | `conversation.chat.error` | host → plugin |
| `chat.{type}` 动态 | `conversation.chat.{type}` 动态 | host → plugin（BroadcastAsync） |
| `chat.message.send` | `conversation.chat.message.send` | plugin → host |
| `chat.generation.stop` | `conversation.chat.generation.stop` | plugin → host |

**保留现状的 op**（不在 OQ3 重命名范围内）：

- `system.notice`：独立控制平面通知。
- `model.capability.*`：model topic RPC（OQ4 子命名空间隔离）。
- `memory.context.supply.*`：memory topic RPC（OQ4 子命名空间隔离）。
- `*.history.replay/batch/completed`：各 topic 内部自洽。

### voice 三件套发包格式收敛

仓库内 KWS / STT / TTS 三个 Process 插件统一升级为信封式 `type=event` + 带 `.v1` 后缀的 op，例如 `voice.kws.detected.v1`。

旧直发式（`type=voice.xxx`）**1.4.0 起不再支持**。宿主侧 `HandleMessageAsync` 入口仅拦截 `type=="event"`，不为遗留格式额外加分支（v1.5 OQ2 决议）。外部第三方语音插件目前不存在，无现实兼容对象。

宿主 `VoiceEventBridge` 内部对 voice op 做 `NormalizeVoiceOp` 归一（`voice.xxx` → `voice.xxx.v1`，OQ5），兼容旧版仓库三件套发包，防 UI 字幕静默失声。

### plugin.json schema 扩展

新增两个对象数组字段：

```json
{
  "publishedOps": [
    { "op": "voice.kws.detected.v1", "description": "检测到唤醒词" }
  ],
  "subscribedOps": [
    { "op": "voice.stt.partial.v1", "reason": "实时显示识别中文本" }
  ]
}
```

旧插件不输出此字段时由 `Normalize()` 兜底为 `[]`，向下兼容。

---

## 核心变更

### 1. PluginBusEventMessage 扩展 2 个可选字段

```text
hopCount       (int?)    — 转发跳数兜底，>= 8 即丢弃（参考 IP TTL 工程惯例）
sourcePluginId (string?) — plugin→plugin 推荐填的 pluginId，弱声明校验依据
```

复用现有 record，无需新建 POCO。

### 2. PluginBusBroadcastDispatcher 宿主转发器

新增 `Src/Netor.Cortana.Networks/WebSockets/PluginBusBroadcastDispatcher.cs`，作为 `type=event` 帧的唯一入口。职责按顺序：

1. **R-CHAT-MISFIRE 受保护 op 防御**：`IsProtectedOp` 黑名单守卫，`conversation.chat.message.send` / `conversation.chat.generation.stop` / `system.notice` / `*.history.replay` / `model.capability.*` / `memory.context.supply.*` 等 12 项 RPC / 控制 op 被误标 `type=event` 时立即 LogWarning 拒收。
2. **R-PAYLOAD-AMP 单帧 1MB 上限**：`frame.GetRawText().Length > MaxFrameBytes` 即丢弃，防大 payload × N 订阅者 OOM。
3. **F3 host 排除**：`source == "host"` 的客户端冒充立即拒。
4. **F2 op 优先级**：`op > eventType > type`（1.4.0 有意变更，旧版为 `eventType > op > type`）；老插件无 op 时 fallback 到 eventType，不破坏 1.3.0 行为。
5. **hopCount 兜底**：`>= 8` 立即丢弃。
6. **路由**：从 op 解析 namespace 段为 topic，按订阅者 `subscribedOps` 二次过滤，排除 sourceClientId。
7. **F-LIFETIME 同步阶段固化**：重写 hopCount 为 owned JSON 字符串、snapshot clientIds、voice 桥接全部在同步阶段完成，避免后台 Task.Run 访问已 Dispose 的 JsonElement。
8. **F6 后台投递**：`Task.Run` 异步投递，每订阅者独立 1 秒超时。
9. **R-DEAD-SUBSCRIBER 死链主动清理**：`SendWithTimeoutAsync` 超时时主动调 `_subscriptions.Remove(cid)` + `_pluginBusConnections.RemoveAndCloseAsync(cid)`，避免 1 秒 linked CTS 击穿 `WebSocketConnectionManager` 守卫导致死链残留。

### 3. PluginManifestRegistry（OQ1 修订）

新建 `Src/Netor.Cortana.Plugin/PluginManifestRegistry.cs`，DI 单例，由 `PluginLoader` 在 load / reload / unload 时调用 `Register` / `Unregister` / `Clear`，供 BroadcastDispatcher 弱声明校验。v1 仅 LogWarning 不强拒。

跨程序集消费走现有间接路径 `Networks → AI → Plugin`（编译期可见 Plugin 公开类型），无需新增直接 `ProjectReference`。

### 4. PluginLoader 4 个接入点

| 方法 | 改动 |
| --- | --- |
| `LoadProcessPluginAsync` | 成功分支调 `RegisterManifest(manifest)` |
| `LoadNativePluginAsync` | 成功分支调 `RegisterManifest(manifest)` |
| `UnloadPluginByPath` | `changed=true` 分支调 `Unregister(pluginId)` |
| `UnloadAllPlugins` | 末尾调 `Clear()` |

顶层方法（`ScanAndLoadAsync` / `LoadPluginByPathAsync` / `Reload*` / 文件 watcher）通过底层方法自动覆盖，不需要单独挂。

### 5. SubscriptionRegistry 加 op 级订阅 API

`PluginBusSubscriptionRegistry` 新增 `_subscribedOps` 字段 + `SetSubscribedOps` / `GetSubscribedOps` / `MatchesSubscribedOp` 三个 API。订阅缺省（未声明或空集合）= 收 topic 下全部 op。

### 6. VoiceEventBridge 桥接器

新建 `Src/Netor.Cortana.Networks/WebSockets/Voice/VoiceEventBridge.cs`，把原 `PluginBusVoiceEventDispatcher` 的 switch 逻辑搬过来，移除帧识别（识别由 BroadcastDispatcher 统一做）。`voice.tts.greeting_ready.v1` / `voice.tts.error.v1` 显式列 case，对齐旧 dispatcher 行为；入口加 `NormalizeVoiceOp` 归一化。

旧 `PluginBusVoiceEventDispatcher.cs` 与对应测试整文件删除。

### 7. SDK Generator 写入事件契约

Native 与 Process 两套 Generator 同步处理 `[PublishesEvent]` / `[SubscribesEvent]`，扫描 attribute 后写入 plugin.json `publishedOps` / `subscribedOps` 对象数组。

`PluginManifestJsonContext` 追加 `PublishedOpDeclaration` / `SubscribedOpDeclaration` 两条 `[JsonSerializable]` 注册，AOT 安全。

### 8. 共享 PluginBus 客户端 SDK（OQ7）

新建 `Src/Plugins/Netor.Cortana.Plugin.Process/PluginBus/PluginBusClient.cs`，作为所有 Process 插件的 PluginBus 客户端基类，AOT-strict（仅 `JsonElement` 透传，载荷由调用方自带 `JsonSerializerContext` 序列化）。公开 API：

```csharp
Task PublishEventAsync(string op, JsonElement payload, CancellationToken ct);
Task SubscribeEventAsync(string op, Func<JsonElement, Task> handler, CancellationToken ct);
```

`ProcessPluginHost.BuildServiceProvider` 自动注册 `PluginInfoData` + `PluginBusClient`（singleton）+ 关联的 `IHostedService`，第三方插件零模板代码即可获得连接管理 / 懒建立 / 接收循环 / ping-pong 心跳 / handler 串行调度。

仓库内 KWS / STT / TTS 三件套同步迁移到新 SDK：每个插件保留 ~25 行类型化薄包装（仅负责把 POCO payload 序列化为 `JsonElement`），删除原各自的 `XxxPluginBusEvent` 信封 POCO 与 240 行 client 实现，三处合计减少约 700 行重复代码。

---

## 第三方迁移指南

### 第三方 PluginBus 客户端（按 websocket-api.md 实现）

1. **协议版本**：`subscribe` / `request` / 常量帧的 `version` 字段从 `1.3.0` 升至 `1.4.0`。
2. **op 字面量**：`chat.message.send` / `chat.generation.stop` / `chat.token` / `chat.done` / `chat.error` 全部加 `conversation.` 前缀。
3. **op 优先级**：宿主侧 1.4.0 起 `op > eventType > type`；同时填 `op` 与 `eventType` 且不一致时以 op 为权威。老插件不填 op 时 fallback 到 eventType，不破坏 1.3.0 行为。
4. **可选字段**：发布事件时建议填 `sourcePluginId`，宿主据此做 plugin.json 弱声明校验（仅 LogWarning 不强拒）。

### 第三方插件 SDK（用 Native / Process Generator 的）

1. 入口类追加 `[PublishesEvent("xxx.v1", Description = "...")]` 与 `[SubscribesEvent("yyy.v1", Reason = "...")]`，多次叠加。
2. 重新构建后检查 plugin.json `publishedOps` / `subscribedOps` 字段正确输出。
3. **op 首段须等于归属 topic**（OQ3）。memory / model topic 上的 plugin event 必须用子命名空间 `memory.event.*` / `model.event.*`，避免与 RPC op 命名冲突（OQ4）。

### 文档与模板同步重命名

- `Docs/系统流程与规划/websocket-api.md`（9 处字面量）
- `skills/websocket-integration/SKILL.md`（6 处）
- `skills/websocket-integration/resources/{ws-message-samples.json,client-checklist.md,csharp-client-template.md}`
- `skills/websocket-integration/scripts/new-websocket-client.ps1`

---

## 范围边界

- **不做事件持久化**：宿主重启即清空，事件是状态变化通知不是审计日志。
- **不做迟订阅快照（replayLatest）/ 不做节流（throttleMs）**：当前场景下属于伪需求。
- **不做请求-响应 RPC 合并**：`memory.context.supply.*` / `model.capability.*` 保持现状。
- **运行帧不支持通配符 op**：`subscribedOps` 必须显式列出（缺省 = 收 topic 下全部，**这是缺省，不是通配符**）。
- **manifest 不支持通配符**：`plugin.json.subscribedOps` 必须显式声明。
- **v1 不做 sourcePluginId 可信绑定**：仅自报 + LogWarning，v2 才做 clientId↔pluginId 强绑定。
- **v1 不保证事件 ordering**：Task.Run + WhenAll 多帧并发投递，partial 类高频流可能错序送达；订阅插件应按"总是用最新值覆盖"模式编写（R-ORDERING）。
- **不保留直发式 grace 期**：仓库内三件套同 PR 升级到信封式，外部第三方语音插件不存在，1.4.0 起 `type=voice.xxx` 老格式不再支持（OQ2）。

---

## 构建验证

已执行并通过：

```text
dotnet build Src/Netor.Cortana.Networks/Netor.Cortana.Networks.csproj -c Release
dotnet build Src/Netor.Cortana.Plugin/Netor.Cortana.Plugin.csproj -c Release
dotnet build Src/Plugins/Netor.Cortana.Plugin.Process/Netor.Cortana.Plugin.Process.csproj -c Release
dotnet build Src/Netor.Cortana.Entitys/Netor.Cortana.Entitys.csproj -c Release
dotnet build Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/Cortana.Plugins.Voice.Kws.Sherpa.csproj -c Release
dotnet build Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Cortana.Plugins.Voice.Stt.Sherpa.csproj -c Release
dotnet build Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/Cortana.Plugins.Voice.Tts.Melo.csproj -c Release
dotnet test  Tests/Netor.Cortana.Networks.Tests/Netor.Cortana.Networks.Tests.csproj -c Release
```

结果：全部库 0 警告 0 错误；测试 62 项全绿（含新增 PluginBusBroadcastDispatcherTests 19 项 + VoiceEventBridgeTests 16 项）。

---

## 联调脚本

仓库内提供两个一次性手测控制台用于 PluginBus 端到端联调（不在解决方案文件里，本质是脚本）：

- `Tests/PluginBusPublisher/`：以 `ClientWebSocket` 直连 `/internal`，按 1.4.0 信封式发布若干 `type=event` 帧；详见 `Tests/PluginBusPublisher/README.md`。
- `Tests/PluginBusSubscriber/`：直连 `/internal` 后发送 1.4.0 `subscribe` 帧并打印命中事件；详见 `Tests/PluginBusSubscriber/README.md`。

典型用法是先启动 UI 主程序，再分别拉起订阅者和发布者，订阅者侧出现 `✓ 收到事件` + 宿主侧出现 `Event publisher not declared in plugin.json` 弱声明告警，即表示 `PluginBusBroadcastDispatcher` 路由链路通畅。

## 已知问题与后续事项

- v2 计划：`subscribe` 帧携带 `pluginId` 字段，宿主用安装包密钥 / 启动握手 token 验证 pluginId 真实性，绑定 `clientId → pluginId` 后做强校验，取代当前的弱声明校验。
- 第三方按 websocket-api.md 实现的 PluginBus 客户端必须按本文档"第三方迁移指南"升级 op 字面量。
- 完整设计文档与决策记录见 `Docs/已完成功能规划/插件事件广播/README.md` v1.5。

> 本发布说明作为 v1.4.0 正式发布文档，后续补丁或增量能力请在新版本发布说明中继续记录。
