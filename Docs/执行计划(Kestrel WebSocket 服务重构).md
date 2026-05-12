# 执行计划(Kestrel WebSocket 服务重构) : 94%

## 背景

当前 `WebSocketInteractionServerService` 与 `WebSocketFeedServerService` 都基于 `HttpListener` + 裸后台线程实现，存在生命周期不可控、启动失败假成功、停止/重启竞态、发送锁释放竞态、重复职责、端点冲突、客户端异常拖垮服务等问题。

本计划已进入第一阶段代码实施。当前目标是先完成零破坏 Kestrel 替换，保持旧端口、旧路径、旧消息类型可用；公共连接基础设施、发送队列、心跳与进一步职责收敛后续分阶段落地。

## 目标边界

### WebSocketInteractionServerService

定位：聊天交互通道。

职责：
- 作为聊天消息输入输出通道。
- 接收前端/客户端消息。
- 触发系统功能调用，例如发送用户消息给 LLM、停止生成、发送系统通知。
- 向对应聊天客户端推送 token、done、error、语音/聊天状态等交互消息。
- 承载交互控制面，包括长期记忆供应与 model-capability 请求/响应。
- 管理多客户端连接、心跳、异常断开、客户端重连后的新连接注册。

不再负责：
- conversation feed 订阅。
- 对话事实广播。

### WebSocketFeedServerService

定位：会话事实广播服务。

职责：
- 只负责将宿主内部产生的会话事实广播给已连接客户端。
- 广播所有对话事件、对话内容、增量消息、回合开始/结束等事件。
- 支持多客户端连接。
- 负责心跳、慢客户端剔除、异常客户端断开。

不负责：
- 接收客户端业务数据。
- 处理 subscribe/replay/request/response 等客户端命令。
- 触发 LLM 调用。
- 长期记忆供应。
- model-capability 请求处理。

长期记忆供应与 model-capability 属于交互控制面，应归入 `WebSocketInteractionServerService` 所代表的交互功能板块；Feed 服务只保留会话事实广播能力。

兼容性原则：本次重构不能破坏既有插件和已连接服务的协议入口。除非有独立迁移计划，否则必须保持旧端口、旧路径、旧消息类型可用；新架构只能先在内部改实现，不直接改变外部协议契约。

---

## 设计原则

1. 使用 Kestrel 替换 `HttpListener`。
2. 不再使用裸 `Thread`。
3. 服务启动失败不能假成功。
4. 服务本体要高可用，单个客户端出错只断开该客户端。
5. 客户端连接短生命周期，服务监听长生命周期。
6. 业务职责清晰隔离：聊天交互和会话事实广播分离。
7. 公共连接管理、发送队列、心跳、异常处理抽到基础设施层。
8. 慢客户端不能阻塞广播服务。
9. 停止/重启必须串行化，不能并发 Start/Stop。
10. Feed 服务为服务端单向广播语义，不接受客户端业务输入。
11. 对外协议优先保持兼容，不能因为内部重构导致旧插件、旧服务全部失效。
12. 如需调整端口或协议模式，必须先提供兼容桥接、灰度迁移和废弃周期。

---

## 推荐架构

```text
Netor.Cortana.Networks
├─ WebSockets
│  ├─ Hosting
│  │  ├─ KestrelWebSocketHost
│  │  ├─ KestrelWebSocketEndpointOptions
│  │  ├─ WebSocketHostState
│  │  └─ WebSocketHostHealth
│  ├─ Connections
│  │  ├─ WebSocketConnection
│  │  ├─ WebSocketConnectionManager
│  │  ├─ WebSocketSendQueue
│  │  ├─ WebSocketHeartbeatLoop
│  │  └─ WebSocketClosePolicy
│  ├─ Protocols
│  │  ├─ ChatWebSocketProtocolHandler
│  │  ├─ MemorySupplyProtocolHandler
│  │  ├─ ModelCapabilityProtocolHandler
│  │  └─ ConversationFeedBroadcastProtocol
│  ├─ Servers
│  │  ├─ WebSocketInteractionServerService
│  │  └─ WebSocketFeedServerService
│  └─ Relays
│     ├─ WebSocketEventRelayService
│     └─ WebSocketConversationFeedRelayService
```
 
---

## 核心组件方案

### 1. KestrelWebSocketHost

公共基类，封装 Kestrel 生命周期。

建议职责：
- 读取端口配置。
- 创建并启动 Kestrel WebApplication。
- 挂载 WebSocket 中间件。
- 注册一个或多个路径。
- 维护 `Port`、`IsRunning`、`LastError`、`RestartCount`。
- 串行化 `StartAsync` / `StopAsync` / `RestartAsync`。
- 启动失败时抛出或进入明确 degraded 状态，不允许假成功。
- 异常停止后按策略重启监听服务。

建议抽象点：
- `ServiceName`
- `SettingsPortKey`
- `DefaultPort`
- `IReadOnlyList<string> Paths`
- `Task HandleSocketAsync(HttpContext context, WebSocket socket, CancellationToken ct)`
- `Task OnStartedAsync(...)`
- `Task OnStoppingAsync(...)`

