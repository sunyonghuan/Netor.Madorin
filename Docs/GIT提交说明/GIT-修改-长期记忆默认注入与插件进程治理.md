# Git 提交记录 - 长期记忆默认注入与插件进程治理

## 提交信息

**提交日期**: 2026-05-10  
**提交类型**: Feature / Fix / Docs  
**影响范围**: 长期记忆系统、Memory 插件、内部 WebSocket 控制面、插件模型能力、插件子进程生命周期、UI 退出流程、发布文档

---

## 修改概述

本次提交继续完善 v1.3.6 阶段能力，重点完成长期记忆从“AI 主动调用工具”到“宿主默认控制面注入”的链路升级，并补齐 Memory 插件运行治理与插件子进程退出清理。

核心变化：

1. 新增长期记忆上下文供应控制面 `memory.context.supply.request/package/error`，宿主在构建主智能体上下文前主动请求 Memory 插件供应长期记忆。
2. 新增 `LongMemoryContextProvider` 和 `LongMemoryPromptFormatter`，将长期记忆分层注入到 `AIContext.Instructions`。
3. Memory 插件接收控制面供应请求，复用 `IMemorySupplyService` 返回结构化供应包，不再依赖 AI 主动调用 `memory_supply_context`。
4. Memory 插件补齐配置工具、删除工具、后台异步触发处理、工具使用指引、启动延迟、自动确认、衰减和召回反衰减。
5. 修复 Memory 插件对话摄取中的 assistant 成功状态识别和 workspaceId 路径哈希问题。
6. 优化记忆提取 prompt 和 JSON 中文明文序列化，减少垃圾片段和 `\uXXXX` 转义。
7. 新增 Windows Job Object 子进程跟踪，确保主程序退出或崩溃时插件子进程自动清理。
8. 优化 UI 退出流程，退出时显式卸载插件/MCP、停止后台服务并释放 DI 容器。
9. 补充 v1.3.6 阶段发布草稿、长期记忆自动注入方案、阶段验收报告和协议文档。

---

## 主要修改

### 1. 长期记忆默认注入控制面

涉及文件：

- `Src/Netor.Cortana.Entitys/MemoryContextSupplyProtocol.cs`
- `Src/Netor.Cortana.Entitys/Memory/MemoryContextSupplyMessages.cs`
- `Src/Netor.Cortana.AI/Memory/ILongMemorySupplyClient.cs`
- `Src/Netor.Cortana.AI/Memory/LongMemoryContextProvider.cs`
- `Src/Netor.Cortana.AI/Memory/LongMemoryPromptFormatter.cs`
- `Src/Netor.Cortana.AI/AIServiceExtensions.cs`
- `Src/Netor.Cortana.AI/AiChatHostedService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketServerService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs`
- `Src/Netor.Cortana.Networks/Extensions/NetworkServiceExtensions.cs`

修改内容：

- 新增协议操作：
  - `memory.context.supply.request`
  - `memory.context.supply.response`
  - `memory.context.supply.error`
- 宿主通过现有 `/internal` 长连接发送长期记忆供应请求。
- `WebSocketServerService` / `WebSocketPluginBusServerService` 实现 `ILongMemorySupplyClient`，维护 `requestId` 到 pending response 的映射。
- 请求默认短超时，超时、错误、无插件连接或无命中时静默降级为空上下文。
- `LongMemoryContextProvider` 从 session state、工作区路径和系统设置中构建供应请求。
- `LongMemoryPromptFormatter` 将供应包格式化为两段：
  - `关于用户的长期认知`
  - `本次对话相关记忆`
- `AiChatHostedService` 在每轮对话前写入 `turnId`、`traceId`、`userMessageId`、`currentTask`，供长期记忆上下文使用。

行为变化：

- 主对话默认注入不再依赖 AI 是否主动调用 `memory_supply_context`。
- Memory 插件未启动、未连接或响应慢时不会阻塞主回复。
- 长期记忆与当前明确指令冲突时，以当前用户指令为准。

### 2. Memory 插件控制面与供应策略

涉及文件：

- `Plugins/Src/Cortana.Plugins.Memory/Models/MemoryContextSupplyProtocol.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Models/MemoryContextSupplyMessages.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Models/MemorySupplyModels.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/MemorySupplyControlHandler.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/MemoryIngestService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/MemorySupplyService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Startup.cs`

修改内容：

