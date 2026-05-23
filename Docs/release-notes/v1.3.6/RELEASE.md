# Madorin v1.3.6 发布说明

> 状态：正式发布文档。  
> 日期：2026-05-13  
> 用途：记录 v1.3.6 正式发布内容、主要变更和发布边界。

## 发布概览

v1.3.6 重点围绕 **插件授权、宿主模型能力、长期记忆默认注入、WebSocket 临时系统提示和插件生命周期治理** 展开。

本版本主要包含以下能力：

1. 新增 `system.notice` 临时系统提示协议，让插件、第三方程序和内部流程可以向主界面发送不入库的系统提示。
2. 新增插件模型能力控制面 `/internal/model-capability/`，由宿主统一代理插件的大模型调用。
3. 新增“插件授权”设置页，为插件或 MCP 服务配置大模型授权。
4. Memory 插件接入宿主模型能力，优先使用授权模型进行记忆提取和抽象生成，失败时回退本地 fallback。
5. 新增长期记忆默认注入链路，宿主在构建主智能体上下文前通过内部控制面请求 Memory 插件供应长期记忆。
6. 增强 Memory 插件治理能力，包括 AI 工具使用指引、配置工具、删除工具、异步触发处理、启动延迟、自动确认和衰减机制。
7. 优化插件子进程退出治理，Windows 下使用 Job Object 跟踪插件子进程，降低 `Madorin.NativeHost.exe` 残留风险。
8. 优化主界面临时提示展示、发送中输入框动效和连接状态提示方式。
9. 更新插件开发、安装、WebSocket 接入和技能编写文档，为后续插件开发流程打基础。

一句话：**本版本把插件从“只能提供工具”推进到“可在授权后调用宿主模型能力和长期记忆控制面”，并补齐了临时系统提示、记忆治理和插件进程清理能力。**

---

## 核心变更

### 1. system.notice 临时系统提示

新增 WebSocket 客户端到宿主的消息类型：

```text
system.notice
```

该协议用于展示临时系统提示，例如：

- 插件执行过程提示；
- 第三方软件状态同步；
- 工具调用进度；
- 模型审核、后台任务或外部流程提示；
- WebSocket/MCP 等连接状态变化。

示例消息：

```json
{
  "type": "system.notice",
  "data": "正在执行外部工具...",
  "title": "工具调用",
  "level": "progress",
  "source": "Photoshop"
}
```

当前行为：

- 只在当前主界面临时显示；
- 不写入 `ChatMessages`；
- 不进入长期历史；
- 不触发 AI 对话；
- 长内容超过 300 字时默认折叠，可展开/收起；
- 旧客户端可以忽略未知 `type`，不影响原协议。

### 2. 插件模型能力控制面

新增内部模型能力协议：

```text
ws://localhost:{port}/internal/model-capability/
```

该通道是宿主与插件/本机辅助进程之间的内部控制面，用于让插件在获得授权后调用宿主配置的大模型。

当前设计边界：

- 插件只提交业务输入和任务指令；
- 宿主负责授权校验、Provider/Model 选择、驱动适配、模型调用和错误映射；
- 插件不直接感知具体模型厂商协议；
- 控制面不作为公网 API；
- 调用失败时返回统一错误响应。

插件初始化扩展字段新增：

- `modelCapabilityEndpoint`
- `modelCapabilityPort`
- `modelCapabilityPath`
- `modelCapabilityProtocol`
- `modelCapabilityVersion`

### 3. 插件授权设置页

设置窗口新增：

```text
插件授权
```

本版本提供“大模型授权”配置，支持本地插件和 MCP 服务。

可配置项：

- 是否允许使用大模型；
- 绑定 Provider；
- 绑定 Model；
- 最大输入 Token；
- 最大输出 Token；
- 超时时间；
- 最大并发数；
- 是否允许后台调用。

当前定位：

- 这是插件能力授权体系的第一阶段；
- 后续可继续扩展文件、网络、后台任务、系统控制等授权卡片；
- 插件调用宿主模型能力前必须显式授权。

### 4. Memory 插件接入宿主模型能力

Memory 插件新增宿主模型能力客户端，并调整语义处理链路：

- 新增 `HostModelCapabilityClient`；
- 新增宿主模型语义提取器；
- 新增宿主模型长期记忆抽象生成器；
- DI 默认优先使用宿主模型实现；
- 宿主模型不可用、未授权或调用失败时回退原 fallback 实现。

