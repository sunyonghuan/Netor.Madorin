# Madorin v1.3.7 预发布说明

> 状态：预发布草稿，持续补充中。  
> 日期：2026-05-13  
> 用途：记录 v1.3.7 当前已完成或正在收敛的项目修改，正式发布前可继续追加、删改和校准发布边界。

## 预发布概览

v1.3.7 当前重点围绕 **单端点统一插件总线、Memory 插件总线迁移、长期记忆供应链路观测、对话流内实时过程卡片、系统提醒展示优化、主窗口状态持久化和版本整理** 展开。

当前已整理的主要修改包括：

1. 主程序版本号从 `1.3.6` 提升到 `1.3.7`。
2. 内部 WebSocket 通道收敛为统一 PluginBus：`ws://localhost:{port}/internal`。
3. `conversation-feed`、模型能力控制面和聊天传输逐步统一到 `madorin.plugin-bus` 协议。
4. Memory 插件从独立 conversation-feed / model-capability 通道迁移为共享 PluginBus 长连接。
5. 长期记忆供应、模型能力调用、历史回放和对话事件通过 `topic` / `op` 进行分发。
6. 主窗口关闭时保存位置和大小，下一次启动优先恢复上次窗口状态。
7. 对话流内新增实时过程卡片，用于展示思维过程、工具执行和后台步骤。
8. `system.notice` 系统提醒展示方式改为可折叠卡片，视觉风格与过程卡片对齐。
9. 补齐 `App.axaml.cs` 中缺失的 XML 注释，便于后续维护。
10. 新增单端点统一插件总线改造计划文档，作为协议演进和验收依据。

一句话：**本预发布版本正在把内部插件通信从多端点、多协议收敛为一个插件总线，同时增强长期记忆链路可观测性，并补齐对话流过程展示和桌面窗口体验细节。**

---

## 当前核心变更

### 1. 版本号更新

主程序项目版本已更新：

```text
Madorin：1.3.6 -> 1.3.7
```

Memory 插件版本已更新：

```text
Memory Engine：1.0.25 -> 1.0.30
```

### 2. 单端点统一插件总线

新增统一内部插件总线概念，目标端点为：

```text
ws://localhost:{port}/internal
```

协议标识：

```text
madorin.plugin-bus
```

协议版本：

```text
1.0.0
```

当前统一承载以下能力：

- 前端/外部聊天交互消息；
- 插件订阅与心跳；
- conversation 对话事实事件；
- conversation 历史回放；
- memory 长期记忆上下文供应请求与响应；
- model 宿主模型能力请求与响应。

相关端点与协议常量已收敛到 `MadorinWsEndpoints`，包括：

- `PluginBusPath`
- `PluginBusProtocol`
- `PluginBusVersion`
- `ConversationTopic`
- `MemoryTopic`
- `ModelTopic`
- `PluginTopic`
- `ConversationHistoryReplayOperation`
- `MemoryContextSupplyRequestOperation`
- `ModelCapabilityRequestOperation`

### 3. WebSocket 服务重构

网络模块新增统一服务：

```text
WebSocketPluginBusServerService
```

并逐步替代旧服务：

- `WebSocketInteractionServerService`
- `WebSocketFeedServerService`

DI 注册已调整为：

- `IChatTransport` 绑定到 `WebSocketPluginBusServerService`；
- `IPluginBusBroadcaster` 绑定到 `WebSocketPluginBusServerService`；
- `ILongMemorySupplyClient` 绑定到 `WebSocketPluginBusServerService`。

同时新增 PluginBus 相关模块：

- `IPluginBusBroadcaster`
- `PluginBusSubscriptionRegistry`
- `PluginBusChatDispatcher`
- `PluginBusConversationHistoryDispatcher`
- `PluginBusMemorySupplyDispatcher`
- `PluginBusModelCapabilityDispatcher`
- `PluginBusMessageFactory`

这些模块用于拆分连接管理、订阅关系、聊天消息、历史回放、长期记忆供应和模型能力请求等职责，避免单个 WebSocket 服务继续膨胀。

### 4. PluginBus topic/op 分发

新协议通过 `topic` 和 `op` 区分数据面与控制面。

当前主要 topic：

