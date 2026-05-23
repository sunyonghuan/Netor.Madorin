# Git 提交记录 - AI 工具链协议修复与调试日志治理

## 提交信息

**提交日期**: 2026-05-06
**提交类型**: Fix / Feature / Docs / Release
**影响范围**: AI 工具调用协议、聊天历史持久化、AI 调试日志、历史显示、插件版本、技能安装脚本

---

## 修改概述

本次提交围绕 OpenAI-compatible 工具调用链路的稳定性进行修复，并补齐可控的 AI 请求追踪能力：

1. 修复聊天历史重排时同一条 `tool` 消息包含多个 tool result 被重复追加的问题，避免 DeepSeek/OpenAI 兼容接口返回 `Messages with role 'tool' must be a response to a preceding message with 'tool_calls'`。
2. 强化工具调用内容的结构化持久化与恢复，确保 `FunctionCallContent` / `FunctionResultContent` / MCP tool content 能完整进入历史上下文。
3. 新增可配置的 AI 全量调试日志，按设置或环境变量输出请求、响应、流式片段、协议诊断和异常信息，并对敏感 Header 做脱敏。
4. 优化聊天窗口历史显示，隐藏工具调用链条和 reasoning，仅展示用户可读文本。
5. 更新应用版本到 `1.3.3`，同步提升内置插件版本号。
6. 清理临时诊断代码，仅保留正式追踪和协议诊断能力。

---

## 修改文件清单

### 1. AI 工具调用协议与历史重排修复

- `Src/Netor.Madorin.AI/Providers/ChatHistoryDataProvider.cs`
- `Src/Netor.Madorin.AI/Extensions/ChatMessageExtensions.cs`

**修改内容**:

- `ReorderForToolCallProtocol(...)` 改为支持 `FunctionCallContent`、`ToolCallContent`、`McpServerToolCallContent` 以及对应 result content。
- 修复一条 `tool` 消息包含多个 callId 时，被按 callId 重复追加到上下文的问题。
- 对 assistant tool_calls 的重排改为先收集去重后的 tool message index，再追加 tool 消息。
- 缺失 tool result 或空 callId 的 assistant tool_calls 会降级为普通 assistant 文本，避免继续发送非法协议消息。
- 持久化文本快照改为从结构化 contents 构建，不再只依赖 `message.Text`。
- `FunctionCallContent.ArgumentsJson` 失败或缺失时回退为 `{}`，避免 OpenAI 兼容接口收到 null arguments。
- 恢复 `functionCall` 内容时使用空字典兜底，降低历史反序列化失败风险。

**影响功能**: 历史上下文中的工具调用链条更稳定，修复多工具调用合并结果场景下的 400 错误。

### 2. AI 全量调试日志与协议诊断

- `Src/Netor.Madorin.AI/Providers/TokenTrackingChatClient.cs`
- `Src/Netor.Madorin.AI/Drivers/UserAgentOverrideHandler.cs`
- `Src/Netor.Madorin.AI/AIAgentFactory.cs`
- `Src/Netor.Madorin.UI/App.axaml.cs`
- `Src/Netor.Madorin.UI/Views/Settings/SystemSettingsPage.axaml.cs`
- `Src/Netor.Madorin.Entitys/Services/SystemSettingsService.cs`

**修改内容**:

- `TokenTrackingChatClient` 新增 AI trace 输出：
  - `request`
  - `response`
  - `stream-request`
  - `stream-response`
  - `stream-error`
- trace 中记录消息结构、stream update、usage、异常堆栈和协议诊断信息。
- 新增 `AnalyzeMessages(...)`，检测 orphan tool、missing tool response、non-adjacent tool message 等协议问题。
- `AIAgentFactory` 构造 `TokenTrackingChatClient` 时传入 `IAppPaths`，用于把 trace 写入工作区 `.madorin/logs/ai-traces`。
- `UserAgentOverrideHandler` 改为仅在 `AI.Trace.Enabled` 或 `MADORIN_AI_TRACE_ENABLED` 开启时记录 HTTP 请求/响应原文。
- HTTP Header 中 `Authorization: Bearer ...` 输出前脱敏。
- 读取响应体后重新包装 `StringContent`，避免 trace 消耗 response body 导致下游无法读取。
- 系统设置新增 `AI.Trace.Enabled`，设置页可见。
- `SystemSettingsService` 新增 `IsAiTraceEnabled(...)` 辅助方法。

**影响功能**: AI 请求可在需要时完整追踪，发布版默认关闭，排障时可通过设置或环境变量启用。

### 3. 聊天历史显示与工具内部消息过滤

- `Src/Netor.Madorin.UI/Views/Main/MainWindow.Sessions.cs`
- `Src/Netor.Madorin.Entitys/Services/ChatMessageService.cs`

**修改内容**:

- 聊天气泡显示内容优先从 `ContentsJson` 中提取 `TextContent`。
- `TextReasoningContent` 保留在结构化历史中，但默认不渲染到聊天气泡。
- 工具调用占位 assistant 消息和 `role=tool` 消息不进入前端历史显示。
- 对历史文本中的 `[工具调用]`、`[工具结果]` 块做清理，避免内部工具链细节污染用户聊天窗口。
- `ChatMessageService` 改为 partial，并使用 `GeneratedRegex` 实现工具块清理。

**影响功能**: 历史记录更接近用户真实对话内容，工具执行链条仍保留给 AI 上下文，但不直接展示给用户。

### 4. 日志目录、应用名称和 UI 调整

