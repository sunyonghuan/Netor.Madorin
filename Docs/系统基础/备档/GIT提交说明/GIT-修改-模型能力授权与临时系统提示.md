# Git 提交记录 - 模型能力授权与临时系统提示

## 提交信息

**提交日期**: 2026-05-09
**提交类型**: Feature / Docs
**影响范围**: 插件大模型授权、Memory 插件宿主模型调用、WebSocket 内部协议、临时系统提示 UI、插件开发技能文档

---

## 修改概述

本次提交围绕插件与宿主能力协作完成多项改造：

1. 新增 `system.notice` 临时系统提示协议，允许插件、第三方程序或内部流程向主界面追加不入库的系统提示卡片。
2. 新增插件模型能力控制面 `/internal/model-capability/`，由宿主统一代理插件的大模型调用。
3. 为设置页增加“插件授权”入口，支持按插件或 MCP 服务配置大模型授权、模型、Token、超时和并发参数。
4. Memory 插件接入宿主模型能力，优先使用授权模型提取记忆语义和生成长期记忆抽象，失败时回退本地 fallback。
5. 更新插件开发、安装、WebSocket 接入和技能编写相关文档，补齐调试和安装规范。
6. UI 侧优化发送中输入框动效，并将连接/MCP 状态通知迁移为临时系统提示卡片。

---

## 主要修改

### 1. system.notice 临时系统提示协议

涉及文件：

- `Docs/system.notice临时系统信息协议方案.md`
- `Docs/执行计划(system.notice临时系统信息协议实现).md`
- `Src/Netor.Cortana.Entitys/Events.cs`
- `Src/Netor.Cortana.Entitys/Interfaces/IChatTransport.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Channels/WebSocketInputChannel.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketServerService.cs`
- `Src/Netor.Cortana.UI/Views/MainWindow.axaml.cs`
- `Src/Netor.Cortana.UI/Views/Main/MainWindow.Messaging.cs`
- `skills/websocket-integration/SKILL.md`

修改内容：

- 新增 `Events.OnSystemNotice`、`SystemNoticeEvent`、`SystemNoticeArgs`。
- WebSocket 客户端消息新增完整消息事件 `OnClientMessageReceived`，保留 `type/data/attachments` 并读取 `title/level/source` 扩展字段。
- `WebSocketInputChannel` 增加 `system.notice` 分支，只发布临时系统提示，不调用 AI 对话。
- 主界面新增 `AddSystemNotice(...)`，使用独立系统卡片展示提示内容。
- 长内容超过 300 字时默认折叠，支持展开/收起。
- WebSocket 技能文档新增 `system.notice` 协议说明和 C# 客户端示例。

行为变化：

- `system.notice` 不写入聊天历史，不触发 AI 回复。
- WebSocket 连接/MCP 状态通知改为系统提示卡片展示。
- 外部客户端可发送如下消息向 UI 推送临时提示：

```json
{
  "type": "system.notice",
  "data": "正在执行外部工具...",
  "title": "工具调用",
  "level": "progress",
  "source": "Photoshop"
}
```

### 2. 插件模型能力控制面

涉及文件：

- `Src/Netor.Cortana.Entitys/ModelCapabilityProtocol.cs`
- `Src/Netor.Cortana.Entitys/ModelCapability/ModelCapabilityMessages.cs`
- `Src/Netor.Cortana.Entitys/CortanaWsEndpoints.cs`
- `Src/Netor.Cortana.AI/Providers/IPluginModelCapabilityService.cs`
- `Src/Netor.Cortana.AI/Providers/PluginModelCapabilityService.cs`
- `Src/Netor.Cortana.AI/AIServiceExtensions.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketServerService.cs`
- `Src/Netor.Cortana.Networks/WebSockets/Servers/WebSocketPluginBusServerService.cs`
- `Src/Netor.Cortana.Plugin/Core/ExternalProcessPluginHostBase.cs`

修改内容：

- 新增内部 WebSocket 路径 `/internal/model-capability/`。
- 新增简化请求/响应模型：插件只提交 `pluginId`、`purpose`、`instruction`、`input`、`outputFormat` 等业务字段。
- 宿主内部负责授权校验、模型选择、驱动适配、`ChatOptions` 构造、超时和并发控制。
- 插件初始化扩展字段新增：
  - `modelCapabilityEndpoint`
  - `modelCapabilityPort`
  - `modelCapabilityPath`
  - `modelCapabilityProtocol`
  - `modelCapabilityVersion`
- 网络层 JSON 源生成上下文补充模型能力协议类型。

行为变化：

- 插件不再直接感知宿主具体 Provider/Model 协议细节。
- 未授权、模型不可用、超时或内部错误时，控制面返回统一错误响应。
- Feed 服务和主 WebSocket 服务均支持模型能力内部通道。

### 3. Memory 插件接入宿主模型能力

涉及文件：

- `Plugins/Src/Cortana.Plugins.Memory/Startup.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/HostModelCapabilityClient.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Services/HostModelCapabilityMessages.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostModelMemorySemanticProcessor.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostModelMemoryAbstractionGenerator.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostSemanticCandidate.cs`
- `Plugins/Src/Cortana.Plugins.Memory/Processing/HostAbstractionResult.cs`

修改内容：

- 新增 `HostModelCapabilityClient`，通过宿主注入的模型能力端点调用授权大模型。
- 插件 ID 优先从 `PluginSettings.Extensions["pluginId"]` 读取，其次尝试读取 `plugin.json`，最后 fallback 到 `memory_engine`。
- 新增宿主模型语义提取器 `HostModelMemorySemanticProcessor`。
- 新增宿主模型抽象生成器 `HostModelMemoryAbstractionGenerator`。
- DI 默认使用宿主模型实现，同时保留 fallback 实现作为降级路径。