插件 ID 解析顺序：

1. 优先读取宿主下发扩展字段 `pluginId`；
2. 其次读取插件目录下的 `plugin.json`；
3. 最后 fallback 到 `memory_engine`。

本版本意义：

- Memory 插件不再只能依赖规则或本地 fallback；
- 在用户授权后，可以使用宿主统一配置的模型进行记忆理解；
- 模型调用细节继续收口在宿主侧。

### 5. 长期记忆上下文默认注入

新增长期记忆默认注入控制面协议：

```text
memory.supply.request
memory.supply.package
memory.supply.error
```

本版本没有新增独立 HTTP/WS 端点，而是复用 `/internal/conversation-feed/` 长连接承载轻量请求/响应：

```text
宿主 LongMemoryContextProvider
   → ILongMemorySupplyClient
   → WebSocketServerService / WebSocketFeedServerService
   → /internal/conversation-feed/ memory.supply.request
   → MemoryIngestService
   → MemorySupplyControlHandler
   → IMemorySupplyService
   → memory.supply.package
   → LongMemoryPromptFormatter
   → AIContext.Instructions
```

关键行为：

- 默认注入不再依赖 AI 主动调用 `memory_supply_context` 工具；
- `memory_supply_context` 继续保留给 AI 主动查询、MCP、调试和验收；
- `agentId` 在控制面请求中为必填，插件侧缺失即返回错误，不再静默回退到 `default`；
- 宿主按 `requestId` 等待响应，默认 250ms 级短超时，超时、错误、插件未连接或无命中均降级为空上下文；
- 宿主侧负责最终 prompt 拼接，插件只返回结构化供应包；
- 注入文本按“关于用户的长期认知”和“本次对话相关记忆”分层展示，并提示当前用户指令优先。

### 6. Memory 插件治理与质量优化

Memory 插件本版本继续补齐长期运行所需的治理能力：

- 新增 AI 工具使用指引，明确何时调用 `memory_add_note`、`memory_recall`、`memory_list_recent`、`memory_get_status`、`memory_delete`、`memory_get_settings` 和 `memory_update_setting`；
- 新增配置读取/更新工具，可查看和调整供应、召回、衰减、保留、抽象和审计配置；
- 新增记忆删除工具，按用户明确授权软删除指定记忆并写入审计；
- `memory_trigger_processing` 改为后台异步触发，避免大量 observations 时阻塞工具调用；
- 记忆处理器首次运行延迟 30 秒，等待 feed 连接和首批观察记录写入；
- 记忆提取与抽象模型调用改为 async，并通过信号量串行化，避免并发模型调用导致失败；
- JSON 序列化改用中文明文输出，避免中文被转义为 `\\uXXXX`；
- 优化记忆提取 prompt，严格过滤 AI 客套话、工具调用描述、短文本、日志、代码输出和无长期价值内容；
- 清理既有垃圾片段，并重置处理状态，让新 prompt 重新生效；
- 新增召回反衰减：记忆被访问时提升 `retentionScore`；
- 新增候选片段自动确认：候选记忆被召回达到阈值后升级为 `active/confirmed`；
- 新增衰减执行：后台定期降低保留分，低于阈值后进入 `fading` 或 `forgotten`。

### 7. 插件子进程生命周期治理

Native 插件当前由 `Madorin.NativeHost.exe` 子进程加载，每个 Native 插件对应一个宿主子进程。为避免主程序异常退出或托盘退出时留下大量插件进程，本版本补强子进程清理：

- 新增 `ChildProcessTracker`，Windows 下使用 Job Object 跟踪插件子进程；
- `ExternalProcessPluginHostBase.StartProcess()` 启动子进程后立即加入 Job；
- 主进程退出或崩溃时，操作系统会自动终止 Job 中的所有插件子进程；
- `PluginLoader` 增加 `IAsyncDisposable`，退出时可异步释放 MCP 和插件宿主；
- UI 退出流程统一为“取消业务任务 → 卸载插件/MCP → 停止后台服务 → 释放 DI 容器”，降低 `Madorin.NativeHost.exe` 驻留风险。

## 发布边界补充

本节用于明确 v1.3.6 的正式发布价值与发布边界。