- `Src/Netor.Madorin.UI/App.axaml.cs`
- `Src/Netor.Madorin.UI/Views/Proxy/ProxyWindow.axaml`
- `Src/Netor.Madorin.UI/Netor.Madorin.UI.csproj`

**修改内容**:

- 应用版本从 `1.3.2` 升级到 `1.3.3`。
- 新增 `App.AppName`，托盘提示改为使用统一应用名称。
- 默认日志目录调整为工作区 `logs`。
- 文件日志滚动粒度调整为分钟级，最低级别调整为 Information 配置入口。
- AI 代理窗口高度从 `500` 调整为 `520`。

### 5. 内置插件版本更新

- `Plugins/Src/Madorin.Plugins.Bt/Madorin.Plugins.Bt.csproj`
- `Plugins/Src/Madorin.Plugins.Bt/Startup.cs`
- `Plugins/Src/Madorin.Plugins.GoogleSearch/Madorin.Plugins.GoogleSearch.csproj`
- `Plugins/Src/Madorin.Plugins.GoogleSearch/Startup.cs`
- `Plugins/Src/Madorin.Plugins.Memory/Madorin.Plugins.Memory.csproj`
- `Plugins/Src/Madorin.Plugins.Memory/Startup.cs`
- `Plugins/Src/Madorin.Plugins.Office/Madorin.Plugins.Office.csproj`
- `Plugins/Src/Madorin.Plugins.Office/Startup.cs`
- `Plugins/Src/Madorin.Plugins.Reminder/Madorin.Plugins.Reminder.csproj`
- `Plugins/Src/Madorin.Plugins.Reminder/Startup.cs`
- `Plugins/Src/Madorin.Plugins.ScriptRunner/Madorin.Plugins.ScriptRunner.csproj`
- `Plugins/Src/Madorin.Plugins.WsBridge/Madorin.Plugins.WsBridge.csproj`
- `Plugins/Src/Madorin.Plugins.WsBridge/Startup.cs`

**版本变化**:

- 宝塔面板插件：`1.0.16` → `1.0.17`
- 谷歌搜索插件：`1.0.15` → `1.0.16`
- Memory Engine：`1.0.4` → `1.0.5`
- 办公文档插件：`1.0.15` → `1.0.16`
- 定时提醒插件：`1.0.19` → `1.0.20`
- C# Script Runner：`1.0.14` → `1.0.15`
- WebSocket 中转插件：`1.0.14` → `1.0.15`

### 6. 技能安装脚本与文档调整

- `skills/skill-plugin-installation/SKILL.md`
- `skills/skill-plugin-installation/scripts/install-package.ps1`

**修改内容**:

- 技能安装说明改为更明确的 zip 安装、目录选择、解压和结构校验流程。
- 安装脚本输出编码改为 UTF-8。
- skill 包结构校验统一要求根目录存在 `skill.md`。
- 错误提示同步从 `SKILL.md 或 skill.md` 收敛为 `skill.md`。

---

## 验证记录

- [x] 使用实际 DeepSeek 请求上下文定位 `role=tool` 非法重复问题。
- [x] 通过 SQLite 查询确认数据库原始历史无重复 tool result。
- [x] 通过临时诊断确认重复发生在历史重排输出阶段。
- [x] 修复后删除临时 `[ToolProtocolProbe]` 诊断代码。
- [x] 搜索确认 `ToolProtocolProbe` 已无残留。
- [x] 本次清理未引入新的文件级诊断；剩余 nullable / 不可达代码诊断为既有问题。

---

## 提交范围说明

本次建议提交：

- AI 协议修复相关源码
- AI trace 相关源码与设置项
- 历史显示过滤相关源码
- 应用与插件版本号更新
- 技能安装说明和脚本调整
- 本提交说明文档
- `Docs/release-notes/v1.3.3/RELEASE.md`

本次不建议提交：

- `skills/skill-plugin-installation/.backup/` 下的备份文件
- 临时数据库查询脚本
- 构建输出、日志、发布产物

---

## 提交说明

建议提交信息：

`fix(ai): repair tool history protocol and add trace diagnostics`

建议提交描述：

- fix duplicated tool messages when one persisted tool result contains multiple call ids
- preserve structured tool call/result contents across chat history persistence
- add configurable AI trace logs with protocol diagnostics and header redaction
- hide tool/reasoning internals from chat history display
- bump app and bundled plugin versions
- update skill package installation guidance

---

## Git 命令

```bash
git add Src/Netor.Madorin.AI
git add Src/Netor.Madorin.UI
git add Src/Netor.Madorin.Entitys
git add Plugins/Src/Madorin.Plugins.Bt
git add Plugins/Src/Madorin.Plugins.GoogleSearch
git add Plugins/Src/Madorin.Plugins.Memory
git add Plugins/Src/Madorin.Plugins.Office
git add Plugins/Src/Madorin.Plugins.Reminder
git add Plugins/Src/Madorin.Plugins.ScriptRunner
git add Plugins/Src/Madorin.Plugins.WsBridge
git add skills/skill-plugin-installation/SKILL.md
git add skills/skill-plugin-installation/scripts/install-package.ps1
git add Docs/GIT提交说明/GIT-修改-AI工具链协议修复与调试日志治理.md
git add Docs/release-notes/v1.3.3/RELEASE.md

git commit -m "fix(ai): repair tool history protocol and add trace diagnostics"
```

---

**提交人**: GitHub Copilot
**审核人**: TBD