- 插件侧新增控制面协议镜像 DTO，避免 Memory 插件直接依赖宿主实体程序集，保障 AOT 发布。
- `MemoryIngestService` 在 feed 长连接中识别 `memory.context.supply.request` 并返回 package/error。
- `MemorySupplyControlHandler` 将宿主请求映射到 `MemorySupplyRequest`。
- 控制面请求中 `agentId` 必填，缺失时返回错误，不再回退到 `default`。
- `MemorySupplyRequest` 新增 `SessionTitle`，用于宽泛主题召回。
- `MemorySupplyService` 实现双路召回：
  - 最新用户消息做精确召回；
  - session 标题和场景做宽泛召回。
- 供应结果分层排序：profile/abstraction 优先，其次 constraint、preference、task、fact。

行为变化：

- `memory_supply_context` 工具保留给 AI 主动查询、MCP、调试和验收。
- 默认注入链路走宿主控制面，结构化字段更完整，作用域更可靠。

### 3. Memory 插件处理链路异步化和模型调用治理

涉及文件：

- `Plugins/Src/Cortana.Plugins.Memory/Processing/IMemoryProcessingService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/IMemorySemanticProcessor.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/IMemoryAbstractionService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/IMemoryAbstractionGenerator.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/MemoryProcessingService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/MemoryAbstractionService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostModelMemorySemanticProcessor.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostModelMemoryAbstractionGenerator.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/FallbackMemorySemanticProcessor.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/FallbackMemoryAbstractionGenerator.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/HostModelCapabilityClient.cs`
- `Src/Netor.Cortana.AI/Providers/PluginModelCapabilityService.cs`

修改内容：

- 记忆处理、语义提取和抽象生成接口改为 async。
- 宿主模型调用使用 `SemaphoreSlim` 串行化，避免并发请求造成模型端失败。
- 记忆抽象模型不可用时跳过，不再用低质量 fallback 生成抽象。
- 语义提取模型不可用时仍允许降级到本地规则处理器。
- `HostModelCapabilityClient` 调用结束后主动关闭 WebSocket。
- 模型能力默认超时从过短配置规范化为 120s，防止后台记忆处理频繁超时。

### 4. Memory 插件质量、配置和治理工具

涉及文件：

- `Plugins/Src/Cortana.Plugins.Memory/Startup.cs`
- `Plugins/Src/Cortana.Plugins.Memory/ToolHandlers/IMemoryWriteToolHandler.cs`
- `Plugins/Src/Cortana.Plugins.Memory/ToolHandlers/MemoryWriteToolHandler.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Tools/MemoryWriteTools.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Tools/MemoryToolJsonContext.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Tools/MemoryToolResult.cs`
- `Plugins/Src/Cortana.Plugins.Memory/ToolHandlers/MemoryReadToolHandler.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Mcp/MemoryMcpToolHandler.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Serialization/MemoryInternalJsonContext.cs`

修改内容：

- 插件 `Instructions` 增加记忆工具使用指引，明确 AI 何时写入、召回、查看、删除和调整配置。
- 新增工具：
  - `memory_get_settings`
  - `memory_update_setting`
  - `memory_delete`
  - `memory_trigger_processing`
- `memory_trigger_processing` 改为后台异步触发，避免工具调用超时。
- 配置更新和删除操作均要求用户明确授权，并写入审计记录。
- 内部和工具 JSON 上下文新增 `Chinese` 实例，使用中文明文序列化。

### 5. 记忆摄取、状态和衰减机制修复

涉及文件：

- `Plugins/Src/Cortana.Plugins.Memory/Services/MemoryIngestService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/MemoryRecallService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/MemoryProcessingHostedService.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Storage/IMemoryStore.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Storage/MemoryStore.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Storage/MemoryFragmentsTable.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Storage/MemoryAbstractionsTable.cs`

修改内容：

- 修复 assistant 成功轮次状态识别：兼容 `ConversationTurnStatus.Succeeded` 被序列化为数字 `0` 的情况。
- 修复 `workspaceId`：路径 fallback 改为 MD5 哈希，匹配宿主工作区标识策略。
- 处理器启动后延迟 30 秒再执行首轮处理，等待 feed 连接和 observations 写入。
- 召回访问时提升 `retentionScore`，实现反衰减。
- 候选片段访问次数达到阈值后自动升级为 `active/confirmed`。
- 周期执行衰减：
  - 降低 `retentionScore`；
  - 低于最低保留分标记为 `fading`；
  - 低于遗忘阈值标记为 `forgotten`。