| topic | 用途 |
|---|---|
| `conversation` | 聊天消息、对话事实流、历史回放 |
| `memory` | 长期记忆上下文供应 |
| `model` | 宿主模型能力调用 |
| `plugin` | 插件状态与未来扩展 |

当前主要 op：

| op | 用途 |
|---|---|
| `conversation.event.publish` | 发布实时对话事件 |
| `conversation.history.replay` | 请求历史回放 |
| `conversation.history.batch` | 返回历史回放批次 |
| `conversation.history.completed` | 历史回放完成 |
| `memory.context.supply.request` | 请求长期记忆上下文供应 |
| `memory.context.supply.response` | 返回长期记忆供应包 |
| `memory.context.supply.error` | 长期记忆供应错误 |
| `model.capability.request` | 请求宿主模型能力 |
| `model.capability.response` | 返回宿主模型能力结果 |

### 5. Memory 插件迁移到 PluginBus

Memory 插件已从旧内部通道迁移到统一 PluginBus。

主要变化：

- `MemoryIngestService` 从“连接 conversation-feed”调整为“连接 PluginBus”；
- 订阅 topic 从单一 `conversation` 扩展为 `conversation`、`memory`、`model`；
- 历史回放请求改为 PluginBus request；
- 长期记忆供应请求通过 `memory.context.supply.request` 处理；
- 模型能力调用不再新建短 WebSocket 连接，改为复用同一 PluginBus 长连接；
- 新增 ping/pong 心跳处理；
- 插件设置新增 `PluginBusEndpoint`、`PluginBusPort`、`PluginBusProtocol`、`PluginBusVersion`；
- 旧的 conversation-feed 属性暂时保留为兼容调用点，但实际映射到 PluginBus。

新增 Memory 插件端模块：

- `MemoryPluginBusConnection`
- `MemoryPluginBusDispatcher`
- `MemoryConversationEventHandler`
- `MemorySupplyRequestHandler`

这些模块让 `MemoryIngestService` 更聚焦于连接生命周期和读循环。

### 6. 插件初始化上下文收敛

插件上下文从旧字段：

```text
WsPort
FeedPort
```

收敛为：

```text
PluginBusPort
```

插件初始化扩展字段从多组内部端点：

```text
modelCapabilityEndpoint
conversationFeedEndpoint
chatWsEndpoint
```

收敛为：

```text
pluginBusEndpoint
pluginBusPort
pluginBusPath
pluginBusProtocol
pluginBusVersion
```

`PluginLoader` 也同步改为只向插件传递 `PluginBusPort`。

### 7. 长期记忆供应链路增强

长期记忆上下文供应链路新增日志与统计信息，便于判断 Memory 插件返回和实际注入情况。

`LongMemoryContextProvider` 现在会记录：

- `RequestId`
- `AgentId`
- `WorkspaceId`
- 插件供应数量；
- 通过置信度过滤数量；
- 实际注入 prompt 的去重数量；
- 分组数量；
- 供应置信度；
- 注入文本长度；
- `TraceId`

当供应为空时也会记录调试日志，便于区分：

- Memory 插件未连接；
- 请求超时；
- 没有命中记忆；
- 插件返回为空。

### 8. 历史回放语义修正

PluginBus 历史回放模块补齐：

- 使用 `batchSize + 1` 查询判断是否仍有后续数据；
- `HasMore` 反映真实后续批次状态；
- `conversation.history.batch` 回带原始 `requestId`；
- `conversation.history.completed` 回带原始 `requestId`；
- 历史回放逻辑从 WebSocket 主服务拆入 `PluginBusConversationHistoryDispatcher`。

### 9. 模型能力协议统一

模型能力协议已从旧命名：

```text
llm.invoke
```

收敛为：

```text
model.capability.request
model.capability.response
```

`ModelCapabilityRequest` 和 `ModelCapabilityResponse` 补齐 `topic` / `op` 字段。

宿主处理模型能力请求时校验：

```text
topic = model
op = model.capability.request
```

Memory 插件模型能力调用也改为通过 PluginBus 长连接发送 request，并按 `requestId` 匹配 response。

### 10. 主窗口位置和大小持久化

主窗口新增启动恢复和退出保存逻辑。

保存到系统设置表的键：

```text
UI.MainWindow.X
UI.MainWindow.Y
UI.MainWindow.Width
UI.MainWindow.Height
```