注意：
- 基类负责监听和连接入口。
- 子类只处理业务协议。

### 2. WebSocketConnection

代表一个客户端连接。

建议字段：
- `Id`
- `WebSocket Socket`
- `RemoteIp`
- `RemotePort`
- `ConnectedAt`
- `LastReceiveAt`
- `LastSendAt`
- `LastPongAt`
- `CancellationTokenSource LifetimeCts`
- `Channel<string> OutboundQueue`
- `Task ReceiveTask`
- `Task SendTask`
- `Task HeartbeatTask`

设计要求：
- 每个连接独立发送队列。
- 发送 loop 串行写 WebSocket，避免多线程同时 Send。
- 业务广播只入队，不直接 await socket.SendAsync。
- 队列满时按策略处理：Feed 可直接断开慢客户端；Chat 可丢弃低优先级状态消息但不能丢 token/done。

### 3. WebSocketConnectionManager

公共连接管理器。

职责：
- 添加连接。
- 移除连接。
- 按 id 查找连接。
- 广播消息。
- 批量关闭连接。
- 统计连接数。
- 剔除异常连接。

要求：
- `Remove` 必须幂等。
- 客户端异常只影响该客户端。
- Dispose 顺序统一：先取消连接 CTS，再完成发送队列，再尝试 Close，最后 Dispose Socket。

### 4. WebSocketHeartbeatLoop

心跳保活。

建议策略：
- 服务端每 20~30 秒发送应用层 ping 消息。
- 客户端收到后返回 pong。
- 如果 2~3 个周期没有 pong，服务端主动断开该客户端。
- 使用应用层 JSON：
  - `{"type":"ping","timestamp":...}`
  - `{"type":"pong","timestamp":...}`

原因：
- .NET WebSocket API 对标准 ping/pong 控制帧支持有限，应用层心跳更可控。

### 5. WebSocketSendQueue

发送稳定性关键。

要求：
- 每个客户端一个 bounded channel。
- 单独 send loop。
- 单次发送有超时。
- 发送失败或超时：关闭当前客户端。
- 不允许广播时逐个同步等待慢客户端。

推荐参数：
- Chat 队列容量：512 或 1024。
- Feed 队列容量：2048，可按事件量调整。
- 单次发送超时：5~10 秒。
- Feed 队列满：断开客户端，让客户端重连补偿。
- Chat 队列满：优先断开客户端，避免状态错乱。

---

## 两个服务的新职责设计

### WebSocketInteractionServerService 新设计

保留接口：
- `IHostedService`
- `IChatTransport`
- `ILongMemorySupplyClient`

建议承载：
- Chat 交互协议。
- 长期记忆供应交互协议。
- model-capability 控制面协议。

端口配置：
- Key：`WebSocket.Port`
- 默认：`52841`

路径：
- `CortanaWsEndpoints.ChatPath`，例如 `/ws/`
- 兼容保留现有 `ModelCapabilityProtocol.Path`。
- 兼容保留现有长期记忆供应消息通道；若当前依赖 conversation-feed 承载，应先在交互服务中提供兼容桥接，不直接删除旧入口。

输入协议：
- `send`：用户消息，进入 `WebSocketInputChannel` / `IAiChatEngine`。
- `stop`：停止当前生成。
- `system.notice`：系统通知。
- `memory.supply.response` / `memory.supply.error`：长期记忆供应响应。
- `model-capability request`：模型能力调用请求。
- `pong`：心跳响应。

输出协议：
- `connected`
- `ping`
- `token`
- `done`
- `error`
- `memory.supply.request`
- `model-capability response`
- `stt_partial`
- `stt_final`
- `tts_started`
- `tts_subtitle`
- `tts_completed`
- `chat_completed`

关键行为：
- 客户端消息必须支持完整 WebSocket 文本帧拼接。
- JSON 解析错误只返回当前客户端 error，不影响服务。
- `OnClientMessageReceived` 继续作为输入通道事件。
- `BroadcastAsync` 改为入队广播，慢客户端自动剔除。
- `SendTokenAsync` / `SendDoneAsync` / `SendErrorAsync` 对单客户端入队发送。
- 长期记忆供应与 model-capability 不能混入 Feed 广播服务，应作为交互控制面 handler 独立维护。
- 对外路径和消息类型必须优先兼容旧插件，内部可以重构 handler 和连接管理，但不能突然更改外部协议。

### WebSocketFeedServerService 新设计

保留接口：
- `IHostedService`

建议新增接口：
- `IConversationFeedBroadcaster`

建议移除接口：
- `ILongMemorySupplyClient`

端口配置：
- Key：`ConversationFeed.Port`
- 默认：`0` 表示随机端口，或明确配置一个默认端口。

路径：
- `CortanaWsEndpoints.ConversationFeedPath`，例如 `/internal/conversation-feed/`

输入协议：
- 只允许：
  - WebSocket 握手。
  - `pong` 心跳响应。
- 不接受：
  - `memory.supply.response`
  - `model-capability` 请求。

