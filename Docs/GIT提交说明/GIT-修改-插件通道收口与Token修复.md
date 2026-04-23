# Git 提交记录 - 插件通道收口与 Token 修复

## 提交信息

**提交日期**: 2026-04-23  
**提交类型**: Refactoring / Feature / Docs  
**影响范围**: 插件体系收口、Process 插件脚手架、Token 进度条修复、工具边界约束

---

## 修改概述

本次提交主要完成四类收口工作：

1. 将旧 Dotnet / Abstractions 插件通道整体退场，运行时与示例、文档、脚本统一切换到 Native + Process + MCP 三通道口径。
2. 为 C# Process 插件补齐正式框架路径，新增脚手架与发布脚本，技能链改为默认教会 AI 使用 `Netor.Cortana.Plugin.Process`，不再手写协议层。
3. 修复 AI 会话中 Token 进度条被后台 LLM 调用污染、切会话不清零、流式统计跳变的问题。
4. 收紧工作目录相关工具的边界说明，明确只有在用户明确同意后才能切换工作区或越界访问文件。

---

## 修改文件清单

### 1. 插件体系收口与 Abstractions 退场

- `Netor.Cortana.slnx`
- `Src/Netor.Cortana.Plugin/PluginLoader.cs`
- `Src/Netor.Cortana.Plugin/Netor.Cortana.Plugin.csproj`
- `Src/Netor.Cortana.Plugin/Core/IPlugin.cs`
- `Src/Netor.Cortana.Plugin/Core/IPluginContext.cs`
- `Src/Plugins/Netor.Cortana.Plugin.Abstractions/`（整项目删除）
- `Src/Netor.Cortana.Plugin/Dotnet/`（旧宿主实现删除）
- `Samples/NativeTestPlugin/NativeTestPlugin.csproj`
- `Samples/ReminderPlugin/ReminderPlugin.csproj`
- `Src/Plugins/Netor.Cortana.Plugin.Native.Debuger/Netor.Cortana.Plugin.Native.Debugger.csproj`
- `Src/Plugins/Netor.Cortana.Plugin.Native.Debuger/PluginDebugRunner.cs`

**修改内容**:

- 移除 `Netor.Cortana.Plugin.Abstractions` 项目及旧 Dotnet 插件宿主实现。
- 将 `IPlugin`、`IPluginContext` 收回宿主侧核心目录。
- Native / Process 框架所需 Attribute 与 `PluginSettings` 分别内聚到各自运行时包。
- 示例插件改为直接引用当前仓库内的 Native 运行时与 Generator，并显式导入 Generator targets。
- 运行时对 `dotnet` manifest 改为识别后跳过，不再尝试加载历史托管插件。

**影响功能**: 新插件开发与运行时主线彻底统一为 Native / Process / MCP，旧 Dotnet 通道只剩历史识别，不再参与构建与分发。

### 2. Process 插件框架、技能与脚手架补全

- `skills/plugin-development/SKILL.md`
- `skills/plugin-development/subskills/process/SKILL.md`
- `skills/plugin-development/subskills/process/resources/csharp-process-plugin.md`
- `skills/plugin-development/scripts/PluginDev.Common.ps1`
- `skills/plugin-development/scripts/create-process-plugin.ps1`
- `skills/plugin-development/scripts/publish-process-plugin.ps1`
- `.github/skills/plugin-development/SKILL.md`
- `.github/skills/plugin-development/scripts/create-process-plugin.ps1`
- `.github/skills/plugin-development/scripts/publish-process-plugin.ps1`
- `Plugins/.cortana/skills/plugin-development/SKILL.md`
- `Src/Plugins/Netor.Cortana.Plugin.Process/README.md`
- `Src/Plugins/Netor.Cortana.Plugin.Process.Generator/README.md`

**修改内容**:

- 主技能和子技能改写为 Process 框架优先路径，并补齐 `version` 字段，便于后续技能增量维护。
- 新增 root 级 `create-process-plugin.ps1` 与 `publish-process-plugin.ps1`，支持 JIT self-contained、framework-dependent 和 AOT 三种发布模式。
- 修复共享脚本的仓库根目录解析逻辑，避免脚手架错误生成到 `skills/Samples`。
- `.github` 下旧 Process 模板目录与手写协议入口全部移除，改为代理 root 脚本。
- README 明确：消费方只需引用 `Netor.Cortana.Plugin.Process` 一个包，Generator 会随包自动带上。