行为变化：

- Memory 插件在获得授权时，可使用宿主模型完成记忆提取和抽象。
- 宿主模型调用失败时自动回退原有 fallback 逻辑。

### 4. 插件授权设置页

涉及文件：

- `Src/Netor.Cortana.UI/Views/SettingsWindow.axaml`
- `Src/Netor.Cortana.UI/Views/SettingsWindow.axaml.cs`
- `Src/Netor.Cortana.UI/Views/Settings/PluginAuthorizationPage.axaml`
- `Src/Netor.Cortana.UI/Views/Settings/PluginAuthorizationPage.axaml.cs`
- `Src/Netor.Cortana.UI/Assets/qx.png`

修改内容：

- 设置窗口新增“插件授权”导航项。
- 新增插件授权页，展示已加载本地插件和 MCP 服务。
- 支持搜索插件名称、ID、简介和类型。
- 支持配置大模型授权：
  - 启用/禁用
  - Provider
  - Model
  - 最大输入 Token
  - 最大输出 Token
  - 超时时间
  - 最大并发数
  - 是否允许后台调用
- 授权配置保存到 `SystemSettingsService` 的隐藏设置项。

行为变化：

- 插件大模型调用必须先在设置中显式授权。
- 授权页为后续文件、网络、后台任务等能力授权预留结构。

### 5. UI 交互与版本更新

涉及文件：

- `Src/Netor.Cortana.UI/Views/MainWindow.axaml`
- `Src/Netor.Cortana.UI/Views/Main/MainWindow.Messaging.cs`
- `Src/Netor.Cortana.UI/App.axaml.cs`
- `Src/Netor.Cortana.UI/Assets/send.png`
- `Src/Netor.Cortana.UI/Assets/send.bak.png`
- `Src/Netor.Cortana.UI/Netor.Cortana.UI.csproj`

修改内容：

- 输入框发送中增加边框跑马灯动效。
- 发送按钮图标资源更新，并保留备份图标。
- WebSocket 客户端连接/断开通知改为 `AddSystemNotice(...)`。
- MCP 连接恢复/断开通知改为 `AddSystemNotice(...)`。
- UI 版本从 `1.3.5` 更新到 `1.3.6`。

### 6. 插件开发与安装技能文档更新

涉及文件：

- `skills/plugin-development/SKILL.md`
- `skills/plugin-development/scripts/create-native-plugin.ps1`
- `skills/plugin-development/scripts/create-dotnet-plugin.ps1`
- `skills/plugin-development/scripts/publish-dotnet-plugin.ps1`
- `skills/plugin-development/subskills/**`
- `skills/skill-plugin-installation/SKILL.md`
- `skills/skill-writer/SKILL.md`

修改内容：

- 插件开发技能版本更新，明确 Native/Process 发布前必须完成本地调试验证。
- Native 插件脚手架新增 `Debug/{PluginName}.Debug.csproj` 和调试入口。
- 新增 Dotnet 托管插件创建与发布脚本。
- 修正子技能相对路径描述，避免误加 `plugin-development/` 前缀。
- 插件安装技能更新为：插件默认全局安装，已加载插件需先卸载、安装后重载。
- 技能编写文档补充创建技能前必须确认全局/项目作用域。

### 7. 其他调整

涉及文件：

- `Src/Netor.Cortana.AI/AIAgentFactory.cs`
- `Src/Netor.Cortana.Networks/Netor.Cortana.Networks.csproj`
- `Src/Netor.Cortana.Networks/WebSocketPluginBusServerService.cs`
- `Src/Netor.Cortana.Platform/Netor.Cortana.Platform.Api/Netor.Cortana.Platform.Api.sln`
- `Docs/【未解决】智能体创建时机与工具上下文刷新问题讨论.md`
- `Docs/执行计划(模型能力协议简化与插件元数据读取).md`

修改内容：

- `AIAgentFactory` 跳过与主智能体相同的子智能体，避免重复挂载同一智能体。
- `Netor.Cortana.Networks` 引入 AI 项目引用以调用插件模型能力服务。
- 删除旧位置的 `WebSocketPluginBusServerService.cs`，保留 `WebSockets/Servers/` 下实现。
- 记录智能体工具上下文刷新问题，后续倾向通过工具上下文版本号和懒重建解决。
- 新增平台 API 独立解决方案文件。

---

## 兼容性说明

- 现有 WebSocket `send`、`stop`、`token`、`done`、`error`、`connected` 语义保持不变。
- 旧客户端可以忽略未知 `system.notice` 类型。
- 插件模型能力为内部 localhost 通道，不作为公网 API。
- Memory 插件在宿主模型能力不可用时仍可回退 fallback，不影响基本功能。

---

## 验证记录

已执行或记录的验证：

- `dotnet build .\Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj`：构建通过。
- system.notice 代码路径验证：仅显示临时系统提示，不触发 AI 对话。
- Memory 插件与主程序模型能力代码构建通过。

尚未完成的端到端验证：

- 在真实运行环境中验证 Memory 插件通过授权模型完成一次宿主大模型调用。
- 验证未授权或模型不可用时 Memory 插件完整降级到 fallback。

---

## 提交说明

建议提交信息：

```text
feat(plugin): add model capability authorization and system notices
```

建议提交描述：

```text
- add system.notice protocol and temporary notice cards
- add internal model-capability websocket endpoint for plugins
- add plugin authorization settings page for llm access
- wire Memory plugin to host model capability with fallback
- update plugin development, installation and websocket skills
- document unresolved agent tool context refresh strategy
```