兼容要求：
- 如果既有插件已经依赖 `subscribe`，第一阶段必须兼容接收并返回成功，但内部可将所有连接默认视为订阅者。
- 如果既有插件已经依赖 `replay`，第一阶段不能直接删除；应保留兼容路径，或先提供等价的历史查询接口并完成迁移。
- Feed 重构目标是“最终只广播”，但迁移期间必须保留旧协议入口，避免旧插件和已连接服务大面积失效。

输出协议：
- `connected`
- `ping`
- `event`
- `error`，仅服务端错误通知，可选。

广播来源：
- `WebSocketConversationFeedRelayService` 订阅 EventHub：
  - `OnConversationTurnStarted`
  - `OnConversationUserMessage`
  - `OnConversationAssistantDelta`
  - `OnConversationTurnCompleted`
- relay 调用 `IConversationFeedBroadcaster.BroadcastAsync(message)`。

关键行为：
- 所有已连接客户端默认视为订阅者。
- 新客户端连接后立即收到 `connected`。
- 新协议不再要求客户端发送 subscribe，但旧协议发送 subscribe 时必须兼容。
- 历史 replay 长期建议迁移到 HTTP API 或独立 query 服务；迁移完成前，旧 replay 入口必须保持可用。

---

## 高可用策略

### 服务级高可用

- Kestrel host 意外停止时，基类 supervisor 记录错误并尝试重启。
- 重启采用指数退避：1s、2s、5s、10s、30s，最大 30s。
- 连续失败不崩溃应用，进入 degraded 状态并继续定期重试。
- `Port` 只在 Kestrel 成功监听后更新为实际端口。
- 随机端口模式下，重启时应优先尝试上次端口，失败再换随机端口，并通知依赖方。

### 客户端级高可用

- 任意客户端异常：立即关闭并移除该客户端。
- 不尝试在服务端恢复客户端连接。
- 客户端应自行重连。
- 服务端保证新连接可重新进入。

### 广播级高可用

- 广播写入每个客户端队列。
- 慢客户端队列满则断开。
- 单个客户端发送失败不影响其他客户端。
- 广播方法本身不因个别客户端失败抛出业务异常。

### 心跳策略

- 每 30 秒发送 `ping`。
- 超过 90 秒未收到 `pong` 判定死连接。
- 死连接立即关闭并清理。
- 客户端重连后获得新 `clientId`。

---

## Kestrel 承载方案

建议每个服务独立 Kestrel host：

- Chat Kestrel：绑定 `WebSocket.Port`。
- Feed Kestrel：绑定 `ConversationFeed.Port`。

原因：
- 两个服务端口生命周期独立。
- Chat 重启不影响 Feed 广播。
- Feed 重启不影响聊天交互。
- 端口配置、错误状态、日志可独立观察。

Kestrel 配置建议：
- ListenLocalhost(port)。
- UseWebSockets。
- Map 指定路径。
- 非 WebSocket 请求返回 400 或 404。
- 限制请求体、header、keep-alive timeout。

不建议：
- 两个服务共用一个 Kestrel host 后再按 path 分发。这样虽然可减少端口，但会重新耦合生命周期。

---

## 旧协议兼容与迁移策略

本次重构是内部实现替换，不应变成外部协议破坏性升级。当前已有插件和服务依赖既有端口、路径、消息类型；如果直接更改协议端口或协议模式，会导致旧插件和已连接服务全部失效，迁移工作量巨大。因此必须采用兼容优先策略。

### 兼容底线

- `WebSocket.Port` 默认值和语义保持不变。
- `ConversationFeed.Port` 默认值和语义保持不变。
- 既有 WebSocket path 保持可用。
- 既有消息类型保持可解析、可响应。
- 插件加载器仍能获得 `WsPort` 与 `FeedPort`。
- 新 Kestrel 实现替换 `HttpListener`，但不能改变外部连接契约。

### 协议归属修正

| 功能 | 最终归属 | 兼容要求 |
|---|---|---|
| Chat send/stop/token/done/error | `WebSocketInteractionServerService` | 保持 `/ws/` 与现有消息类型 |
| `model-capability` 控制面 | `WebSocketInteractionServerService` 交互板块 | 保持现有 `ModelCapabilityProtocol.Path` 可用 |
| 长期记忆供应 | `WebSocketInteractionServerService` 交互板块 | 保持现有 memory supply request/response/error 消息兼容 |
| 会话事实广播 | `WebSocketFeedServerService` | 保持 conversation-feed 旧连接入口 |
| `subscribe` | Feed 兼容入口 | 新协议可默认订阅，但旧 subscribe 必须返回成功 |
| `replay` | Feed 兼容入口，长期迁移 | 不可第一阶段直接删除 |

### 迁移阶段

#### 第一阶段：零破坏替换

- 只把底层 `HttpListener` 换成 Kestrel。
- 端口不变。
- 路径不变。
- 消息类型不变。
- `subscribe`、`replay`、memory supply、model-capability 均保持旧插件可用。
- 内部先通过 handler 分流，将交互控制面从 Feed 逻辑中解耦出来。

#### 第二阶段：双入口兼容

- 新交互控制面在 `WebSocketInteractionServerService` 下提供正式归属入口。
- 旧 Feed 内的 memory/model capability 入口保留为兼容桥接。
- 旧入口收到请求时转发到新的交互 handler。
- 日志标记旧入口为 legacy，但不影响功能。