- 明确 `system.notice` 为“主界面临时系统提示”，不写入聊天历史、不触发 AI 对话、仅用于当前界面展示。
- 强调插件模型能力由宿主统一代理，插件只提交业务输入，宿主负责授权、模型选择和异常处理。
- 说明长期记忆默认注入链路为“宿主主动请求 Memory 插件供应上下文”，非 AI 主动工具调用。
- 额外补充 Memory 插件治理能力提升，包括工具使用指引、配置工具、删除工具、后台触发和衰减策略。
- 说明子进程治理通过 Job Object 和统一退出流程降低宿主退出时插件进程残留风险。

> 本发布说明作为 v1.3.6 正式发布文档，后续补丁或增量能力请在新版本发布说明中继续记录。

### 8. UI 体验调整

本版本包含若干界面体验调整：

- 主界面新增临时系统提示卡片；
- WebSocket 客户端连接/断开提示改为系统提示卡片；
- MCP 连接恢复/断开提示改为系统提示卡片；
- 输入框在发送中显示边框跑马灯动效；
- 更新发送图标资源；
- UI 项目版本号更新到 `1.3.6`。

### 9. 插件开发与技能文档更新

插件相关技能和脚本继续完善：

- `plugin-development` 技能更新到新版本；
- Native 插件脚手架新增 Debug Console 项目；
- Native/Process 插件发布前必须先完成本地调试验证；
- 新增 Dotnet 托管插件创建脚本；
- 新增 Dotnet 托管插件发布脚本；
- 修正多个子技能中的相对路径说明；
- `skill-plugin-installation` 明确插件默认全局安装流程；
- `skill-writer` 补充全局技能/项目技能创建位置选择；
- `websocket-integration` 补充内部通道端口来源、`system.notice`、模型能力端点等说明。

---

## 已知限制与后续计划

以下内容不阻塞 v1.3.6 正式发布，但仍需要在后续版本中继续补充或验证：

1. **Memory 插件模型能力端到端验证未完成**
   - 需要在真实运行环境中验证 Memory 插件通过授权模型完成一次宿主大模型调用；
   - 需要验证未授权、模型不可用或调用失败时完整回退 fallback。

2. **长期记忆默认注入仍需真实命中链路验收**
   - 需要验证 Memory 插件已连接但无命中时不注入长期记忆块；
   - 需要验证存在相关记忆时主对话上下文包含 `--long-term-memory--` 块；
   - 需要验证 `agentId`、`workspaceId`、`workspaceDirectory` 在运行态正确传递。

3. **插件授权体系仍是第一阶段**
   - 当前只实现大模型授权；
   - 文件、网络、后台任务、系统能力等授权仍待后续扩展。

4. **智能体工具上下文刷新问题尚未实现**
   - 已记录设计讨论；
   - 后续倾向引入工具上下文版本号或 dirty 标记，在下一轮对话前懒重建 Agent。

---

## 当前验证记录

已完成验证：

```text
dotnet build .\Src\Netor.Madorin.UI\Netor.Madorin.UI.csproj
```

结果：成功。

```text
dotnet build .\Plugins\Src\Madorin.Plugins.Memory\Madorin.Plugins.Memory.csproj
```

结果：成功。

```text
dotnet build .\Src\Netor.Madorin.Plugin\Netor.Madorin.Plugin.csproj --no-restore
```

结果：成功。

代码路径验证：

- `system.notice` 只显示临时系统提示，不触发 AI 对话；
- 原有 `send` / `stop` 语义保持不变；
- 长系统提示支持折叠展示；
- Memory 插件与主程序模型能力相关代码可编译。
- 长期记忆控制面协议、宿主 Provider、插件侧处理器和 prompt formatter 已完成代码链路；
- Memory 插件处理器启动延迟、自动确认、衰减、召回反衰减和中文明文 JSON 序列化已完成代码验证；
- 插件子进程 Job Object 跟踪在 `Netor.Madorin.Plugin` 项目构建中通过。

待补充验证：

- 插件授权页保存后，Memory 插件通过宿主获得大模型输出；
- 未授权或模型不可用时 Memory 插件降级 fallback；
- `/internal/model-capability/` 在运行环境中的连接、请求和错误响应；
- 长期记忆默认注入在真实运行态的有命中/无命中行为；
- `system.notice` 外部脚本联调；
- 插件授权页在无 Provider/Model、多个插件和 MCP 服务场景下的 UI 行为。

---