行为说明：

- 应用退出时保存当前主窗口坐标与大小；
- 下次启动时优先恢复上次位置和尺寸；
- 第一次启动或没有设置时保留 XAML 默认大小，并居中显示；
- 保存位置不在当前任意屏幕工作区内时，不强制恢复坐标，避免窗口出现在屏幕外；
- 仅在普通窗口状态下保存，最大化或最小化不会覆盖上次正常尺寸。

### 11. 对话流内实时过程卡片

主对话流新增实时过程卡片，用于承载 AI 运行过程中的思维、工具调用、命令执行和后台处理状态。

主要行为：

- 过程卡片直接插入对话记录流，不再以零散文本打断消息结构；
- 卡片标题栏支持展开和折叠；
- 标题栏左侧使用图标指示展开状态；
- 卡片内容区域支持滚动；
- 内容刷新后默认滚动到底部，便于查看最新输出；
- 过程完成后可自动折叠，降低长过程对聊天记录的占用。

当前该能力主要用于“思维模式”一类过程展示，也可承载后续工具、命令和智能体编排过程。

### 12. system.notice 系统提醒卡片优化

`system.notice` 临时系统提醒继续保持“不入库、不进入长期历史、不触发 AI 对话”的定位，本版本重点优化主界面展示方式。

主要调整：

- 系统提醒改为与实时过程卡片对齐的卡片样式；
- 标题栏左侧加入展开/折叠图片按钮；
- 标题栏增加下边框，视觉层级与过程卡片保持一致；
- 文字颜色与当前过程卡片风格对齐；
- 内容预览默认只显示两行；
- 去掉底部“展开详情 / 收起详情”文字按钮；
- 点击标题栏即可展开或折叠详情；
- 调试构建下默认显示一条示例系统提醒，方便验收样式。

### 13. 注释与代码维护

`App.axaml.cs` 补齐缺失 XML 注释，包括：

- 应用级取消令牌源；
- 关闭状态标记；
- 托盘图标；
- 浮动窗口；
- 气泡窗口；
- 全局服务提供程序；
- 应用生命周期属性；
- `Initialize`；
- `OnFrameworkInitializationCompleted`；
- `UpdateVoiceMenuItemHeader`；
- `RunShutdownStepAsync`。

### 14. 文档与方案补充

新增执行计划文档：

```text
Plugins/docs/memory/执行步骤/S08-单端点统一插件总线协议改造计划.md
```

并在 Memory 架构规划 README 中加入该计划入口。

该文档记录：

- 单端点 PluginBus 设计背景；
- 新协议 envelope 字段；
- `type` / `topic` / `op` 语义；
- 主程序迁移步骤；
- Memory 插件迁移步骤；
- 旧协议清理项；
- 验收标准；
- 复查后继续收敛步骤；
- P0/P1 问题修复步骤。

---

## 当前构建验证

已执行并通过：

```text
dotnet build .\Src\Netor.Madorin.UI\Netor.Madorin.UI.csproj
```

结果：构建成功。

---

## 当前风险与待确认项

以下内容正式发布前建议继续确认：

1. 前端或外部客户端是否全部可以连接新的 `/internal` 路径。
2. 旧 `/ws/` 客户端是否需要发布迁移说明。
3. `PluginBus.Port` 与既有 `WebSocket.Port` 设置项之间的关系是否需要 UI 层明确展示。
4. Memory 插件的 PluginBus 长连接在断线重连场景下是否需要补充自动重连策略。
5. `conversationFeed*` 兼容属性是否在后续正式版前彻底清理。
6. 旧 release 文档中提到的 `/internal/model-capability/` 和 `/internal/conversation-feed/` 是否需要在正式 v1.3.7 中标注为已废弃。

---

## 正式发布前待补充

后续补充时建议继续完善以下章节：

- 完整版本更新表；
- 不兼容变更与迁移提示；
- 插件 SDK / Native 插件兼容边界；
- WebSocket API 文档同步情况；
- Memory 插件运行验收记录；
- 发布包构建与安装验证记录；
- 已知问题与回滚建议。

> 本文档为 v1.3.7 预发布草稿。正式发布时应复制或改写为 `v1.3.7/RELEASE.md`，并删除未完成项或改为明确的“已知问题”。