#### 第三阶段：插件协议升级

- 更新插件 SDK / 文档。
- 新插件默认连接新的交互控制面。
- 老插件继续走 legacy bridge。
- 增加协议版本字段，便于服务端识别新旧客户端。

#### 第四阶段：废弃旧入口

- 只有在确认所有官方插件和主流第三方插件完成迁移后，才考虑废弃旧入口。
- 废弃前至少保留一个完整版本周期。
- 删除前必须有兼容性扫描和插件市场提示。

### 实施要求

- 不允许一次性删除旧协议。
- 不允许默认端口突然变化。
- 不允许路径迁移但不提供 bridge。
- 不允许 Feed 目标重构影响当前 Memory 插件可用性。
- 所有协议调整必须先写入 `Docs/系统流程与规划/websocket-api.md`，再实施代码。

---

## Kestrel 与 AOT 安全讨论

结论：可以使用 Kestrel，但必须按 Native AOT / Trim 安全方式使用。当前项目主 UI 已启用 `PublishAot=true`，而 `Netor.Cortana.Networks` 是普通 class library；引入 Kestrel 后，最终风险会体现在 UI AOT 发布阶段。因此该重构必须把 Kestrel 用法控制在 AOT 友好子集内。

### 1. Kestrel 本身是否适合 AOT

ASP.NET Core / Kestrel 在 .NET 8+ 已支持 Native AOT 的核心场景，尤其是 Minimal API、WebSocket、中间件管线、显式路由这类轻量用法。这里计划使用 Kestrel 只做本机 WebSocket 监听，不使用 MVC、Razor、Controller、Endpoint 自动发现，因此整体方向是 AOT 可控的。

适合本项目的 Kestrel 用法：
- `WebApplication.CreateSlimBuilder` 优先于完整 `CreateBuilder`。
- `UseWebSockets`。
- 显式 `Map` 固定路径。
- 显式 `ListenLocalhost(port)`。
- 手写 WebSocket 协议处理。
- 使用 `System.Text.Json` 源生成上下文。

不建议使用：
- MVC / Controller。
- Razor Pages。
- Endpoint 自动扫描。
- 反射式程序集扫描注册服务。
- Newtonsoft.Json。
- 动态插件类型发现放在 Kestrel host 内。
- 运行时动态生成代理或表达式编译。

### 2. 当前项目引入 Kestrel 的项目文件风险

`Netor.Cortana.Networks.csproj` 当前是：

```xml
<Project Sdk="Microsoft.NET.Sdk">
```

如果在该类库里直接使用 `WebApplication`、Kestrel、ASP.NET Core 中间件，需要补充 ASP.NET Core shared framework 引用。推荐方向：

```xml
<ItemGroup>
  <FrameworkReference Include="Microsoft.AspNetCore.App" />
</ItemGroup>
```

不建议为了 Networks 类库直接改成 `Microsoft.NET.Sdk.Web`，因为它不是入口 Web 项目，改 Web SDK 可能引入不必要发布行为。更合适的是保持 class library，增加 `FrameworkReference`，最终由 UI AOT 发布时携带并裁剪。

### 3. AOT 安全设计要求

#### 3.1 JSON 必须源生成

所有 WebSocket 输入输出 DTO 必须进入 `WebSocketJsonContext`。

允许：
- `JsonSerializer.Serialize(value, WebSocketJsonContext.Default.SomeType)`
- `JsonSerializer.Deserialize(json, WebSocketJsonContext.Default.SomeType)`
- `JsonSerializer.SerializeToElement(value, WebSocketJsonContext.Default.SomeType)`

避免：
- `JsonSerializer.Serialize(object)`
- `JsonSerializer.Deserialize<T>(json)` 未传 `JsonTypeInfo`
- `JsonDocument` 解析后再反射映射到对象
- `object` / `dynamic` 作为协议载荷

Feed 的事件广播如果必须承载多种事件类型，应继续由 Relay 层用明确 `JsonTypeInfo<TArgs>` 转成 `JsonElement`，不要在 Feed 服务里做动态类型序列化。

#### 3.2 DI 必须显式注册

Kestrel host 内部服务注册必须显式。

允许：
- `services.AddSingleton<WebSocketConnectionManager>()`
- `services.AddSingleton<IConversationFeedBroadcaster, WebSocketFeedServerService>()`

避免：
- 按程序集扫描注册。
- 根据命名约定查找 handler。
- `Activator.CreateInstance` 创建协议处理器。

#### 3.3 路由必须显式固定

建议只使用：

```text
/ws/
/internal/conversation-feed/
```

不要做运行时扫描 endpoint，也不要引入 Controller 路由。

#### 3.4 日志建议源生成

高频路径例如连接、断开、发送失败、心跳超时，建议逐步改成 `[LoggerMessage]` 源生成日志，减少 AOT 和高频分配压力。但这不是第一阶段硬性要求。

#### 3.5 不在 Kestrel 层放插件动态能力