**影响功能**: AI 在 C# Process 插件场景下会默认生成框架化工程、自动产出 `plugin.json` 与 `StartupDebugger`，不再回退到手写协议胶水代码。

### 3. Token 进度条与会话状态修复

- `Src/Netor.Cortana.AI/Providers/TokenTrackingChatClient.cs`
- `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs`
- `Src/Netor.Cortana.AI/AiChatHostedService.cs`
- `Docs/执行计划(Token进度条修复).md`

**修改内容**:

- 在 `TokenTrackingChatClient` 增加 `SuppressUsage()`，屏蔽标题生成、摘要压缩等后台 LLM 调用对主对话 Token 进度条的污染。
- 流式响应的 usage 改为流内累积、流尾统一提交，避免进度条抖动和跳变。
- 新建会话、恢复会话、切换 Provider/Model/Agent 时统一调用 `ResetTokenStats()` 清空旧状态。
- 会话 `TotalTokenCount` 的累加改为仅在真实对话持久化路径启用，避免重复统计。

**影响功能**: UI 中的 Token 进度条会和真实主对话上下文保持一致，不再被摘要、标题生成或旧会话残留状态干扰。

### 4. 工作区边界与工具说明收紧

- `Src/Netor.Cortana.AvaloniaUI/Providers/WindowToolProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileBrowserProvider.cs`
- `Src/Netor.Cortana.Plugin/BuiltIn/FileBrowser/FileOperationProvider.cs`

**修改内容**:

- 工作区切换工具描述改为必须在用户明确同意后才能调用。
- 文件浏览和文件操作工具的系统指令统一为英文，并明确限定在当前 workspace boundary 内使用。
- 要求越界访问前必须先取得用户同意并切换工作目录边界，避免 AI 直接跨工作区访问文件。

**影响功能**: 文件与目录相关工具的行为边界更清晰，和当前工作区安全约束保持一致。

---

## 验证记录

- [x] 新 Process 脚手架已用临时样本执行 `dotnet build` 验证通过。
- [x] `publish-process-plugin.ps1 -SkipDeploy` 路径已验证通过。
- [x] 技能链中的旧 `template-process-csharp` / 手写协议提示已完成检索清理。
- [x] 主技能与子技能入口已补齐 `version` 字段。
- [x] Token 修复执行计划已整理到独立文档，便于后续回溯。

---

## 提交范围说明

本次建议提交：源码、文档、技能和脚本变更，以及必要的新接口文件与 README。

本次不建议提交：

- `build_output.txt`
- `smoke.big.txt`
- `smoke.errors.txt`
- `Samples/*/generated/` 下的自动生成代码

---

## 提交说明

建议提交信息：

`refactor(plugin): retire dotnet plugin path and add process framework workflow`

建议提交描述：

- remove legacy dotnet plugin host and abstractions project
- align runtime, docs, samples and skills to native/process/mcp
- add process plugin scaffolding and publish scripts
- fix token usage progress reset and background usage pollution
- tighten workspace boundary rules for file tools

---

## Git / TFS Git 命令

```bash
# 先只提交源码/文档/脚本，排除日志与 generated
git add -u
git add Docs/GIT-修改-插件通道收口与Token修复.md
git add Docs/执行计划\(Token进度条修复\).md
git add skills/plugin-development
git add .github/skills/plugin-development
git add Plugins/.cortana/skills/plugin-development
git add README.md Netor.Cortana.slnx plugin.publish.ps1
git add Samples/NativeTestPlugin/NativeTestPlugin.csproj
git add Samples/ReminderPlugin/ReminderPlugin.csproj
git add Samples/ReminderPlugin/.github/skills/plugin-development
git add Src/Netor.Cortana.AI
git add Src/Netor.Cortana.AvaloniaUI/Providers/WindowToolProvider.cs
git add Src/Netor.Cortana.Plugin
git add Src/Plugins

git commit -m "refactor(plugin): retire dotnet plugin path and add process framework workflow"

# 提交到 GitHub
git push github master

# 提交到 TFS Git 远端
git push netor master
```

说明：当前机器没有 `tf.exe`，无法执行 TFVC `checkin`。本仓库实际存在 TFS Git 远端 `netor`，因此这里的“TFS 提交”按 Git 方式推送到 TFS 服务端仓库。

---

**提交人**: GitHub Copilot  
**审核人**: TBD