- 新增按工作区聚合抽象所需的 workspace 列表读取。

### 6. 插件子进程退出治理

涉及文件：

- `Src/Netor.Cortana.Plugin/Core/ChildProcessTracker.cs`
- `Src/Netor.Cortana.Plugin/Core/ExternalProcessPluginHostBase.cs`
- `Src/Netor.Cortana.Plugin/PluginLoader.cs`
- `Src/Netor.Cortana.UI/App.axaml.cs`
- `Src/Netor.Cortana.UI/Views/MainWindow.axaml.cs`

修改内容：

- 新增 `ChildProcessTracker`，Windows 下使用 Job Object 跟踪所有插件子进程。
- 子进程启动后立即加入 Job Object，父进程退出或崩溃时由操作系统自动清理。
- `ExternalProcessPluginHostBase.KillProcess()` 增加 kill 后等待退出。
- `PluginLoader` 实现 `IAsyncDisposable`，退出时异步释放 MCP host。
- UI 退出流程改为统一 shutdown：
  - 取消业务任务；
  - 卸载插件/MCP；
  - 停止后台服务；
  - 释放 DI 容器。
- 主窗口关闭逻辑兼容应用退出状态，避免托盘退出时窗口只隐藏不退出。

### 7. 文档和发布草稿

涉及文件：

- `Docs/release-notes/v1.3.6/DRAFT.md`
- `Plugins/Src/Cortana.Plugins.Memory/Docs/长期记忆上下文自动注入方案.md`
- `Plugins/Src/Cortana.Plugins.Memory/Docs/执行计划(长期记忆上下文自动注入).md`
- `Plugins/Src/Cortana.Plugins.Memory/Docs/阶段验收报告(长期记忆上下文自动注入-2026-05-10).md`
- `Plugins/Src/Cortana.Plugins.Memory/Docs/项目总进度表(2026-05-10).md`
- `Plugins/Src/Cortana.Plugins.Memory/Docs/AI工具暴露规划.md`
- `Plugins/docs/memory/构架规划/03-内部事件WebSocket协议.md`
- `Plugins/docs/memory/执行步骤/S05-控制面通道骨架（能力申请与召回）.md`

修改内容：

- 补充 v1.3.6 阶段发布草稿，记录长期记忆默认注入、记忆治理和插件子进程清理。
- 新增长期记忆自动注入方案和执行计划。
- 新增阶段验收报告和项目总进度表。
- 更新内部 WebSocket 协议文档，加入 `memory.context.supply.*` 请求/响应示例。
- 更新 AI 工具暴露规划，明确 `memory_supply_context` 不承担默认注入职责。

### 8. 插件版本同步

涉及文件：

- 多个 `Plugins/Src/Cortana.Plugins.*/*.csproj`
- 多个插件 `Startup.cs`

修改内容：

- 同步提升本地插件版本号，便于重新发布和识别新包。
- Memory 插件版本更新到 `1.0.18`。

---

## 验证记录

已执行：

```text
dotnet build Plugins/Src/Cortana.Plugins.Memory/Cortana.Plugins.Memory.csproj --no-restore
```

结果：通过，0 错误 0 警告。

```text
dotnet build Src/Netor.Cortana.Plugin/Netor.Cortana.Plugin.csproj --no-restore
```

结果：通过，0 错误 0 警告。

```text
Plugins/publish.cmd
```

结果：通过。

```text
Build/ui.publish.cmd
```

结果：通过。

运行态待验收：

- Memory 插件已连接但无命中时不注入长期记忆块。
- 存在相关记忆时主对话上下文包含 `--long-term-memory--`。
- `agentId`、`workspaceId`、`workspaceDirectory` 在运行态正确传递。
- 主程序异常退出时 `Cortana.NativeHost.exe` 是否被 Job Object 自动清理。

---

## 提交建议

建议提交信息：

```text
feat: 增强长期记忆默认注入与插件进程治理
```

提交正文建议：

```text
- 新增长期记忆 memory.supply 控制面协议和宿主默认注入链路
- 增加 Memory 插件供应控制处理、双路召回和分层 prompt 注入
- 补齐记忆配置、删除、异步处理、自动确认和衰减机制
- 修复 assistant 状态识别、workspaceId 哈希和中文 JSON 序列化
- 新增 Windows Job Object 插件子进程跟踪与统一退出流程
- 更新 v1.3.6 阶段发布草稿和长期记忆设计/验收文档
```