## 文件参考

### 文档与计划

- `Docs/system.notice临时系统信息协议方案.md`
- `Docs/执行计划(system.notice临时系统信息协议实现).md`
- `Docs/执行计划(模型能力协议简化与插件元数据读取).md`
- `Plugins/Src/Madorin.Plugins.Memory/Docs/长期记忆上下文自动注入方案.md`
- `Plugins/Src/Madorin.Plugins.Memory/Docs/执行计划(长期记忆上下文自动注入).md`
- `Plugins/Src/Madorin.Plugins.Memory/Docs/阶段验收报告(长期记忆上下文自动注入-2026-05-10).md`
- `Plugins/Src/Madorin.Plugins.Memory/Docs/项目总进度表(2026-05-10).md`
- `Docs/【未解决】智能体创建时机与工具上下文刷新问题讨论.md`
- `Docs/GIT提交说明/GIT-修改-模型能力授权与临时系统提示.md`

### 宿主模型能力

- `Src/Netor.Madorin.Entitys/ModelCapabilityProtocol.cs`
- `Src/Netor.Madorin.Entitys/ModelCapability/ModelCapabilityMessages.cs`
- `Src/Netor.Madorin.AI/Providers/IPluginModelCapabilityService.cs`
- `Src/Netor.Madorin.AI/Providers/PluginModelCapabilityService.cs`
- `Src/Netor.Madorin.Networks/WebSockets/Servers/WebSocketServerService.cs`
- `Src/Netor.Madorin.Networks/WebSockets/Servers/WebSocketFeedServerService.cs`

### 长期记忆默认注入

- `Src/Netor.Madorin.Entitys/MemoryContextSupplyProtocol.cs`
- `Src/Netor.Madorin.Entitys/Memory/MemoryContextSupplyMessages.cs`
- `Src/Netor.Madorin.AI/Memory/ILongMemorySupplyClient.cs`
- `Src/Netor.Madorin.AI/Memory/LongMemoryContextProvider.cs`
- `Src/Netor.Madorin.AI/Memory/LongMemoryPromptFormatter.cs`
- `Src/Netor.Madorin.AI/AIServiceExtensions.cs`
- `Src/Netor.Madorin.AI/AiChatHostedService.cs`

### Memory 插件

- `Plugins/Src/Madorin.Plugins.Memory/Services/HostModelCapabilityClient.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Processing/HostModelMemorySemanticProcessor.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Processing/HostModelMemoryAbstractionGenerator.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Services/MemorySupplyControlHandler.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Models/MemoryContextSupplyProtocol.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Models/MemoryContextSupplyMessages.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Processing/MemoryProcessingHostedService.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Storage/MemoryStore.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Startup.cs`

### 插件生命周期

- `Src/Netor.Madorin.Plugin/Core/ChildProcessTracker.cs`
- `Src/Netor.Madorin.Plugin/Core/ExternalProcessPluginHostBase.cs`
- `Src/Netor.Madorin.Plugin/PluginLoader.cs`
- `Src/Netor.Madorin.UI/App.axaml.cs`

### UI 与授权页

- `Src/Netor.Madorin.UI/Views/Settings/PluginAuthorizationPage.axaml`
- `Src/Netor.Madorin.UI/Views/Settings/PluginAuthorizationPage.axaml.cs`
- `Src/Netor.Madorin.UI/Views/Main/MainWindow.Messaging.cs`
- `Src/Netor.Madorin.UI/Views/MainWindow.axaml`
- `Src/Netor.Madorin.UI/App.axaml.cs`

### 技能与脚本

- `skills/websocket-integration/SKILL.md`
- `skills/plugin-development/SKILL.md`
- `skills/plugin-development/scripts/create-native-plugin.ps1`
- `skills/plugin-development/scripts/create-dotnet-plugin.ps1`
- `skills/plugin-development/scripts/publish-dotnet-plugin.ps1`
- `skills/skill-plugin-installation/SKILL.md`
- `skills/skill-writer/SKILL.md`

---

## 后续版本记录建议

后续继续开发新版本时，建议按以下方式记录：

1. 新增功能写入“核心变更”或新增章节；
2. 修复项单独增加“修复记录”；
3. 不兼容行为增加“迁移提示”；
4. 每次补充验证都追加到“当前验证记录”；
5. 正式发布前统一整理为对应版本的 `RELEASE.md`。