插件加载、Native DLL、Process 插件、MCP 等动态能力本身可能涉及反射、AssemblyLoadContext、进程启动或动态协议。Kestrel WebSocket 层只应做连接和消息转发，不应承担插件类型发现和动态调用，避免把 AOT 风险扩散到网络服务基础层。

### 4. Kestrel Native AOT 发布风险清单

| 风险 | 说明 | 规避方式 |
|---|---|---|
| ASP.NET Core 功能面过大 | MVC/Razor/Controller 会增加 trim/AOT 风险 | 只用 Minimal + WebSocket |
| JSON 反射序列化 | AOT 后可能缺元数据 | 全部纳入 `WebSocketJsonContext` |
| DI 扫描 | trim 后类型可能被裁剪 | 显式注册 |
| 动态协议载荷 | `object`/`dynamic` 不可静态分析 | 使用明确 DTO 或 `JsonElement` |
| Kestrel 随主 UI AOT 发布 | Networks 是库，最终由 UI 发布暴露问题 | 在 UI 发布链路跑 AOT 分析 |
| 第三方包 trim 警告 | 依赖包可能不是 AOT safe | 开启分析器并逐项处理 |

### 5. 推荐验证命令

在正式编码后，应至少增加以下验证：

```text
dotnet build Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj /p:EnableAotAnalyzer=true /p:EnableTrimAnalyzer=true /p:TrimmerSingleWarn=false
```

发布验证：

```text
dotnet publish Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj -c Release -r win-x64 /p:PublishAot=true /p:TrimmerSingleWarn=false
```

验收标准：
- 不新增 IL2026 / IL2057 / IL2070 / IL3050 等关键警告。
- 若出现警告，优先改代码，不优先 suppress。
- 必须实际运行 AOT 产物并验证两个 WebSocket 服务可启动、连接、心跳、断线重连。

### 6. 最终建议

Kestrel 方案可继续推进，但要坚持“瘦 Kestrel、显式协议、源生成 JSON、显式 DI、无动态扫描”。只要不引入 MVC/Razor/反射式发现，Kestrel 用作本机 WebSocket host 对 AOT 是可接受的，风险低于继续维护 `HttpListener` + 裸线程生命周期。

---

## DI 与接口调整

### 新增接口

```text
IConversationFeedBroadcaster
- int Port { get; }
- Task BroadcastAsync(string message, CancellationToken cancellationToken = default)
```

### 调整注册

当前：
- `WebSocketInteractionServerService` 注册为 `IChatTransport`。
- `WebSocketFeedServerService` 注册为 `ILongMemorySupplyClient`。

目标：
- `WebSocketInteractionServerService` 注册为 `IChatTransport`。
- `WebSocketInteractionServerService` 或其交互控制面组件注册为 `ILongMemorySupplyClient`。
- `WebSocketFeedServerService` 注册为 `IConversationFeedBroadcaster`。
- 迁移期内 `WebSocketFeedServerService` 可保留 legacy bridge，但不作为长期归属。

### Relay 调整

`WebSocketConversationFeedRelayService` 依赖从具体 `WebSocketFeedServerService` 改为 `IConversationFeedBroadcaster`。

---

## 迁移步骤

## Step 1 明确协议边界 : 0%
- [×] 确认 Chat 服务只保留 `/ws/`。
- [×] 确认 Feed 服务只保留 `/internal/conversation-feed/`。
- [×] 确认长期记忆供应归属交互控制面。
- [×] 确认 model-capability 归属交互控制面。
- [×] 确认 Feed 最终只负责广播，但迁移期兼容 subscribe/replay 等旧协议。

## Step 2 抽离公共 WebSocket 基础设施 : 100%
- [×] 新增 Kestrel host 基类。
- [×] 新增连接对象 WebSocketConnection。
- [×] 新增连接管理器 WebSocketConnectionManager。
- [×] 新增发送队列 WebSocketSendQueue。
- [×] 新增心跳组件 WebSocketHeartbeatLoop。
- [×] 新增统一关闭策略 WebSocketClosePolicy。

已落地：

