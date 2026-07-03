# 插件事件广播

> 完成归档稿 v1.5
> 当前状态：✅ 已完成，已归档到已完成功能规划
>
> **实施代码已落地**：`[PublishesEvent]` / `[SubscribesEvent]` attribute、共享 SDK `PluginBusClient`、voice 三件套类型化包装与 `.v1` op 已就位。三方插件开发者请直接看 [插件事件总线开发指南.md](../../三方对接文档/插件开发指南/插件事件总线开发指南.md) 与 [语音插件开发指南.md §六/§九](../../三方对接文档/插件开发指南/语音插件开发指南.md)。本目录是面向 Madorin 内部的策划文档，记录设计权衡、宿主侧 BroadcastDispatcher / VoiceEventBridge / PluginManifestRegistry 的代码级骨架与 PR 拆分。
>
> 代码核对（2026-06-14，v1.5 基于 8 维度代码审评结果再修订）：
>
> - **`type: "event"` 帧已存在并被双向使用**：宿主侧 [PluginBusMessageFactory.CreateChatEvent](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusMessageFactory.cs#L15-L30) + 三处 Relay 服务 ([WorkflowFeed:88](../../../Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketWorkflowFeedRelayService.cs#L88) / [MeetingFeed:93](../../../Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketMeetingFeedRelayService.cs#L93) / [ConversationFeed:99](../../../Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketConversationFeedRelayService.cs#L99)) 已用 `Type = "event"` 做 host→plugin 广播；语音三件套已上行发送 `type: event` 或 `type: voice.*`。**本方案是扩展 plugin→plugin 方向，不是新增帧类型**。
> - **`PluginBusEventMessage` POCO 已存在并已注册 JsonContext**：[WebSocketJsonContext.cs:15, 93-127](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93)，字段含 `type/protocol/version/topic/requestId/op/source/target/timestamp/eventType/payload`。**本方案复用此 POCO，仅新增 `hopCount` 与 `sourcePluginId` 两个可选字段**，不另建。
> - **现有 dispatcher 已多格式兼容**：[PluginBusVoiceEventDispatcher.ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 优先级 `eventType > op > type`，覆盖信封式（`type=event` + `eventType=voice.xxx`）和直发式（`type=voice.xxx`）两种格式；本方案 1.4.0 起**有意变更**优先级为 `op > eventType > type`，把 op 提到首位作为权威标识，eventType 保留向下兼容（缺省 op 时 fallback；二者同时存在且不一致时以 op 为准）。
> - **订阅注册器已就位**：[PluginBusSubscriptionRegistry](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusSubscriptionRegistry.cs) 支持 topic 订阅与 capability 声明，需要追加 op 级订阅；pluginId 绑定留 v2。
> - **subscribe 帧解析已就位**：[WebSocketPluginBusServerService.HandleMessageAsync:462-502](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L462-L502) 已处理 topics + capabilities，需追加 subscribedOps。
> - **VoiceEventDispatcher 是单向收敛**：转 `Events.OnXxx`，**未广播给其他插件**。本方案让 TryHandle 入口退出舞台，把 switch 逻辑搬入 BroadcastDispatcher 内部 voice bridge。
> - **memory / model RPC 路径完全不动**：[PluginBusMemorySupplyDispatcher](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusMemorySupplyDispatcher.cs) / [PluginBusModelCapabilityDispatcher](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusModelCapabilityDispatcher.cs) 已对外，保持现状（决策依据见 §5.2）。
> - **整个软件 AOT 强约束**：`PluginBusEventMessage` 加 2 个可选字段，`WebSocketJsonContext` 已注册无需追加；新增 `PublishedOpDeclaration` / `SubscribedOpDeclaration` 两个 plugin manifest 子记录类型，需追加注册到 `PluginManifestJsonContext`；`PluginManifestRegistry` 容器复用 `ConcurrentDictionary<string, HashSet<string>>` 形态（详见 §3.6）。
>
> **v1.3 与 v1.2 的差异**（基于源码核验，见 §九 修订记录）：
>
> - F1 复用 `PluginBusEventMessage`，不新建 `PluginBusEventFrame`
> - F2 字段优先级 op > eventType（延续现有 ReadEventType 规则）
> - F3 type==event 分支识别要排除 `source == "host"` 的 host→plugin 帧
> - F4 `PluginManifestRegistry` 改 DI 单例，由 PluginLoader load/unload 时调用
> - F5 `subscribedOps` 分层：manifest 强制声明（开发态）vs subscribe 帧缺省全收（运行态）
> - F6 fire-and-forget 措辞改为"业务侧不等响应，但宿主等待网络投递完成"，伪代码改为后台 Task.Run + per-subscriber 1 秒超时
> - F7 `voice.tts.greeting_ready` 在 voice bridge 显式列 case + LogDebug，与旧 dispatcher 行为对齐
>
> **v1.4 与 v1.3 的差异**（基于 7 条评审问题再修订，见 §九 修订记录）：
>
> - F-LIFETIME `using var doc = JsonDocument.Parse(json)` 在 HandleMessageAsync return 后即被 Dispose，root/frame 指向已释放对象。投后台 Task.Run 前必须先在同步阶段把转发载荷序列化为 string + 解析出目标 clientId 列表，后台只跑 SendAsync
> - F-PRIORITY 1.4.0 起字段优先级 **有意变更** 为 `op > eventType > type`（旧 [ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 是 `eventType > op > type`）。op 与 eventType 同时存在且不一致时以 op 为权威；老插件无 op 时 fallback 到 eventType，不破坏 1.3.0 行为
> - F-DIRECT 老 voice 直发式（`type=voice.xxx`）入口闭环：grace 期承诺**仅对外部第三方语音插件**，仓库内三件套同 PR 升级到信封式后无遗留直发式帧；HandleMessageAsync 入口仅拦截 `type == "event"`，不为遗留格式额外加分支
> - F-SCHEMA `plugin.json` 中 `publishedOps` / `subscribedOps` schema 统一为对象数组（含 op + description/reason，便于审计）；C# 模型用 record 数组承载；`PluginManifestRegistry.Register` 接口签名保留 `IEnumerable<string>`（取 op 集合），由 PluginLoader 内从对象数组抽 op 后传入
> - F-LOADER `PluginManifestRegistry` 接入点挂在 PluginLoader 底层方法：注册挂在 `LoadProcessPluginAsync` / `LoadNativePluginAsync` 的成功分支（紧挨 `NotifyPluginsChanged()`）；注销挂在 `UnloadPluginByPath` 的 `changed=true` 分支末尾；`UnloadAllPlugins` 用 `manifests.Clear()` 清空。文件 watcher 自动覆盖（它们都调用底层方法）
> - F-FIELDCOUNT §2.1 标题改为"新增 2 个可选字段（hopCount + sourcePluginId）"，与 §2.1.1 字段表 + §2.1.3 代码示例的 2 字段保持一致
> - F-SOURCE source / sourcePluginId 语义分层收紧：v1 路由仅信任 clientId 排除自回环（不信任自报字段）；弱声明校验从 frame 读 sourcePluginId（缺省 fallback 到 source），与 plugin.json 比对，仅 LogWarning，不作安全依据；F3 host 排除仅靠 `source == "host"` 判别（host 自身的 BroadcastPluginBusAsync 已确保不进 HandleMessageAsync 上行；客户端冒充 source="host" 上行被立即拒）；v2 才做可信绑定
>
> **v1.5 与 v1.4 的差异**（基于 8 维度代码审评 + 5 个 OQ 决议，见 §九 修订记录）：
>
> - **OQ1 PluginManifestRegistry 改放 Plugin 程序集**（v1.4 误放 Networks 反向依赖）—— 贴近 PluginLoader 写入方，BroadcastDispatcher 跨程序集消费；DI 注册移到 `PluginServiceExtensions.AddCortanaPlugin`
> - **OQ2 删除直发式 grace 期承诺** —— v1.4 §6 "1.5.0 grace" 与 §3.2 "入口仅拦截 type==event" 自相矛盾；外部第三方语音插件不存在，承诺无现实对象，v1.4.0 起直发式不再支持
> - **OQ3 chat.\* op 完整重命名为 conversation.chat.\*（A2 全量）** —— 6 处宿主代码 + 公开 API 文档 + 接入模板同步迁移；删除 `chat → conversation` 别名表，op 首段严格等于 topic
> - **OQ4 memory/model topic plugin→plugin event 必须用子命名空间** —— `memory.event.*` / `model.event.*`，避免与 `memory.context.supply.*` RPC / `model.capability.*` RPC 命名冲突
> - **OQ5 VoiceEventBridge 加 NormalizeVoiceOp .v1 后缀容忍** —— 旧版仓库三件套（无 .v1）发包时 BroadcastDispatcher 仍能桥接到 IPublisher，防 UI 静默失声
> - **R-CHAT-MISFIRE 受保护 op 黑名单防御** —— BroadcastDispatcher 加 `IsProtectedOp` 守卫，`conversation.chat.message.send` / `conversation.chat.generation.stop` / `*.history.replay` / `model.capability.request` / `memory.context.supply.*` 等 RPC/控制 op 被误标 type=event 时立即 LogWarning 拒收，避免对话链路无故断流（与 OQ3 A2 重命名一致）
> - **R-PAYLOAD-AMP 单帧 1MB 上限** —— BroadcastDispatcher 入口加单帧字节上限校验（与现有 ReadTextMessageAsync 16K 读缓冲是两回事），防大 payload × N 订阅者 OOM
> - **R-DEAD-SUBSCRIBER 慢订阅者死链主动清理** —— SendWithTimeoutAsync OperationCanceledException 分支调 `_subscriptions.Remove(cid)` + `_pluginBusConnections.RemoveAndCloseAsync(cid)`，避免 1 秒超时后死链残留
> - **R-AOT-CMD AOT 验证命令矩阵修订** —— ClassLibrary 不能跑 PublishAot，库改用 `<IsAotCompatible>true</IsAotCompatible>` + Build 触发 analyzer，可执行项目（UI / NativeHost / 三件套）单独 publish 验证
> - **R-FILES 文件清单补全** —— 补 PluginManifest.cs / NetworkServiceExtensions.cs / PluginServiceExtensions.cs / WebSocketPluginBusServerMeetingIntegrationTests.cs 四处漏列项
> - **R-ORDERING v1 不保证事件 ordering** —— Task.Run + WhenAll 多帧并发投递不保序，partial 类高频流可能错序送达；订阅插件按"总是用最新值覆盖"模式编写，详见 §四 范围边界与 §九 修订记录

---

## 一、要解决的问题

当前 PluginBus 是**星形拓扑**，仓库已经累积 1 个真实场景需要"插件 A 状态变更 → 插件 B / C / D 各自响应"：

- 语音插件触发会话开始 → 字幕 / 桌宠 / 日志插件联动

未来还会有：

- 记忆插件刷新索引 → 上下文 / 检索插件重载
- 文件插件感知工作区变更 → 索引 / AI 辅助插件级联处理

注意：**记忆插件目前的"长期记忆供应"链路是同步 RPC（宿主 → 记忆插件 → 等响应），不属于 pub-sub 场景**。要让记忆刷新驱动其他插件，未来会通过本方案新增独立的 `memory.index.updated.v1` pub-sub 事件，不复用现有 RPC 通道。

为什么不能用 `host.llm.invoke.v1` 那一套 request / response 替代？模型能力是同步请求-响应、一对一、需等待结果；事件广播是单向、一对多、fire-and-forget，二者语义不重叠（详见 §2.0 协议层语义分层）。

---

## 二、核心设计

### 2.0 协议层语义分层（PluginBus type 字典）

新机制成立的前提是**按通信语义为每种 type 划清边界**。这张表既是现状盘点，也是未来所有新协议帧的判断标准 —— 新增协议先归入某一档，不要发明跨档混合体。

| type | 通信语义 | 需 RequestId | 需等响应 | 一对几 | 现有用法 |
| --- | --- | --- | --- | --- | --- |
| `request` | 同步请求（RPC） | ✅ | ✅ 阻塞 | 1 ↔ 1 | `model.capability.request`、`memory.context.supply.request`、`replay` |
| `response` | 同步响应 | ✅（关联请求） | — | 1 ↔ 1 | `model.capability.response`、`memory.context.supply.response` |
| `event` | 异步事件（pub-sub） | ❌ | ❌ fire-and-forget | 1 ↔ N | **已存在**：host→plugin（[PluginBusMessageFactory.CreateChatEvent](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusMessageFactory.cs#L15-L30) + 三处 Relay 服务）、plugin→host（voice 三件套上行）；**本方案扩展为支持 plugin→plugin** |
| `subscribe` | 控制平面 | — | — | — | 客户端声明 topic / capabilities，**本方案追加 subscribedOps** |
| `send` / `stop` / `system.notice` | 数据流 / 控制 | — | — | — | 聊天输入与控制 |
| `pong` | 心跳 | — | — | — | 心跳响应 |

**强制原则**：

- 新通信模式先决定它属于哪一档，再决定 type
- 同步 RPC 不要改造成 `event`（会丢掉 RequestId / 错误码 / 阻塞等待语义）
- pub-sub 事件只用 `type: event`，不复用 `request` 帧
- 不允许"看起来像 event 实际是 request"的混杂设计

**关于 voice 插件历史发包格式**：现有 voice 三件套实际同时存在两种 `event` 帧形态——

- **信封式**（推荐，与 host→plugin 一致）：`{ "type": "event", "eventType": "voice.xxx", ... }`，例如 [TtsPluginBusEvent](../../../Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/PluginBus/TtsPluginBusEvent.cs#L8) 默认 `Type = "event"`
- **直发式**（历史遗留）：`{ "type": "voice.xxx", "eventType": "voice.xxx", ... }`，例如 [KwsPluginBusClient.PublishAsync:49-58](../../../Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/PluginBus/KwsPluginBusClient.cs#L49-L58) 把 `Type` 与 `EventType` 都赋为 op 字符串

现状 [PluginBusVoiceEventDispatcher.ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 通过 `eventType > op > type` 优先级同时兼容两种。**1.4.0 起有意变更优先级为 `op > eventType > type`**，把 `op` 提到首位作为权威标识；`eventType` 保留向下兼容（缺省 op 时 fallback；二者同时存在且不一致时以 op 为准），voice 三件套统一收敛到信封式（详见 §3.5）。

### 2.1 协议帧（PluginBus protocol 1.4.0，复用 PluginBusEventMessage）

**复用现有** [PluginBusEventMessage](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93)，**新增 2 个可选字段**（`hopCount` 与 `sourcePluginId`）。完整帧示例（plugin→plugin 方向）：

```json
{
  "type": "event",
  "protocol": "cortana.plugin-bus",
  "version": "1.4.0",
  "topic": "voice",
  "op": "voice.kws.detected.v1",
  "eventType": "voice.kws.detected.v1",
  "source": "voice_kws_sherpa",
  "timestamp": 1718323200000,
  "hopCount": 0,
  "payload": { "text": "小娜", "sessionId": "..." }
}
```

#### 2.1.1 字段优先级与兼容规则

宿主侧 dispatcher 解析按下表优先级（**1.4.0 有意变更优先级，把 op 提到首位**——旧版 [ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 是 `eventType > op > type`，新版改为 `op > eventType > type`）：

| 字段 | 1.4.0 之后语义 | 兼容性 |
| --- | --- | --- |
| `op` | **权威标识**，必填，形如 `{namespace}.{name}.v{n}` | 1.4.0 起为第一优先级；与 eventType 同时存在且不一致时，以 op 为准 |
| `eventType` | 历史字段，与 op 等价 | 老插件无 op 时 fallback 到 eventType（向下兼容 1.3.0） |
| `type` | 帧类型 | 1.4.0 起插件统一发 `"event"`；老 voice 三件套直发式（`type=voice.xxx`）由仓库内同 PR 升级到信封式；外部第三方语音插件不存在，1.4.0 起直发式不再支持（v1.5 OQ2） |
| `topic` | 粗粒度领域 | 必填；缺省时由宿主从 `op` 第一段反推 |
| `source` | 来源标识（兼容字段） | host→plugin 时 = `"host"`（用于 §3.2 host 排除判别）；plugin→plugin 时若无 sourcePluginId，fallback 用 source。**v1 仅作弱声明校验依据，不作安全依据** |
| `sourcePluginId`（新增字段） | plugin→plugin 推荐填的 pluginId | 与 `source` 二者必有其一；插件推荐填 `sourcePluginId`，宿主自身广播填 `source = "host"`。**v1 仅自报，仅作弱声明校验依据；v2 做可信绑定** |
| `hopCount`（新增字段） | 转发跳数 | 缺省 0；宿主收到后判 `>= 8` 即丢弃并 LogWarning，转发时重写为原值 + 1（详见 §5.6） |
| `payload` | 业务负载（JsonElement 透传） | 必须 JSON-serializable，禁止函数 / 类实例 |
| `requestId` / `target` | 现有字段 | 在 plugin→plugin 场景中保留为可选 |

#### 2.1.2 字段约束

- `op` 命名空间段（`{namespace}`）标识领域（`voice` / `memory` / `workspace` 等），由订阅注册器按段反查订阅者；**op 首段严格等于 topic**（v1.5 OQ3 后无别名表，详见 §2.3）
- `op` 必须出现在发布方 `plugin.json.publishedOps` 中（v1 仅 LogWarning 不强拒，与 5B-D 一致）
- `sourcePluginId` 由插件自报，v1 不做可信绑定（详见 §5.3，v2 计划）
- **单帧 ≤ 1 MB**（v1.5 R-PAYLOAD-AMP 修订）：BroadcastDispatcher 入口加显式字节上限校验，超过即丢弃 + LogWarning。注意 [WebSocketPluginBusServerService:381](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L381) 的 16K 是 `ReceiveAsync` 读缓冲，不是消息上限——本方案引入 plugin→plugin 广播后，1 个 100 MB 事件 × N 订阅者会被复制到 N 份 forwardedJson，瞬时 OOM；必须在 dispatcher 入口二次防御（具体阈值由用户拍板，建议 64KB-1MB 可配）

#### 2.1.3 PluginBusEventMessage 改动

```csharp
// Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs:93-127
internal sealed record PluginBusEventMessage
{
    // ... 现有 11 个字段保持不变 ...

    // ★ 新增字段：plugin→plugin 转发跳数兜底
    [JsonPropertyName("hopCount")]
    public int? HopCount { get; init; }

    // ★ 新增字段：与 source 互补，推荐插件填这个
    [JsonPropertyName("sourcePluginId")]
    public string? SourcePluginId { get; init; }
}
```

`WebSocketJsonContext` 已注册过该类型（[第 15 行](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L15)），新字段随源生成器自动产出，**无需新增 [JsonSerializable] 行**。详见 §3.6 AOT。

### 2.2 subscribe 帧扩展（开发态契约 vs 运行态过滤分层）

在不破坏现有 1.3.0 语义的前提下，给 [HandleMessageAsync:462-502](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L462-L502) 已解析的 subscribe 帧追加一个可选字段:

```json
{
  "type": "subscribe",
  "protocol": "cortana.plugin-bus",
  "topics": ["voice"],
  "capabilities": [],
  "subscribedOps": ["voice.kws.detected.v1", "voice.tts.completed.v1"]
}
```

#### 2.2.1 subscribedOps 在两个维度的语义

为了避免 v1.2 的"自相矛盾"（manifest 强制声明 vs 运行帧通配符），1.3 起严格按下表分层：

| 维度 | manifest（`plugin.json.subscribedOps`） | 运行帧（subscribe 帧的 `subscribedOps`） |
| --- | --- | --- |
| 谁声明 | 插件作者（开发态） | 插件运行实例（运行态） |
| 缺省语义 | **必须显式列出**（无通配符） | **缺省 = 收 topic 下全部 op** |
| 加载期校验 | LogWarning 不一致 | — |
| 运行期作用 | 仅作开发态契约对账 | 宿主用来运行时过滤投递 |
| 修改方式 | 改源码重新发布 | 重连时下发 |

这样既保留 manifest 的契约价值（运维可审计），又保留运行时灵活性（订阅插件可短期内只关心子集）。

#### 2.2.2 兼容性

老插件不发 `subscribedOps`，行为与 1.3.0 完全一致，**向下兼容**。

> **本版砍掉的字段**（与 v1.1 比较）：
>
> - ~~`replayLatest`~~ —— 当前环境下"启动后立刻产生事件"不存在，迟订阅补偿是伪需求
> - ~~`throttleMs`~~ —— voice.\* 事件低频，不需要节流；高频场景出现时再加（v2 候选）

### 2.3 插件清单声明（[Plugin] 体系扩展）

新增两个 attribute，与现有 [`[RequiredHostCapability]`](../../../Src/Plugins/Netor.Cortana.Plugin.Native/Attributes/PluginAttribute.cs) 同样支持多次叠加:

```csharp
[Plugin(Id = "sherpa_kws", Name = "Sherpa KWS", Version = "1.0.0",
        Capabilities = ["voice.kws"])]
[PublishesEvent("voice.kws.detected.v1",
                Description = "检测到唤醒词")]
public static partial class Startup { }

[Plugin(Id = "subtitle_overlay", Name = "Subtitle Overlay", Version = "1.0.0")]
[SubscribesEvent("voice.stt.partial.v1", Reason = "实时显示识别中文本")]
[SubscribesEvent("voice.stt.final.v1", Reason = "确认最终字幕")]
[SubscribesEvent("voice.tts.subtitle.v1", Reason = "显示 TTS 字幕")]
public static partial class Startup { }
```

Generator 将这些声明写入 `plugin.json` 新增字段（**对象数组形态**，便于审计；每个元素含 op + description/reason）：

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

**Schema 与 C# 模型分层**（F-SCHEMA 修订）：

- `plugin.json` schema：对象数组（op + description/reason），便于运维审计与 manifest 阅读
- `PluginManifest` C# 模型：用 record 数组承载（如 `PublishedOpDeclaration[]` / `SubscribedOpDeclaration[]`），保持与 JSON 结构对齐
- `PluginManifestRegistry.Register` 接口签名：保留 `IEnumerable<string>` 取 op 集合（运行时只用到 op 字符串做声明校验），**调用方在 PluginLoader 内从对象数组抽出 op 字符串后传入**
- Generator 输出对象数组形态，与上述 C# 模型一致

attribute 字段保持最小：v1 不引入 `RetainLatest` / `ThrottleMs`（与 §2.2 砍字段一致）。

### 2.4 宿主转发器（PluginBusBroadcastDispatcher）

新增 [`Src/Netor.Cortana.Networks/WebSockets/PluginBusBroadcastDispatcher.cs`](../../../Src/Netor.Cortana.Networks/WebSockets/)，作为 `type: event` 帧的**唯一入口**（不再走"旁路 hook"，详见 §5.4）。职责：

1. **受保护 op 防御（v1.5 R-CHAT-MISFIRE 新增）**：在所有解析前先做黑名单守卫；命中 `IsProtectedOp(op)` 立即 LogWarning 并丢弃，不进入后续路由。受保护 op 列表 = 现有 RPC / 控制 op 全集：`conversation.chat.message.send` / `conversation.chat.generation.stop` / `system.notice` / `conversation.history.replay` / `workflow.history.replay` / `meeting.history.replay` / `replay` / `model.capability.request` / `model.capability.response` / `memory.context.supply.request` / `memory.context.supply.response` / `memory.context.supply.error`。原因：客户端 SDK bug 把 chat 帧误标 `type=event` 时，新分支会先于 chat 分支命中并按 op namespace 路由到 conversation topic，可能把控制帧错投给广播订阅者；显式拒收避免对话链路无故断流
2. **来源辨识（F3 + F-SOURCE）**：
   - **路由层（v1 安全依据）**：仅信任 `clientId` 排除自回环（订阅者集合中过滤掉 `sourceClientId`），**不信任** frame 自报的 `source` / `sourcePluginId` 字段
   - **host 排除判别**：仅靠 `source == "host"` 判别 host→plugin 帧，命中即拒绝并 LogWarning（host 自身的 [BroadcastPluginBusAsync](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L242) 已确保不进 HandleMessageAsync 上行；客户端冒充 `source = "host"` 上行被立即拒）
   - **弱声明校验**：从 frame 读 `sourcePluginId`，缺省 fallback 到 `source`，与发布方 `plugin.json.publishedOps` 比对，**仅 LogWarning，不作安全依据**
   - v2 才做 clientId↔pluginId 可信绑定（详见 §5.3）
3. **op 解析（F2 + F-PRIORITY）**：v1.4.0 **有意变更优先级**为 `op > eventType > type`（旧 ReadEventType 是 `eventType > op > type`）；老插件无 op 时 fallback 到 eventType，向下兼容 1.3.0；二者都缺时 type 字段作最后兜底；全部缺则 LogWarning 后丢弃。
4. **单帧字节上限（v1.5 R-PAYLOAD-AMP 新增）**：dispatcher 入口检查 raw json 字符串长度，超过 1MB（可配）立即丢弃 + LogWarning。**重要**：注意 ReadTextMessageAsync 16K 是读缓冲不是消息上限——本方案引入 plugin→plugin 广播后 1 个 100MB 事件 × N 订阅者会被复制到 N 份 forwardedJson 瞬时 OOM，必须在此处二次防御
5. **hopCount 兜底**：判 `hopCount >= 8` 立即丢弃并 LogWarning（详见 §5.6）。
6. **路由**：从 op 解析 namespace 段为 topic → 调用 `_subscriptionRegistry.GetSubscribers(topic)` 拿订阅者 → 按订阅者 `subscribedOps` 二次过滤 → **排除 sourceClientId**。
7. **转发（F6 后台投递 + F-LIFETIME + v1.5 R-DEAD-SUBSCRIBER）**：业务侧不等订阅方响应，但宿主等待网络投递完成（每个订阅者独立 1 秒超时）；**同步阶段**先把转发载荷序列化为 string + 解析出目标 clientId 列表（避免 JsonElement 生命周期坑——HandleMessageAsync return 后 `using var doc` 会被 Dispose），**再投后台 Task.Run 只跑 SendAsync**，HandleMessageAsync 立刻返回，不阻塞 ReceiveLoop。
   - **R-DEAD-SUBSCRIBER**：SendWithTimeoutAsync 触发 OperationCanceledException 时，[WebSocketConnectionManager.SendAsync:94](../../../Src/Netor.Cortana.Networks/WebSockets/Connections/WebSocketConnectionManager.cs#L94) 的 `when (!cancellationToken.IsCancellationRequested)` 守卫被 1 秒 linked CTS cancel 击穿，不会触发自动 RemoveAndCloseAsync。dispatcher 必须**主动**调 `_subscriptions.Remove(cid)` + `_pluginBusConnections.RemoveAndCloseAsync(cid)`，否则死链残留每次广播都重新尝试 1 秒超时
8. **voice 桥接**：当 `op.namespace == "voice"` 时，**额外**调用内部 `VoiceEventBridge.Bridge(op, frame)` 把事件桥接到宿主 `IPublisher` 的 `Events.OnSttPartial` 等，取代现有 [PluginBusVoiceEventDispatcher](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs) 的拦截器角色。voice 桥接在同步阶段直接读 frame，方法返回前完成，无需 Clone（详见 03 §2.2）。
   - **v1.5 OQ5 NormalizeVoiceOp 容忍**：Bridge 内部对 op 做归一化（`voice.xxx`/`voice.xxx.vN` 自动归一到当前匹配的 case），兼容旧版仓库三件套发包（无 .v1）的 envelope 帧，防止 op 落 default 分支导致 UI 字幕静默失声（详见 §5.4）

伪代码（v1.4 修订版，含 F-LIFETIME 同步阶段载荷固化）：

```csharp
internal sealed class PluginBusBroadcastDispatcher(
    PluginBusSubscriptionRegistry subscriptions,
    PluginManifestRegistry manifests,
    VoiceEventBridge voiceBridge,
    Func<string, string, CancellationToken, Task> sendAsync,
    ILogger logger)
{
    private const int MaxHopCount = 8;
    private const int PerSubscriberTimeoutMs = 1000;

    /// <summary>
    /// 调用方契约：frame 是 HandleMessageAsync 内 `using var doc = JsonDocument.Parse(json)` 的 RootElement，
    /// 仅在本方法 await 返回前有效。所有需要带入后台的 JsonElement 必须先 Clone() 或序列化为 string。
    /// </summary>
    public Task HandleAsync(string sourceClientId, JsonElement frame, CancellationToken ct)
    {
        // F3：host→plugin 帧不走本路径
        var source = ReadString(frame, "source");
        if (string.Equals(source, "host", StringComparison.Ordinal))
        {
            logger.LogWarning("Client uploaded host-source event, ignored. ClientId={Id}", sourceClientId);
            return Task.CompletedTask;
        }

        // F2/F-PRIORITY：op > eventType > type 优先级（1.4.0 有意变更）
        var op = ReadString(frame, "op")
            ?? ReadString(frame, "eventType")
            ?? ReadString(frame, "type");
        if (string.IsNullOrWhiteSpace(op)) { logger.LogWarning("Event missing op/eventType"); return Task.CompletedTask; }

        var hops = frame.TryGetProperty("hopCount", out var h) ? h.GetInt32() : 0;
        if (hops >= MaxHopCount) { logger.LogWarning("hop limit. Op={Op}", op); return Task.CompletedTask; }

        // F-SOURCE：弱声明校验，仅 LogWarning，不作安全依据
        var sourcePluginId = ReadString(frame, "sourcePluginId") ?? source ?? "";
        ValidatePublisherDeclaration(sourcePluginId, op);

        var topic = ExtractNamespace(op);

        // F-LIFETIME：同步阶段固化载荷与目标列表，避免后台访问已 Dispose 的 JsonElement
        // 注：BuildForwardFrameJson 仅作示意，最终方法名 / 签名以 03 §2.3 RewriteHopCount 为准
        var forwardedJson = RewriteHopCount(frame, hops + 1);   // 返回 string，不返回 JsonElement
        var targets = subscriptions.GetSubscribers(topic)
            .Where(cid => !string.Equals(cid, sourceClientId, StringComparison.Ordinal))
            .Where(cid => subscriptions.MatchesSubscribedOp(cid, op))
            .ToArray();

        // voice 桥接在同步阶段直接读 frame，方法返回前完成，无需 Clone
        // （VoiceEventBridge.Bridge 是同步方法，详见 03 §2.2 与 §3.2）
        if (string.Equals(topic, "voice", StringComparison.Ordinal))
        {
            voiceBridge.Bridge(op, frame);
        }

        // F6：后台投递，HandleMessageAsync 立刻返回；后台只用 owned 数据
        _ = Task.Run(async () =>
        {
            var tasks = targets.Select(cid => SendWithTimeoutAsync(cid, op, forwardedJson, ct));
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }, ct);

        return Task.CompletedTask;
    }

    private async Task SendWithTimeoutAsync(string cid, string op, string payload, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(PerSubscriberTimeoutMs);
        try { await sendAsync(cid, payload, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { logger.LogWarning("Forward timeout. Target={Cid} Op={Op}", cid, op); }
        catch (Exception ex)               { logger.LogWarning(ex, "Forward failed. Target={Cid} Op={Op}", cid, op); }
    }
}
```

`VoiceEventBridge` 把现有 [PluginBusVoiceEventDispatcher.cs:35-85](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L35-L85) 那段 switch 逻辑搬出来包成的内部组件，不再做帧识别（识别由 BroadcastDispatcher 统一做）；`voice.tts.greeting_ready` 显式列 case 保持"吞掉不发 IPublisher"行为（详见 §5.4 与 03 §3.3）。

---

## 三、改动点

### 3.1 协议层

| 位置 | 改动 |
| --- | --- |
| [CortanaWsEndpoints.cs:22](../../../Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs#L22) | `PluginBusVersion` 升至 `"1.4.0"`；不新增 `EventFrameType` 常量（沿用现有字面量 `"event"`） |
| [WebSocketJsonContext.cs:93-127 PluginBusEventMessage](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93) | **复用现有 record**，新增 2 个可选属性：`HopCount`（`int?`）、`SourcePluginId`（`string?`）；`[JsonSerializable]` 已注册（[第 15 行](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L15)），新字段随源生成器自动产出，**无需追加注册行** |

**不新建** `PluginBusEventFrame` / `PluginBusEventSubscribeOptions` 等 POCO（v1.2 误判，已纠正）。

### 3.2 宿主转发层

| 文件 | 改动 |
| --- | --- |
| [WebSocketPluginBusServerService.HandleMessageAsync](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L414) | 在 pong 分支后、subscribe / chat / model / memory 等现有分支**之前**新增：`if (string.Equals(type, "event", StringComparison.Ordinal)) { await _broadcastDispatcher.HandleAsync(id, root, ct); return; }`；该分支内部按 §2.4 F3 自行排除 `source == "host"` 的帧。**入口仅拦截 `type == "event"`，不为老 voice 直发式（`type=voice.xxx`）额外加分支**——同 PR 内仓库 voice 三件套全升级到信封式（详见 §3.5），外部第三方语音插件不存在，1.4.0 起直发式不再支持（v1.5 OQ2），宿主侧不留遗留兜底 |
| 同文件 ctor | 删除 `_voiceEventDispatcher = new PluginBusVoiceEventDispatcher(...)`；新建 `_manifestRegistry` / `_voiceEventBridge` / `_broadcastDispatcher` 三个字段（DI 单例 `PluginManifestRegistry` 注入） |
| 同文件 `_voiceEventDispatcher.TryHandle` 现有调用点（约 [429-432 行](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L429-L432) 附近，PR 实施时按当前行号定位） | 整个 if 块删除：voice 帧识别下沉到 BroadcastDispatcher 的 voice bridge 内部 |
| 同文件 subscribe 分支 [462-502](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L462-L502) | 新增解析 `subscribedOps` 字段，写入扩展后的 `PluginBusSubscriptionRegistry`（缺省全收语义，详见 §2.2） |
| [PluginBusSubscriptionRegistry](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusSubscriptionRegistry.cs) | 新增 `SetSubscribedOps(clientId, ops)` / `GetSubscribedOps(clientId)` / `MatchesSubscribedOp(clientId, op)`；同步更新 `Remove` / `Clear` 清理新字段 |
| 新建 `PluginBusBroadcastDispatcher.cs` | §2.4 描述的核心转发器（含**受保护 op 黑名单防御** / F3 host 排除 / F2 op 优先 / **单帧 1MB 上限** / F6 后台投递 + 1 秒超时 + **死链主动清理** / hopCount 兜底；voice 桥接 `voice.*` op 自动归一为 `.v1`） |
| 新建 `Voice/VoiceEventBridge.cs` | 把 [PluginBusVoiceEventDispatcher:35-85](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L35-L85) 的 switch 逻辑搬过来，移除帧识别部分；只做"按 op 桥接到 `Events.OnXxx`"；`voice.tts.greeting_ready` 显式列 case + LogDebug（与旧 dispatcher 行为对齐） |
| 删除 [`Voice/PluginBusVoiceEventDispatcher.cs`](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs) | 整文件删除（被 `BroadcastDispatcher` + `VoiceEventBridge` 取代） |
| 新建 `Src/Netor.Cortana.Plugin/PluginManifestRegistry.cs` | **v1.5 OQ1 修订**：从 Networks 改放 Plugin 程序集贴近 PluginLoader 写入方；DI 单例（在 `PluginServiceExtensions.AddCortanaPlugin` 注册），由 PluginLoader 在 load/reload/unload 时调用 `Register` / `Unregister`；供 BroadcastDispatcher 弱声明校验。BroadcastDispatcher 跨程序集消费走现有间接路径 Networks → AI → Plugin，编译期可见 Plugin 公开类型，新增直接 ProjectReference 是依赖加固而非编译必需（详见 03 §四） |
| 新建 `Tests/.../PluginBusBroadcastDispatcherTests.cs` | 参考 [PluginBusVoiceEventDispatcherTests](../../../Tests/Netor.Cortana.Networks.Tests/PluginBusVoiceEventDispatcherTests.cs) 风格：覆盖正常转发 / 自回环排除 / source==host 拒绝 / hopCount 上限 / subscribedOps 二次过滤 / voice 桥接 + greeting_ready 不发 IPublisher 七条主路径 |
| **删除** [`PluginBusVoiceEventDispatcherTests.cs`](../../../Tests/Netor.Cortana.Networks.Tests/PluginBusVoiceEventDispatcherTests.cs) | 由新测试取代；`TryHandle_TtsGreetingReady_ReturnsTrue` 这条用例对应迁移为新测试矩阵 V10 |
| [Core/PluginManifest.cs](../../../Src/Netor.Cortana.Plugin/Core/PluginManifest.cs) | **v1.5 R-FILES**：新增 `PublishedOps` / `SubscribedOps` 两个 `IReadOnlyList<>` 字段 + 两个 record `PublishedOpDeclaration` / `SubscribedOpDeclaration`（含 `op` + `description/reason`，对应 plugin.json 对象数组 schema）；`Normalize()` 兜底 `?? []` |
| [Core/PluginManifestJsonContext.cs](../../../Src/Netor.Cortana.Plugin/Core/PluginManifestJsonContext.cs) | **v1.5 R-FILES**：追加 2 条 `[JsonSerializable(typeof(PublishedOpDeclaration))]` / `[JsonSerializable(typeof(SubscribedOpDeclaration))]` 注册 |
| [PluginServiceExtensions.cs](../../../Src/Netor.Cortana.Plugin/PluginServiceExtensions.cs) | **v1.5 R-FILES + OQ1**：`AddCortanaPlugin` 新增 `services.AddSingleton<PluginManifestRegistry>()`（DI 单例就近 PluginLoader 写入方） |
| `Src/Netor.Cortana.Networks/NetworkServiceExtensions.cs`（按现有模块 DI 入口路径） | **v1.5 R-FILES**：新增 DI 注册 `PluginBusBroadcastDispatcher` / `VoiceEventBridge`（PR 实施时确认与现有 `Add*` 方法一致；如不存在则直接复用 `WebSocketPluginBusServerService` 的注册位） |
| [WebSocketPluginBusServerMeetingIntegrationTests.cs:49](../../../Tests/Netor.Cortana.Networks.Tests/WebSocketPluginBusServerMeetingIntegrationTests.cs#L49) | **v1.5 R-FILES**：`WebSocketPluginBusServerService` ctor 增 `PluginManifestRegistry` 参数后，本测试 ctor 调用同步加构造参数（其他 Networks/Tests 内 ctor 调用同 PR 一并改动） |

### 3.3 插件 SDK 与 Generator

| 文件 | 改动 |
| --- | --- |
| [`Src/Plugins/Netor.Cortana.Plugin.Native/Attributes/`](../../../Src/Plugins/Netor.Cortana.Plugin.Native/Attributes/) | 新增 `PublishesEventAttribute` / `SubscribesEventAttribute`（`AttributeUsage` 允许多次叠加） |
| [`Src/Plugins/Netor.Cortana.Plugin.Process/Attributes/`](../../../Src/Plugins/Netor.Cortana.Plugin.Process/Attributes/) | 同步新增同名 attribute |
| [NativePluginGenerator](../../../Src/Plugins/Netor.Cortana.Plugin.Native.Generator/NativePluginGenerator.cs) | 扫描 `[PublishesEvent]` / `[SubscribesEvent]`，写入 `plugin.json.publishedOps` / `subscribedOps` |
| [ProcessPluginGenerator](../../../Src/Plugins/Netor.Cortana.Plugin.Process.Generator/ProcessPluginGenerator.cs) | 同步处理 |
| [PluginManifestJsonContext.cs](../../../Src/Netor.Cortana.Plugin/Core/PluginManifestJsonContext.cs) | 追加 `[JsonSerializable(typeof(PublishedOpDeclaration))]` 与 `[JsonSerializable(typeof(SubscribedOpDeclaration))]`（现有已注册 `PluginManifest` / `RequiredHostCapability` / `PluginSettingDescriptor` 三种） |
| 插件 SDK PluginBus 客户端 | 新增 `PublishEventAsync(string op, JsonElement payload, ct)` / `SubscribeEventAsync(string op, Func<JsonElement, Task> handler)` 两个公开 API；签名严格用 `JsonElement`（AOT 约束，详见 §3.6） |

### 3.4 文档

| 文档 | 改动 |
| --- | --- |
| [插件清单与能力声明.md](../../三方对接文档/插件开发指南/插件清单与能力声明.md) | 新增 §"事件契约声明"章节，对照 `[PublishesEvent]` / `[SubscribesEvent]` 字段语义 |
| [插件开发总览.md](../../三方对接文档/插件开发指南/插件开发总览.md) | 在"能力声明"章节后追加"事件总线"章节 + §2.0 协议层语义分层表 |
| [语音插件开发指南.md](../../三方对接文档/插件开发指南/语音插件开发指南.md) §六 | 把 `voice.*` 事件 op 清单全部更新为 `.v1` 形态；现有"信封式 vs 直发式"两种格式说明改为"统一信封式（type=event）+ op 字段"；不再有裸帧 / 新格式之分（详见 §2.0 末段） |
| 新建 [插件事件总线开发指南.md](../../三方对接文档/插件开发指南/) | 完整流程：声明 → 发布 → 订阅；以语音三件套迁移作为完整示例 |

### 3.5 仓库内 voice 插件迁移清单（与宿主同 PR）

三个仓库内插件随宿主一起升级，作为本方案 PR 的强制配套改动。**实际改动很小**：现有代码已用 `type=event` 或直发式 + `eventType` 字段（详见 §2.0 末段说明），本次只需 op 升 .v1 后缀 + 发布方信封式收敛 + attribute 声明。

| 插件 | 路径 | 改动要点 | 当前 op | 升级后 op |
| --- | --- | --- | --- | --- |
| Sherpa KWS | [Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa](../../../Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/) | 入口加 `[PublishesEvent("voice.kws.detected.v1", ...)]`；[KwsPluginBusClient.PublishAsync:49-58](../../../Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/PluginBus/KwsPluginBusClient.cs#L49-L58) 把 `Type = eventType` 改为 `Type = "event"`（信封式）；EventType 加 .v1 后缀；POCO 默认 `SourcePluginId = "voice_kws_sherpa"` 保留即可 | `voice.kws.detected` | `voice.kws.detected.v1` |
| Sherpa STT | [Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa](../../../Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/) | 同 KWS，[SttPluginBusClient.PublishAsync:55-64](../../../Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/PluginBus/SttPluginBusClient.cs#L55-L64) 改 `Type = "event"`；attribute 覆盖 `partial.v1` / `final.v1` / `stopped.v1` | `voice.stt.partial` 等 | `voice.stt.partial.v1` 等 |
| Melo TTS | [Plugins/Src/Cortana.Plugins.Voice.Tts.Melo](../../../Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/) | [TtsPluginBusEvent.cs:8](../../../Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/PluginBus/TtsPluginBusEvent.cs#L8) `Type` 已默认 `"event"` 不需改；[TtsPluginBusClient:47-69](../../../Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/PluginBus/TtsPluginBusClient.cs#L47-L69) `EventType` 加 .v1 后缀；attribute 覆盖 `started.v1` / `subtitle.v1` / `completed.v1` / `greeting_ready.v1` / `error.v1` | `voice.tts.started` / `subtitle` / `completed` / `greeting_ready` / `error` | 各自加 `.v1` |

每个插件 PR 检查项：

- ✅ `[PublishesEvent]` 覆盖该插件 client 实际使用的所有 op（KWS 1 个、STT 3 个、TTS 5 个）
- ✅ 发包帧 `type` 字段统一为 `"event"`（信封式收敛），grep 验证无 `Type = eventType` 残留
- ✅ `eventType` / `op` 字段值统一为 `voice.xxx.v1` 形态
- ✅ self-test 对每个 op 至少调用一次对应的 `PublishXxxAsync`
- ✅ `plugin.json` 输出 `publishedOps` 字段非空且与 attribute 一致
- ✅ **直发式 1.4.0 起不再支持**：仓库内 voice 三件套同 PR 升级到信封式后无遗留直发式帧；外部第三方语音插件不存在，无现实兼容对象（v1.5 OQ2）

**注意**：现有 voice 三件套客户端 POCO 内 `Protocol` 默认值为 `"cortana.pluginbus"`（无破折号，详见 [KwsPluginBusEvent.cs:8](../../../Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/PluginBus/KwsPluginBusEvent.cs#L8)），与宿主常量 `"cortana.plugin-bus"` 不一致，但宿主侧未校验该字段，本方案**不修复**此偏差以保持改动最小（如要修复，单独开 PR）。

### 3.6 AOT 改动清单（强约束）

整个软件 AOT 编译，所有改动必须显式可静态分析。

#### 3.6.1 WebSocketJsonContext 不需要新增注册

`PluginBusEventMessage` 已注册（[WebSocketJsonContext.cs:15](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L15)），新增 `HopCount` / `SourcePluginId` 字段后源生成器自动产出新的 metadata，**无需追加 `[JsonSerializable]` 行**。

```csharp
// Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs
[JsonSerializable(typeof(PluginBusEventMessage))]   // ← 已存在第 15 行
internal partial class WebSocketJsonContext : JsonSerializerContext;

internal sealed record PluginBusEventMessage
{
    // ... 现有 11 字段保持不变 ...

    [JsonPropertyName("hopCount")]      public int? HopCount { get; init; }       // 新增
    [JsonPropertyName("sourcePluginId")] public string? SourcePluginId { get; init; } // 新增
}
```

约束：

- 新增属性类型限制在 `int?` / `string?`（已 AOT 验证）
- `Payload` 字段维持 `JsonElement` 透传，宿主**不**反序列化为业务类型
- 三方插件如要发布业务 POCO，必须自带 `[JsonSerializable]` 的 context（参考 [TtsPluginBusEvent](../../../Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/PluginBus/TtsPluginBusEvent.cs) 模式）

#### 3.6.1.1 PluginManifestJsonContext 需追加两条注册

`plugin.json` 解析侧的 [PluginManifestJsonContext](../../../Src/Netor.Cortana.Plugin/Core/PluginManifestJsonContext.cs) 当前注册了 `PluginManifest` / `RequiredHostCapability` / `PluginSettingDescriptor` 三种。本方案新增 `PublishedOpDeclaration` / `SubscribedOpDeclaration` 两个 record（详见 03 §4.4 F-SCHEMA），必须追加注册：

```csharp
// Src/Netor.Cortana.Plugin/Core/PluginManifestJsonContext.cs
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(RequiredHostCapability))]
[JsonSerializable(typeof(PluginSettingDescriptor))]
[JsonSerializable(typeof(PublishedOpDeclaration))]   // ★ 新增
[JsonSerializable(typeof(SubscribedOpDeclaration))]  // ★ 新增
public partial class PluginManifestJsonContext : JsonSerializerContext;
```

两个 record 类型仅含 `string` + `string?`，无嵌套，AOT 安全。

#### 3.6.2 SDK 公开 API 必须 JsonElement-only

```csharp
// ✅ 允许
Task PublishEventAsync(string op, JsonElement payload, CancellationToken ct);
Task SubscribeEventAsync(string op, Func<JsonElement, Task> handler);

// ❌ 禁止
Task PublishEventAsync<T>(string op, T payload, ...);   // 触发反射
```

业务侧自己用 `[JsonSerializable]` 注册过的 context 序列化 POCO 后再调 SDK。

#### 3.6.3 禁止的反射 API（PR 检查）

- ❌ `Activator.CreateInstance` / `Type.GetType` / `MakeGenericMethod`
- ❌ `JsonSerializer.Serialize<T>(value)` 反射重载
- ❌ Generator 在运行时由插件反射读自己的 attribute；声明态信息只走 `plugin.json`
- ✅ 允许：`[LoggerMessage]` 源生成器、现有 `ConcurrentDictionary<string, HashSet<string>>` 等已 AOT 验证的容器

#### 3.6.4 验证命令（v1.5 R-AOT-CMD 修订）

**事实纠正**：`Netor.Cortana.Networks` / `Netor.Cortana.Plugin` 都是 ClassLibrary（无 `OutputType=Exe`、无 `RuntimeIdentifier`、无 `PublishAot` 配置），**不能直接 `dotnet publish -p:PublishAot=true`**——会因缺可执行入口失败。库的 AOT 兼容性应通过 `<IsAotCompatible>true</IsAotCompatible>` + `EnableAotAnalyzer` / `EnableTrimAnalyzer` 在 build 阶段验证；真正 `publish` 验证落到可执行项目矩阵。

**库级（build 时验证 AOT analyzer）**：

```powershell
dotnet build Src/Netor.Cortana.Networks/Netor.Cortana.Networks.csproj -c Release
dotnet build Src/Netor.Cortana.Plugin/Netor.Cortana.Plugin.csproj -c Release
dotnet build Src/Netor.Cortana.Entitys/Netor.Cortana.Entitys.csproj -c Release
```

要求三个 csproj 都设置 `<IsAotCompatible>true</IsAotCompatible>`，build 0 trim/AOT warning。

**可执行项目级（publish 验证）**：

```powershell
# 主程序
dotnet publish Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj -c Release -r win-x64

# Native Host
dotnet publish Src/Plugins/Netor.Cortana.NativeHost/Netor.Cortana.NativeHost.csproj -c Release

# voice 三件套（AOT 强约束）
dotnet publish Plugins/Src/Cortana.Plugins.Voice.Kws.Sherpa/Cortana.Plugins.Voice.Kws.Sherpa.csproj -c Release
dotnet publish Plugins/Src/Cortana.Plugins.Voice.Stt.Sherpa/Cortana.Plugins.Voice.Stt.Sherpa.csproj -c Release
dotnet publish Plugins/Src/Cortana.Plugins.Voice.Tts.Melo/Cortana.Plugins.Voice.Tts.Melo.csproj -c Release
```

每条命令必须 **0 个 trim warning、0 个 AOT warning**。任一新增 warning 必须在 PR 内修掉。

可一次性走仓库统一发布脚本（如 `Build/publish.ps1` 或 `Plugins/Tools/Publish/`，PR 实施时确认入口），避免遗漏环节。

---

### 3.7 chat.\* op 重命名 PR 实施清单（v1.5 OQ3 A2）

OQ3 决议 A2：chat.\* op 全部归入 conversation.chat.\*，op 首段严格 = topic。下表是 PR 实施时除宿主代码外（已在 §3.2 列出 6 处宿主代码）必须同步更新的**外部文档与接入模板**清单：

| 文件 | 改动量 | 改动类型 |
| --- | --- | --- |
| [Docs/系统流程与规划/websocket-api.md](../../系统流程与规划/websocket-api.md) | 9 处字面量 | line 111 / 150 / 200 / 204 / 419 / 456 / 459 / 462；client send 示例 + server 推送 case 分支全部加 `conversation.` 前缀 |
| [skills/websocket-integration/SKILL.md](../../../skills/websocket-integration/SKILL.md) | 6 处字面量 | line 3 description 4 处（`chat.message.send` / `chat.generation.stop` 各 2 处）+ line 61 / 80 / 110-112 op 字面量（`chat.token` / `chat.done` / `chat.error`），全部加 `conversation.` 前缀 |
| [skills/websocket-integration/scripts/new-websocket-client.ps1](../../../skills/websocket-integration/scripts/new-websocket-client.ps1#L75) | 1 处 | `Op = "chat.message.send"` |
| [skills/websocket-integration/resources/ws-message-samples.json](../../../skills/websocket-integration/resources/ws-message-samples.json) | 4 处 | 全部 op 字面量 |
| [skills/websocket-integration/resources/csharp-client-template.md](../../../skills/websocket-integration/resources/csharp-client-template.md) | 1 处 | template 文字 |
| [skills/websocket-integration/resources/client-checklist.md](../../../skills/websocket-integration/resources/client-checklist.md) | 1 处 | "使用 chat.message.send" 描述 |
| **新建** [Docs/release-notes/RELEASE-1.4.0.md](../../release-notes/) 或合并到现有最近 release | 新增条目 | **破坏性变更**：PluginBus 1.4.0 起 `chat.token/done/error/message.send/generation.stop` 全部重命名为 `conversation.chat.*`，第三方按 websocket-api.md 实现的 PluginBus 客户端必须升级 |

**完整 op 重命名映射表**（PR 实施时统一替换）：

| 旧 op (1.3.x) | 新 op (1.4.0) | 方向 |
| --- | --- | --- |
| `chat.token` | `conversation.chat.token` | host → plugin |
| `chat.done` | `conversation.chat.done` | host → plugin |
| `chat.error` | `conversation.chat.error` | host → plugin |
| `chat.{type}` 动态 | `conversation.chat.{type}` 动态 | host → plugin（BroadcastAsync） |
| `chat.message.send` | `conversation.chat.message.send` | plugin → host |
| `chat.generation.stop` | `conversation.chat.generation.stop` | plugin → host |

**不纳入 OQ3 重命名的 op**（保留现状）：

- `system.notice`：独立控制平面通知，不属于任何 topic，与 chat 无关
- `model.capability.*`：model topic RPC，OQ4 单独管子命名空间隔离
- `memory.context.supply.*`：memory topic RPC，OQ4 单独管子命名空间隔离
- `*.history.replay/batch/completed`：各 topic 内部自洽，namespace 已 = topic 名

---

## 四、范围边界（不做什么）

- ❌ **不做事件持久化**：宿主重启即清空，事件是状态变化通知，不是审计日志
- ❌ **不做迟订阅快照（replayLatest）**：当前环境不存在"启动后零秒产生事件"场景
- ❌ **不做节流（throttleMs）**：voice.\* 事件低频；高频场景出现时再加
- ❌ **不做请求-响应 RPC 合并**：`memory.context.supply.*` / `model.capability.*` 保持现状（决策依据 §5.2）
- ❌ **运行帧不支持通配符 op**：subscribe 帧 `subscribedOps` 必须显式列出（缺省 = 收 topic 下全部，**这是缺省，不是通配符**，详见 §2.2 分层语义）
- ❌ **manifest 不支持通配符**：`plugin.json.subscribedOps` 必须显式声明所有关心的 op，无 `voice.*` 之类匹配
- ❌ **不做跨进程会话续传**：插件断连重连按"全新订阅"处理
- ❌ **不引入消息中间件**：进程内 `ConcurrentDictionary` + WebSocket 转发足够
- ❌ **不做基于角色的 ACL**：所有已加载插件按声明发布 / 订阅
- ❌ **v1 不做 sourcePluginId 可信绑定**（详见 §5.3，留 v2）
- ❌ **不引入 PluginBusEventFrame 等新 POCO**：复用现有 [PluginBusEventMessage](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93)（v1.2 误判，v1.3 已纠正）
- ❌ **不保留 chat → conversation 别名表**（v1.5 OQ3 A2）：op 首段严格等于 topic，chat.\* 全部重命名为 conversation.chat.\*，BroadcastDispatcher.ExtractNamespace 不做特例
- ❌ **memory/model topic 上 plugin→plugin event 必须用子命名空间**（v1.5 OQ4）：`memory.event.*` / `model.event.*` 与 `memory.context.supply.*` RPC / `model.capability.*` RPC 命名隔离；v1 阶段宿主可选拒绝 plugin 上行 `memory.context.*` / `model.capability.*` 类型 `event` 帧（已通过 R-CHAT-MISFIRE 受保护 op 黑名单实现）
- ❌ **v1 不保证同一发布者→同一订阅者的事件 ordering**（v1.5 R-ORDERING）：Task.Run + WhenAll 多帧并发投递，partial 类高频流可能错序送达；订阅插件应按"总是用最新值覆盖"模式编写。详见 §九 v1.5 R-ORDERING 修订记录
- ❌ **不保留直发式（type=voice.xxx）grace 期**（v1.5 OQ2）：仓库内 voice 三件套同 PR 升级到信封式，外部第三方语音插件不存在，承诺无现实对象；HandleMessageAsync 入口仅拦截 type=event，不为遗留格式额外加分支

---

## 五、关键问题与决策

### 5.1 为什么不复用 Capabilities，而要新增 PublishesEvent / SubscribesEvent？

[`[Plugin].Capabilities`](../../../Src/Plugins/Netor.Cortana.Plugin.Native/Attributes/PluginAttribute.cs) 当前是混合语义字段：既描述"我提供什么能力"（如 `voice.stt`），又被订阅注册器借用作 capabilities 集合（5B Phase 4 决策 5B-D）。再叠"我会发什么事件 / 我订阅什么事件"会让一个字段承担三重含义。

新增独立 attribute：

- 字段语义单一，文档可独立演进
- `PublishesEvent` 可携带 `Description`，`Capabilities` 是 string 数组没法承载
- `SubscribesEvent` 可携带 `Reason`，与 `[RequiredHostCapability]` 的"申请理由"语义对齐，便于审计

### 5.2 为什么不把 memory / model RPC 合并到新事件体系？

通信语义本质不同（详见 §2.0 协议层语义分层）。强行合并的代价：

| 失去的契约 | 后果 |
| --- | --- |
| RequestId 关联 | 现 `_memorySupplyDispatcher.WaitAsync(requestId)` 靠 RequestId 路由响应回等待方；pub-sub 没这个概念 |
| 超时 + 错误码语义 | RPC 失败回 `errorCode` + `errorMessage` 给等待方做降级；pub-sub 失败发布方不感知 |
| 阻塞等待 | 宿主对话流要拿到记忆才能继续；pub-sub 是 fire-and-forget |
| 模式边界 | 开发者得猜"这个 op 是同步等响应还是 fire-and-forget"，最后产生"看似 event 实为 request"的混杂帧 |

**模式数量本身不是复杂度的来源，模式之间语义边界模糊才是**。现有 5 种模式各管各的反而清晰，本方案在第 6 种新增 `event`，按 §2.0 严格分层。

记忆插件未来需要驱动其他插件时，会**新增独立的 `memory.index.updated.v1` pub-sub 事件**，与现有 `memory.context.supply.*` RPC 并存，不互相替代。

### 5.3 sourcePluginId 防伪 — v1 不做的理由

事件帧 `sourcePluginId` 来自客户端自报，[PluginBusSubscriptionRegistry](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusSubscriptionRegistry.cs) 当前只存 clientId↔topics/capabilities，**没有 clientId↔pluginId 的可信绑定**。理论上插件 A 可冒充插件 B 发事件。

v1 决策：**仅自报 + LogWarning 不一致，不强制断连**，与 5B-D 降级策略一致。

v2 计划（独立 PR）：

1. `subscribe` 帧携带 `pluginId` 字段
2. 宿主用插件安装包内的密钥 / 启动握手 token 验证 pluginId 真实性
3. 验证后绑定 `clientId → pluginId` 到 `PluginBusSubscriptionRegistry`
4. 广播时校验 `frame.sourcePluginId == registry.GetPluginId(clientId)`，不一致直接拒绝

### 5.4 VoiceEventDispatcher 的彻底重写

**当前状态**（[PluginBusVoiceEventDispatcher.cs](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs)）：作为 [HandleMessageAsync](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L414) 内部的拦截器，通过 [ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 按 `eventType > op > type` 优先级兼容信封式（`type=event` + `eventType=voice.xxx`）和直发式（`type=voice.xxx`）两种格式。`TryHandle` 命中即 return，事件被宿主独吞，**无法广播给其他订阅插件**。

```csharp
// 当前代码（约 429-432 行附近）
if (_voiceEventDispatcher.TryHandle(id, root))   // ← 顶层拦截器
{
    return;   // ← 独吞，无法再广播
}
```

**改后**（彻底删除 `TryHandle` 入口 + 整个 [PluginBusVoiceEventDispatcher.cs](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs)）：

```csharp
if (string.Equals(type, "event", StringComparison.Ordinal))
{
    await _broadcastDispatcher.HandleAsync(id, root, ct);
    return;
}
// chat / subscribe / replay / model / memory 等现有分支完全不动
```

`PluginBusBroadcastDispatcher` 内部按 §2.4 流程处理：

1. F3 排除 `source == "host"`（host→plugin 不该走客户端上行）
2. F2 按 `op > eventType > type` 解析（1.4.0 有意变更，旧 ReadEventType 是 `eventType > op > type`）
3. 转发给订阅了 voice topic 的其他插件
4. **额外**调用 `VoiceEventBridge.Bridge(op, frame)` 桥接到 `IPublisher.Events.OnXxx`，UI 层零感知
5. `voice.tts.greeting_ready.v1` 在 bridge 内显式列 case + LogDebug，保持"吞掉不发 IPublisher"行为（与 [TryHandle_TtsGreetingReady_ReturnsTrue](../../../Tests/Netor.Cortana.Networks.Tests/PluginBusVoiceEventDispatcherTests.cs#L108-L123) 测试期望一致）

效果：

- voice.\* 事件**同时**广播给订阅插件 + 触发 UI 层 `Events.OnXxx`
- 仓库内 voice 三件套同 PR 升级到信封式后无遗留直发式帧；外部第三方语音插件不存在，1.4.0 起直发式不再支持（v1.5 OQ2 决议）；HandleMessageAsync 入口仅拦截 `type == "event"`，不为 `type=voice.xxx` 兜底
- 其他领域（memory pub-sub / workspace）按 §5.2 走相同 `BroadcastDispatcher`，不需要专门的 bridge

### 5.5 与 host.llm.invoke.v1（model topic）的关系

两套机制**并存且语义独立**（详见 §2.0 / §5.2）。事件帧不替代 `model.capability.request`，二者协议帧、topic、生命周期都独立。

### 5.6 间接事件循环 — hopCount 兜底

A → B → A 这种环（A 发 X，B 订 X 后发 Y，A 订 Y 又发 X）运行时无法静态防。

**v1 方案**：协议帧带 `hopCount`，宿主转发时递增，`>= 8` 即丢弃 + LogWarning。代码量 ≤ 5 行（详见 §2.4 伪代码）。

**v1 不做**：

- ❌ 加载期 publish/subscribe 图静态环检测（DFS plugin.json）—— 收益小
- ❌ traceId 链路追踪 —— 复杂度高

`hopCount = 8` 阈值参考 IP TTL 工程惯例。文档应明示"如果你的插件链路真的需要 ≥8 跳，重新审视设计"。

### 5.7 老插件兼容（5B-D 降级策略对齐）

参考 [HandleMessageAsync:490-498](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L490-L498) 现有对 workflow topic 的处理（声明缺失仅 `LogWarning`）：

- 老插件不发 `subscribedOps` → subscribe 行为完全等价于 1.3.0
- 老插件发 `type: event` 但 `plugin.json` 未声明 `publishedOps` → LogWarning，**仍转发**
- 订阅方声明了 `subscribedOps` 但 `plugin.json` 未声明 `subscribedOps` → LogWarning，**仍允许订阅**
- **例外**：仓库内 voice 三件套必须同步升级（§3.5），不享受降级窗口 —— 因为它们是仓库自有插件，不是外部已发布插件

不在校验失败时强制断连；正式版（PluginBus 2.0）随 §5.3 防伪一起收紧。

---

## 六、实施路线图（建议切片）

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| **PR0 文档先行（v1.5 新增）** | 把 v1.5 修订决议（OQ1-OQ5 + 5 个 Top 高危 + 中危补丁）合并进 README / 03 / 后续子文档；用户评审通过即可启动后续 PR | 文档自洽，无 v1.4 残留矛盾措辞 |
| 阶段 1 协议骨架 | [CortanaWsEndpoints.PluginBusVersion](../../../Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs#L22) 升至 `"1.4.0"`；[PluginBusEventMessage](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93) 扩展 2 个可选字段（`HopCount` / `SourcePluginId`）；[PluginBusSubscriptionRegistry](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusSubscriptionRegistry.cs) 新增 `SetSubscribedOps` / `MatchesSubscribedOp`；**新建 `PluginManifestRegistry` 放 `Src/Netor.Cortana.Plugin/`**（v1.5 OQ1，DI 单例，由 [`PluginServiceExtensions.AddCortanaPlugin`](../../../Src/Netor.Cortana.Plugin/) 注册）；[PluginManifest.cs](../../../Src/Netor.Cortana.Plugin/Core/PluginManifest.cs) 新增 `PublishedOps` / `SubscribedOps` 两个字段 + 两个 record（`PublishedOpDeclaration` / `SubscribedOpDeclaration`），Normalize() 兜底 `?? []`；[PluginManifestJsonContext.cs](../../../Src/Netor.Cortana.Plugin/Core/PluginManifestJsonContext.cs) 追加 2 条 `[JsonSerializable]`；**PluginLoader 接入点（F-LOADER 修订，挂底层方法）**：注册挂在 [`LoadProcessPluginAsync`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L403)（成功分支 line 416-420，紧挨 `NotifyPluginsChanged()`）和 [`LoadNativePluginAsync`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L432)（成功分支 line 445-449）；注销挂在 [`UnloadPluginByPath`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L730)（changed=true 分支末尾，读 `host.Manifest.Id` 反查）；[`UnloadAllPlugins`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L233) 用 `manifests.Clear()` 清空。文件 watcher 自动覆盖 | AOT analyzer 0 warning（库 `<IsAotCompatible>` + Build）；老插件 1.3.0 行为零变化；扩展字段 source generator 自动产出 |
| 阶段 2 转发器 + voice bridge | 新建 `PluginBusBroadcastDispatcher`（含**职责 1 受保护 op 黑名单防御** / F3 host 排除 / F2 op 优先 / 单帧 1MB 上限 / F6 后台投递 + 1 秒超时 + 死链主动清理 / hopCount 兜底）；新建 `Voice/VoiceEventBridge`（含 OQ5 NormalizeVoiceOp）；`HandleMessageAsync` 在 pong 后插入 `type==event` 分支；删除 `_voiceEventDispatcher` 字段与 `TryHandle` 调用；删除 [PluginBusVoiceEventDispatcher.cs](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs) 与对应测试文件；`WebSocketPluginBusServerService` ctor 增 `PluginManifestRegistry` 参数（同 PR 改 [WebSocketPluginBusServerMeetingIntegrationTests:49](../../../Tests/Netor.Cortana.Networks.Tests/WebSocketPluginBusServerMeetingIntegrationTests.cs#L49) 测试构造） | `PluginBusBroadcastDispatcherTests` 全部主路径绿（含 T17 受保护 op 防御 / T18 大 payload 拒绝 / T19 死链清理）；`VoiceEventBridgeTests` 含 V12 旧版无 .v1 后缀归一 |
| 阶段 3 SDK + Generator | 新增 `[PublishesEvent]` / `[SubscribesEvent]`（Native + Process 两套）；两个 Generator 写入 `plugin.json.publishedOps` / `subscribedOps`（对象数组）；SDK 公开 `PublishEventAsync(string op, JsonElement payload, ct)` / `SubscribeEventAsync(string op, Func<JsonElement, Task> handler)`（JsonElement-only 严格 AOT） | 三个仓库内 voice 插件 build 后 `plugin.json` diff 符合 §2.3；Generator 输出回归现有所有插件无破坏 |
| 阶段 4 仓库内 voice 三件套迁移 + chat.\* 重命名（与阶段 2-3 同 PR） | Sherpa KWS / Sherpa STT / Melo TTS 三个项目按 §3.5 表格升级：`Type` 统一为 `"event"`、`EventType` 加 `.v1` 后缀、入口加 `[PublishesEvent]`；**v1.5 OQ3 A2** 同 PR 落地 chat.\* 全部 6 处宿主代码（[PluginBusChatDispatcher.cs:19,28,37,46](../../../Src/Netor.Cortana.Networks/WebSockets/PluginBusChatDispatcher.cs#L19) + [WebSocketPluginBusServerService.cs:435,442](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L435)）+ websocket-api.md + skills/websocket-integration/ 重命名为 `conversation.chat.*`（详见 §3.7） | UI 层行为完全等价；新增"字幕插件"订阅 `voice.stt.partial.v1` 能正确收到帧；`voice.tts.greeting_ready.v1` 不触发 UI 事件；websocket-api.md 与代码字面量一致 |
| 阶段 5 release notes + 第三方通知 | 新增 [Docs/release-notes/RELEASE-1.4.0.md](../../release-notes/) 标记破坏性变更（chat.\* 重命名 + voice 直发式废弃 + plugin.json schema 扩展）；通知所有外部 PluginBus 客户端升级 | release notes 经评审 |
| 阶段 6（按需） | workspace / memory pub-sub 事件接入；新事件 op 按 §2.3 声明流程添加；不阻塞 1.4.0 发布 | 真实场景验证 |

阶段 1 完成即对应 PluginBus 1.4.0 grace 版本，外部老插件零感知；阶段 2-4 必须同一个 PR 合并（否则 voice 链路中断 + websocket-api.md 与代码不一致）；阶段 5 在阶段 4 合并后立即跟进；阶段 6 按需推进。

**关键风险点**：

- **阶段 2 + 阶段 4 必须同 PR 合并**：删 `_voiceEventDispatcher` 入口、升级三个 voice 插件发包、chat.\* 重命名三件事强绑定，缺一就破坏链路一致性
- **直发式无 grace 期**（v1.5 OQ2）：`type=voice.xxx` 老格式 1.4.0 起不再支持；外部第三方语音插件目前不存在，无现实兼容对象
- **AOT 验证**：库走 `dotnet build` + `<IsAotCompatible>true</IsAotCompatible>` + Analyzer 检查；可执行项目走 `dotnet publish` 矩阵（详见 §3.6.4 修订后命令）
- **`plugin.json` schema 改动**：新增 `publishedOps` / `subscribedOps` 字段必须确保现有所有插件的 Generator 输出无破坏（向下兼容字段不丢）；旧 plugin.json 缺字段时 Normalize() 兜底 `?? []`
- **跨程序集依赖（v1.5 OQ1）**：`PluginManifestRegistry` 放 `Netor.Cortana.Plugin`，`Netor.Cortana.Networks` 通过 DI 反向消费；不破坏现有 Networks → Plugin 单向依赖

---

## 七、文档结构

| 文档 | 内容 |
| --- | --- |
| README.md（本文） | 问题、设计、协议层语义分层、改动点、范围、关键决策、路线图 |
| 02-协议帧规范.md | `event` 帧 / 扩展 subscribe 帧的完整字段表 + JSON Schema |
| 03-宿主转发器实现.md | `PluginBusBroadcastDispatcher` + `VoiceEventBridge` + `PluginManifestRegistry` 类设计 + `HandleMessageAsync` 接入位置 + 单元测试矩阵 |
| 04-SDK与Generator.md | `[PublishesEvent]` / `[SubscribesEvent]` 字段表 + Generator 处理流程 + plugin.json 输出示例 + JsonElement-only API 约束 |
| 05-voice插件迁移.md | Sherpa KWS / Sherpa STT / Melo TTS 三个项目的 op 清单、attribute 写法、发包代码 diff、self-test 改造 |
| 06-实施路线图.md | 阶段 1-5 的具体任务清单 + 验收标准 + 风险点 + AOT 检查项 |

---

## 九、修订记录

### v1.5（2026-06-14）—— 基于 8 维度代码审评 + OQ1-OQ5 决议再修订

修订原因：用户提出 5 条评审问题（JsonElement 生命周期、字段优先级、grace 期闭环、schema 矛盾、Loader 接入点）+ 自我核验 2 条（字段计数、source 语义）后，启动 8 维度并行代码审评，发现 10 个 high/medium 级风险与 8 个 OQ 待拍板。本版集中落地 5 个 OQ + 5 个 Top 高危 + 中危补丁。

| 修订项 | v1.4 缺漏 | v1.5 修正 |
| --- | --- | --- |
| OQ1 PluginManifestRegistry 跨程序集 | v1.4 误放 `Src/Netor.Cortana.Networks/`，但 PluginLoader 在 `Src/Netor.Cortana.Plugin/`，反向依赖编译不过 | 改放 `Src/Netor.Cortana.Plugin/`，DI 注册移到 `PluginServiceExtensions.AddCortanaPlugin`，BroadcastDispatcher 跨程序集消费 |
| OQ2 grace 期承诺自相矛盾 | §3.2 "入口仅拦截 type==event" vs §6 "直发式 grace 到 1.5.0" — 入口不进何来兜底 | 删除全部 grace 期措辞；外部第三方语音插件不存在，仓库内三件套同 PR 升级 |
| OQ3 chat.\* op 重命名 A2 | chat.\* op 命名空间与 conversation topic 不一致，BroadcastDispatcher.ExtractNamespace 取 chat 路由失败 | 全部 6 处宿主代码（PluginBusChatDispatcher 4 处 + HandleMessageAsync 2 处）+ websocket-api.md + skills/ 重命名为 `conversation.chat.*`；删除别名表方案；新增 §3.7 PR 实施清单 |
| OQ4 memory/model 子命名空间 | plugin→plugin event 与 memory/model RPC 命名空间冲突，订阅者无法分流 | 强制 plugin event 用 `memory.event.*` / `model.event.*`，与 RPC op 隔离；通过 R-CHAT-MISFIRE 黑名单实现拒绝 |
| OQ5 NormalizeVoiceOp 容忍 | 用户安装的旧版三件套（无 .v1）落 default 分支，UI 字幕静默失声 | VoiceEventBridge 入口归一 `voice.xxx` → `voice.xxx.v1`，加 V12 测试用例 |
| R-CHAT-MISFIRE 受保护 op 黑名单 | chat 帧被 SDK bug 误标 type=event 时静默丢弃，对话链路无故断流 | BroadcastDispatcher 加职责 1 `IsProtectedOp` 黑名单守卫，命中即 LogWarning 拒收 |
| R-PAYLOAD-AMP 单帧字节上限 | ReadTextMessageAsync 16K 是读缓冲不是消息上限，100MB 事件 × N 订阅者 OOM | dispatcher 入口加 1MB 字节上限校验（可配） |
| R-DEAD-SUBSCRIBER 死链清理 | SendWithTimeoutAsync 1s 超时后 WebSocketConnectionManager 守卫失效，死链残留 | 异常分支主动调 `_subscriptions.Remove(cid)` + `RemoveAndCloseAsync(cid)` |
| R-AOT-CMD AOT 命令矩阵 | ClassLibrary 跑 PublishAot 直接失败 | 库改 `<IsAotCompatible>true</IsAotCompatible>` + Build；可执行项目独立 publish 矩阵（UI/NativeHost/三件套） |
| R-FILES 文件清单补全 | 漏列 PluginManifest.cs / NetworkServiceExtensions / PluginServiceExtensions / 测试 ctor | 全部补入 §3.2 表 |
| R-ORDERING 不保证 ordering | partial 类高频帧 Task.Run + WhenAll 错序送达，字幕回退 | §四 范围边界明示 v1 不保序；订阅插件按"总是用最新值覆盖"模式编写 |

### v1.4（2026-06-14）—— 基于 7 条评审问题再修订

修订原因：v1.3 经源码再核验后暴露 7 条事实级 / 设计级问题，本版本逐条修复。

| 修订项 | v1.3 缺漏 | v1.4 修正 |
| --- | --- | --- |
| F-LIFETIME | §2.4 伪代码后台 `Task.Run` 闭包捕获 `frame`，但 [HandleMessageAsync:418](../../../Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs#L414-L556) 是 `using var doc = JsonDocument.Parse(json)`，return 后 doc 被 Dispose，`frame` 指向已释放对象 | 同步阶段先把转发载荷序列化为 string、解析出目标 clientId 列表、voice payload 显式 `Clone()`，再投后台 Task.Run 只跑 SendAsync |
| F-PRIORITY | "延续现有规则"措辞不准确——[ReadEventType](../../../Src/Netor.Cortana.Networks/WebSockets/Voice/PluginBusVoiceEventDispatcher.cs#L88-L93) 实际是 `eventType > op > type`，v1.4 改为 `op > eventType > type` 是有意变更 | 全文措辞改为"1.4.0 有意变更优先级，把 op 提到首位"；明确兼容：op 与 eventType 同时存在以 op 为权威，老插件无 op 时 fallback 到 eventType（不破坏 1.3.0） |
| F-DIRECT | HandleMessageAsync 入口仅拦截 `type == "event"`，老 voice 直发式（`type=voice.xxx`）不进入，但文档承诺 grace 期到 1.5.0 兜底 | 明文承诺 grace 期**仅对外部第三方语音插件**；仓库内三件套同 PR 升级到信封式后无遗留直发式帧，宿主入口不为 `type=voice.xxx` 额外加分支 |
| F-SCHEMA | README §2.3 写"对象数组 `[{op, description/reason}]`"、03 §4.2 `Register` 接口写 `IEnumerable<string>`、03 §4.4 又写"字符串数组"，三处自相矛盾 | `plugin.json` schema 统一为对象数组（含 op + description/reason）；C# `PluginManifest` 用 record 数组；`Register` 接口签名保留 `IEnumerable<string>`（取 op 集合），调用方在 PluginLoader 内从对象数组抽 op 后传入；Generator 输出对象数组 |
| F-LOADER | 接入点描述为"PluginLoader 在 load/unload 调用"，未具体到方法 | 注册挂在 [`LoadProcessPluginAsync` 成功分支](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L403) line 416-420 与 [`LoadNativePluginAsync` 成功分支](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L432) line 445-449（紧挨 `NotifyPluginsChanged()`）；注销挂在 [`UnloadPluginByPath`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L730) changed=true 分支末尾；[`UnloadAllPlugins`](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L233) 用 `manifests.Clear()`；文件 watcher 走底层方法自动覆盖 |
| F-FIELDCOUNT | §2.1 标题"仅新增 1 个字段 hopCount" 与 §2.1.1 字段表 + §2.1.3 代码示例的 2 字段（hopCount + sourcePluginId）不一致 | 改为"新增 2 个可选字段（hopCount 与 sourcePluginId）" |
| F-SOURCE | source / sourcePluginId 在文档中三处描述不一致：v2.1.1 表说"plugin→plugin 时 source = pluginId"、§2.4 职责说"读 source 进入广播路径"、§5.3 说"v1 不做可信绑定" | 严格分层：v1 路由仅信任 clientId 排除自回环（不信任自报字段）；弱声明校验从 frame 读 sourcePluginId（缺省 fallback 到 source），与 plugin.json 比对仅 LogWarning，不作安全依据；F3 host 排除仅靠 `source == "host"` 判别；v2 才做可信绑定 |

### v1.3（2026-06-14）—— 基于真实源码核验重写

修订原因：用户对 v1.2 提出 6 条评审意见，自我核验发现第 7 条事实级错误。整体降稿重写。

| 修订项 | v1.2 错误 / 缺漏 | v1.3 修正 |
| --- | --- | --- |
| F1 | "新增 PluginBusEventFrame" | 复用现有 [PluginBusEventMessage](../../../Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs#L93)，仅加 2 个字段 |
| F2 | 没说明字段优先级 | 明确 op > eventType > type 优先级 ⚠️ 该条措辞被 v1.4 F-PRIORITY 推翻：旧 ReadEventType 真实优先级是 `eventType > op > type`，`op > eventType > type` 是 1.4.0 有意变更而非延续 |
| F3 | type==event 分支没区分上下行 | 排除 `source == "host"` 的 host→plugin 帧 |
| F4 | `PluginManifestRegistry` 在 ctor 内 new | 改 DI 单例，PluginLoader load/unload 时调用 |
| F5 | "缺省全收"与"不支持通配符"自相矛盾 | 分层：manifest 强制声明（开发态）vs subscribe 帧缺省全收（运行态） |
| F6 | fire-and-forget 与 `Task.WhenAll` 矛盾，会拖死 ReceiveLoop | 后台 Task.Run 投递 + per-subscriber 1 秒超时 |
| F7 | `voice.tts.greeting_ready` 行为未闭环 | VoiceEventBridge 显式列 case + LogDebug，加 V10 测试 |
| 事实级 | "voice 是裸帧" | voice 三件套已用 type=event 信封式 / 直发式两种格式，dispatcher 通过 ReadEventType 兼容 |
| 事实级 | "type=event 是新增" | 已存在用于 host→plugin（3 处 Relay + Factory），本方案扩展为支持 plugin→plugin |

### v1.2（2026-06-14）—— 基于"只改 voice"约束简化（已废稿）

- 彻底改 voice / 不合并 RPC / hopCount 兜底 / AOT 改动清单 / 协议层语义分层
- ⚠️ 含多处事实级错误，已被 v1.3 取代

### v1.1（2026-06-14）—— 按用户授权简化（已废稿）

- 去掉 replayLatest / RetainLatest；新发现旁路广播兼容路径；展开 AOT 清单与 hopCount

### v1（2026-06-14）—— 初稿

---

**最后更新**：2026-06-14（v1.5：基于 8 维度代码审评 + 5 个 OQ 决议再修订——OQ1 Plugin 程序集 / OQ2 删 grace / OQ3 chat→conversation.chat 重命名 / OQ4 memory.event 子命名空间 / OQ5 NormalizeVoiceOp / R-CHAT-MISFIRE 受保护 op 黑名单 / R-PAYLOAD-AMP 1MB 上限 / R-DEAD-SUBSCRIBER 死链清理 / R-AOT-CMD 命令矩阵 / R-FILES 文件清单补全 / R-ORDERING 明示不保序）