- `KestrelWebSocketHost`：封装 `CreateSlimBuilder`、`ListenLocalhost`、`UseWebSockets`、endpoint 映射和 Host 释放流程。
- `WebSocketSendQueue`：每个连接独立串行发送队列，基于 `Channel<string>`。
- 支持有界容量、入队超时、发送超时。
- 队列满、发送超时或发送异常时触发 fault 回调，后续接入连接管理后可统一剔除慢客户端。
- `WebSocketConnection`：封装连接 ID、Socket、Channel、LastPong 和发送队列。
- `WebSocketConnectionManager`：统一注册、查找、心跳标记、单发、广播、移除和关闭连接。
- `WebSocketHeartbeatLoop`：封装应用层 ping、超时判断和超时关闭回调。
- `WebSocketClosePolicy`：统一正常关闭、Abort 和 Dispose 顺序。
- `WebSocketSendQueue`：关闭路径已加幂等保护，发送 worker 故障回调改为异步调度，避免发送队列 worker 在故障自清理时等待自身。
- `WebSocketSendQueue.DisposeAsync` 已加幂等保护；发送 worker 故障时异步调度连接清理，避免自等待和重复释放 CTS。
- 基础设施类已按推荐架构归档到 `WebSockets/Hosting` 与 `WebSockets/Connections` 目录。
- `WebSocketInteractionServerService` 已接入 `KestrelWebSocketHost` 公共 Host 构建与释放流程。
- `WebSocketInteractionServerService` 的 Chat 主通道已接入 `WebSocketConnectionManager`、`WebSocketConnection`、`WebSocketSendQueue` 与 `WebSocketHeartbeatLoop`。
- `WebSocketInteractionServerService` 的 conversation-feed legacy 通道已接入 `WebSocketConnectionManager`、`WebSocketConnection`、`WebSocketSendQueue` 与 `WebSocketHeartbeatLoop`。
- `WebSocketInteractionServerService` 的 model-capability legacy 通道已接入 `WebSocketConnectionManager`、`WebSocketConnection`、`WebSocketSendQueue` 与 `WebSocketHeartbeatLoop`。
- `WebSocketInteractionServerService` 已清理旧内联心跳循环和旧连接移除包装方法，统一使用公共连接基础设施。
- `WebSocketFeedServerService` 已接入 `KestrelWebSocketHost` 公共 Host 构建与释放流程。
- `WebSocketFeedServerService` 的 conversation-feed 通道已接入 `WebSocketConnectionManager`、`WebSocketConnection`、`WebSocketSendQueue` 与 `WebSocketHeartbeatLoop`。
- `WebSocketFeedServerService` 的 model-capability legacy 通道已接入 `WebSocketConnectionManager`、`WebSocketConnection`、`WebSocketSendQueue` 与 `WebSocketHeartbeatLoop`。
- `WebSocketFeedServerService` 已清理旧内联心跳循环和 model-capability 旧发送锁残留。
- 已通过 `dotnet build Src/Netor.Cortana.Networks/Netor.Cortana.Networks.csproj` 构建验证。

## Step 3 重构 WebSocketInteractionServerService : 89%
- [×] 移除 HttpListener 和裸 Thread。
- [ ] 只挂载 ChatPath。
- [×] 接入 KestrelWebSocketHost 公共 Host 构建与释放流程。
- [×] 保留 IChatTransport 事件和发送方法。
- [ ] 删除 conversation-feed 广播相关逻辑。
- [×] 将 memory supply 和 model-capability 迁移到交互服务兼容入口。
- [×] 接收消息改为完整帧读取。
- [×] 修复发送失败清理客户端后重复释放发送锁的竞态。
- [×] Chat 主通道输出消息改为 WebSocketSendQueue 入队发送。
- [×] conversation-feed legacy 输出消息改为 WebSocketSendQueue 入队发送。
- [×] model-capability legacy 输出消息改为 WebSocketSendQueue 入队发送。
- [×] Chat 主通道接入 WebSocketConnectionManager。
- [×] conversation-feed legacy 通道接入 WebSocketConnectionManager。
- [×] model-capability legacy 通道接入 WebSocketConnectionManager。
- [×] Chat 主通道心跳改为 WebSocketHeartbeatLoop 驱动。
- [×] conversation-feed legacy 通道心跳改为 WebSocketHeartbeatLoop 驱动。
- [×] model-capability legacy 通道心跳改为 WebSocketHeartbeatLoop 驱动。
- [×] 清理旧内联心跳循环与旧连接移除包装方法。
- [×] 输出消息增加发送超时，发送失败或超时后清理对应客户端。
- [×] 增加应用层 ping/pong 心跳发送与 pong 兼容处理。
- [×] 记录最后 pong 时间，超过 90 秒无 pong 时主动断开连接。
- [×] 保持旧插件依赖的路径和消息类型兼容。

## Step 4 重构 WebSocketFeedServerService : 80%
- [×] 移除 HttpListener 和裸 Thread。
- [×] 只挂载 ConversationFeedPath。
- [ ] 移除客户端业务消息处理。
- [×] 最终移除 replay，但第一阶段保留 legacy 兼容入口。
- [ ] 将 memory supply 迁移到交互控制面，并在迁移期保留 bridge。
- [ ] 将 model-capability 迁移到交互控制面，并在迁移期保留 bridge。
- [×] 实现 IConversationFeedBroadcaster。
- [×] 接入 KestrelWebSocketHost 公共 Host 构建与释放流程。
- [×] conversation-feed 通道接入 WebSocketConnectionManager。
- [×] conversation-feed 输出消息改为 WebSocketSendQueue 入队发送。
- [×] conversation-feed 心跳改为 WebSocketHeartbeatLoop 驱动。
- [×] model-capability legacy 通道接入 WebSocketConnectionManager。
- [×] model-capability legacy 输出消息改为 WebSocketSendQueue 入队发送。
- [×] model-capability legacy 心跳改为 WebSocketHeartbeatLoop 驱动。
- [×] 清理旧内联心跳循环与 model-capability 旧发送锁残留。
- [×] 所有客户端默认订阅广播。
- [×] Feed 发送增加超时，发送失败或超时后剔除慢客户端。
- [×] 增加应用层 ping/pong 心跳发送与 pong 兼容处理。
- [×] 记录最后 pong 时间，超过 90 秒无 pong 时主动断开连接。
- [×] 修复发送失败清理客户端后重复释放发送锁的竞态。

## Step 5 调整依赖和注册 : 80%
- [×] 更新 NetworkServiceExtensions 注册关系。
- [×] 更新 WebSocketConversationFeedRelayService 依赖接口。
- [×] 检查 PluginLoader 使用 WsPort / FeedPort 的时机。
- [×] 确保服务启动成功后再加载插件。

## Step 6 重启和设置页改造 : 80%
- [×] 禁止页面直接 StopAsync + StartAsync。
- [×] 提供服务内部 RestartAsync。
- [×] RestartAsync 串行化。
- [×] 重启失败时保留明确错误状态。
- [×] 暴露 IsRunning、LastError、RestartCount 运行状态。
- [×] UI 提示插件可能需要重连或重启。

## Step 7 稳定性验证 : 74%
- [×] 验证项目可编译。
- [×] 验证解决方案可编译。
- [×] 验证心跳代码可编译。
- [×] 验证心跳超时断开代码可编译。
- [×] 验证完整帧读取与发送锁竞态修复可编译。
- [×] 执行 UI 项目 AOT/Trim 分析构建；当前 Kestrel WebSocket 改造未新增 Networks 侧 AOT/Trim 警告。
- [×] 复验解决方案全量构建通过。
- [×] 验证运行状态属性与重启错误记录代码可编译。
- [×] 通过临时探针验证端口占用时不会崩溃，并可回退到可用端口。
- [×] 通过临时探针验证 Chat 与 Feed WebSocket 路径可完成握手并收到 connected 首帧。
- [×] 通过临时探针验证 RestartAsync 后服务仍运行且 RestartCount 递增。
- [×] 通过临时探针验证 StopAsync 后 IsRunning 变为 false。
- [×] 通过临时探针验证客户端异常断开后服务仍可接受新连接。
- [ ] 验证慢客户端会被剔除。（代码层已实现发送超时剔除，仍需运行级验证）
- [×] 通过临时探针验证服务重启后可继续接收新连接。
- [×] 广播发送已改为客户端级并行，避免单客户端串行阻塞整个广播循环。
- [×] 通过临时探针验证 Feed 多客户端广播均能收到同一消息。
- [×] 通过临时探针验证 Chat token/done/error 正常投递。
- [×] Chat 广播已改为客户端级并行，并通过临时探针验证多客户端均能收到同一消息。
- [×] 验证 Kestrel WebSocket 端点委托等待接收循环，避免连接在首帧后被请求生命周期提前关闭。
- [×] Feed conversation-feed 通道接入连接管理器和发送队列后，临时探针回归通过。
- [×] Feed conversation-feed 通道接入连接管理器和发送队列后，Networks 项目二次构建通过。
- [×] Chat 主通道接入连接管理器和发送队列后，临时探针回归通过。
- [×] Chat 主通道接入连接管理器和发送队列后，Networks 项目构建通过。
- [×] Interaction conversation-feed legacy 通道接入连接管理器和发送队列后，临时探针回归通过。
- [×] Interaction conversation-feed legacy 通道接入连接管理器和发送队列后，Networks 项目构建通过。
- [×] Interaction model-capability legacy 通道接入连接管理器和发送队列后，临时探针回归通过。
- [×] Interaction model-capability legacy 通道接入连接管理器和发送队列后，Networks 项目构建通过。
- [×] 清理旧内联心跳循环后，解决方案 `Netor.Cortana.slnx` 全量构建通过。
- [×] 清理旧内联心跳循环后，临时探针回归通过。
- [×] Feed 服务清理旧内联心跳与 model-capability 旧发送锁残留后，解决方案 `Netor.Cortana.slnx` 全量构建通过。
- [×] Feed 服务清理旧内联心跳与 model-capability 旧发送锁残留后，临时探针回归通过。
- [×] 公共连接基础设施关闭路径竞态加固后，解决方案 `Netor.Cortana.slnx` 全量构建通过。
- [×] 公共连接基础设施关闭路径竞态加固后，临时探针回归通过。

本轮临时运行探针：`artifacts/KestrelWsProbe`。

已验证行为：

- 占用配置端口后，`WebSocketInteractionServerService` 与 `WebSocketFeedServerService` 均未导致进程崩溃。
- 两个服务均回退到可用端口启动，且 `LastError` 在回退成功后保持为空。
- Chat 路径 `/ws/` 可建立 WebSocket 连接并收到 `connected` 首帧。
- Feed 路径 `/internal/conversation-feed/` 可建立 WebSocket 连接并收到 `conversation-feed` 的 `connected` 首帧。
- Chat 客户端可收到 `SendTokenAsync`、`SendDoneAsync`、`SendErrorAsync` 投递的 `token`、`done`、`error` 消息。
- Chat 可同时连接两个客户端，并通过 `BroadcastAsync` 向两个客户端投递同一条广播消息。
- Chat 与 Feed 客户端异常 `Abort` 后，服务仍可继续接受新的 WebSocket 连接。
- Feed 可同时连接两个客户端，并通过 `BroadcastConversationFeedAsync` 向两个客户端投递同一条广播消息。
- `RestartAsync` 后 `RestartCount` 递增到 `1`，服务保持 `IsRunning = true`。
- `RestartAsync` 后，Chat 与 Feed 路径均可再次完成 WebSocket 握手并收到 `connected` 首帧。
- `StopAsync` 后两个服务均变为 `IsRunning = false`。
- Feed conversation-feed 通道改用 `WebSocketConnectionManager` / `WebSocketSendQueue` 后，握手、异常断开、新连接、双客户端广播、重启后握手均通过探针回归。
- Chat 主通道改用 `WebSocketConnectionManager` / `WebSocketSendQueue` 后，握手、异常断开、新连接、token/done/error 投递、双客户端广播、重启后握手均通过探针回归。
- Interaction conversation-feed legacy 通道改用 `WebSocketConnectionManager` / `WebSocketSendQueue` 后，现有 Chat/Feed 握手、异常断开、双客户端广播、重启后握手均通过探针回归。
- Interaction model-capability legacy 通道改用 `WebSocketConnectionManager` / `WebSocketSendQueue` 后，现有 Chat/Feed 握手、异常断开、双客户端广播、重启后握手均通过探针回归。

本轮修复：

- Kestrel WebSocket endpoint 委托现在等待对应 ReceiveLoop 完成，不再 fire-and-forget 接收循环；避免请求委托返回后 Kestrel 结束 WebSocket 请求生命周期，导致连接在欢迎首帧后被动关闭。
- `BroadcastConversationFeedAsync` 改为对客户端发送任务执行 `Task.WhenAll`，单客户端慢发送只占用该客户端发送锁，不再串行阻塞后续客户端发送。
- `BroadcastAsync` 同样改为对客户端发送任务执行 `Task.WhenAll`，保持 Chat 广播和 Feed 广播一致的慢客户端隔离策略。
- Feed conversation-feed 通道发送路径已改为连接管理器入队发送，服务停止、接收循环结束和心跳超时统一走连接管理器关闭流程。
- Chat 主通道发送路径已改为连接管理器入队发送，服务停止、接收循环结束和心跳超时统一走连接管理器关闭流程。
- Interaction conversation-feed legacy 通道发送路径已改为连接管理器入队发送，服务停止、接收循环结束和心跳超时统一走连接管理器关闭流程。
- Interaction model-capability legacy 通道发送路径已改为连接管理器入队发送，服务停止、接收循环结束和心跳超时统一走连接管理器关闭流程。
- `WebSocketInteractionServerService` 旧内联心跳循环、旧 `HasOpenClient` / `SendHeartbeatAsync` / `IsHeartbeatTimedOut` / `CloseHeartbeatTimedOutClient` 与旧 Remove 包装方法已删除。
- `WebSocketFeedServerService` 旧内联心跳循环、model-capability 旧字典发送锁与旧关闭逻辑已删除；model-capability legacy 通道改为连接管理器入队发送。
- `WebSocketSendQueue` 已修复 Dispose 幂等与 worker 故障自清理路径，避免重复释放取消源或故障回调阻塞发送 worker。

## Step 8 文档和兼容说明 : 0%
- [×] 更新 websocket-api 文档。
- [×] 更新插件连接说明。
- [×] 标注 Feed 不再接受客户端命令。
- [×] 标注 replay 的 legacy 兼容策略。
- [×] 标注 memory/model-capability 归属交互控制面和旧入口桥接策略。

---

## 风险点

1. 旧插件可能依赖 Feed 的 `subscribe` 或 `replay` 消息，需要兼容期。
2. Memory 插件当前可能依赖 feed 通道供应上下文，必须提供旧入口 bridge，不能第一阶段破坏。
3. 随机端口会影响插件连接，必须在插件加载前拿到真实监听端口。
4. Kestrel 引入后需确认项目目标框架和引用包是否已具备 ASP.NET Core shared framework。
5. 双 Kestrel host 会增加少量资源占用，但换来生命周期隔离。
6. 如果直接修改端口、路径或消息类型，会导致旧插件和已连接服务大面积失效，必须禁止。

---

## 推荐落地顺序

1. 先建立公共 Kestrel WebSocket 基础设施。
2. 先做零破坏 Kestrel 替换，保持旧端口、旧路径、旧消息类型。
3. 再重构 Chat，保留现有 IChatTransport 行为，并承载 memory/model-capability 交互控制面。
4. 再重构 Feed，使其最终只负责广播，同时保留 subscribe/replay legacy 兼容入口。
5. 最后通过 bridge 和版本标记逐步迁移旧插件。
6. 每一步都做构建和运行验证，不一次性大爆炸改完。

---

## 当前方案涉及文件

预计后续会新增或修改：
- `Src/Netor.Cortana.Networks/WebSockets/Hosting/*`
- `Src/Netor.Cortana.Networks/WebSockets/Connections/*`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketInteractionServerService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketFeedServerService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Relays/WebSocketConversationFeedRelayService.cs`
- `Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`
- `Src/Netor.Cortana.UI/Views/Settings/SystemSettingsPage.axaml.cs`
- `Src/Netor.Cortana.UI/App.axaml.cs`
- `Docs/系统流程与规划/websocket-api.md`
