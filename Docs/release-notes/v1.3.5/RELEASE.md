# Madorin v1.3.5 Release Notes

发布日期：2026-05-07

## 概览

v1.3.5 是一次面向插件体系、模型代理与工具体验的综合增强版本。

本版本重点引入 **全局插件管理**，让插件可以从用户全局目录统一加载，并按需设为所有智能体可用；同时将“工具管理”升级为“插件管理”，补齐 MCP 服务展示、重连和智能体绑定能力。

模型代理方面，v1.3.5 补齐 DeepSeek reasoner 在 OpenAI-compatible 代理场景下的 `reasoning_content` 回传能力，解决外部客户端不回传思维内容时可能触发的连续请求或工具调用协议问题。

一句话：**本版本让插件更好管、工具名更干净、DeepSeek 代理更稳。**

## 核心亮点

### 1. 全局插件能力

新增全局插件支持，插件不再必须绑定到某一个智能体才能使用。

全局插件启用后，可被所有智能体注入和调用，适合以下类型插件：

- 文件与项目设置类工具
- 搜索、办公、提醒等通用工具
- 应用启动与窗口管理工具
- 团队或个人长期使用的基础插件

AI 工具装配流程同步调整为：

1. 先注入全局插件；
2. 再注入当前智能体绑定插件；
3. 通过插件 ID 去重，避免同一插件重复注入。

### 2. 插件管理页重构

原“工具管理”升级为“插件管理”，插件列表不再依赖当前智能体选择。

插件管理页现在支持：

- 查看插件名称、简介、版本、ID、目录和工具数量；
- 按插件或 MCP 服务搜索；
- 启用 / 关闭全局插件；
- 给当前智能体绑定或解绑插件；
- 卸载、重载、删除插件；
- 打开插件目录；
- 查看 MCP 服务和工具列表；
- MCP 服务手动重连与智能体绑定。

### 3. 插件统一从用户全局目录加载

工作区插件加载路径已收口，插件统一从用户全局插件目录扫描。

这降低了插件来源复杂度，避免工作区切换时插件状态不一致，也让“插件安装一次、多工作区复用”成为默认行为。

兼容说明：

- 旧工作区插件目录接口保留废弃提示；
- 工作区切换不再创建 `.madorin/plugins`；
- 如仍有旧插件放在工作区目录，需要迁移到用户全局插件目录。

### 4. 工具名 shortName 治理

Native / Process 插件协议新增 `shortName` 支持。

宿主现在优先向 AI 暴露短工具名，内部调用仍保留完整工具名映射，从而避免工具列表出现冗长、重复的插件 ID 前缀。

示例：

```text
旧：office_word_create_document
新：word_create_document

旧：bt_bt_get_sites
新：get_sites
```

这让模型看到的工具更短、更清晰，也减少函数调用时由于命名噪声导致的选择错误。

### 5. DeepSeek 代理 reasoning 回放

OpenAI-compatible 代理新增 DeepSeek reasoner 专用 reasoning 回放能力。

当外部客户端没有在下一轮请求中回传 `reasoning_content` 时，Madorin 会在代理侧缓存 DeepSeek 响应中的 reasoning，并在后续 DeepSeek 请求中按协议补写到 assistant 消息里。

该能力只在 DeepSeek provider 下启用，不改变 OpenAI、Azure OpenAI、GLM、Custom 等普通兼容通道的请求结构。

## 新能力与改进

### 插件体系

- 新增 `GlobalPlugins` 数据表与全局插件服务。
- 新增插件安装范围展示，区分用户目录等插件来源。
- 插件加载器只扫描用户全局插件目录。
- 插件管理页支持插件全局开关和智能体绑定即时保存。
- MCP 服务在插件管理页中可展示、搜索、重连和绑定。
- 插件 SDK / 生成器输出 `shortName`。
- Native 生成器兼容新旧命名空间。
- Process 插件工具名生成避免重复拼接插件 ID 前缀。

### 内置能力插件化

应用启动与窗口管理能力从主程序内置 Provider 迁移为独立 Native 插件：

- `application_launcher`
- `window_management`

这让主程序内置工具继续收口，通用能力逐步转向插件分发和管理。

### 内置插件升级

多个内置 Native 插件升级到新版插件 SDK，并同步清理工具名前缀：

- 宝塔面板插件
- 谷歌搜索插件
- Memory Engine
- 办公文档插件
- 定时提醒插件
- C# Script Runner
- WebSocket 中转插件
- 应用启动插件
- 窗口管理插件

Office 工具名统一改为 `word_*`、`excel_*`、`ppt_*`；Bt 插件也移除重复 `bt_` 前缀。

### 项目设置工具替换文件记忆工具

`FileMemoryProvider` 已替换为 `ProjectSettingsProvider`。

新的项目设置文件优先路径为：

```text
.madorin/project-settings.md
```

旧路径继续兼容：

```text
.madorin/memory.md
```

新增工具：

- `sys_read_settings`
- `sys_write_settings`
- `sys_edit_settings`
- `sys_delete_settings`
- `sys_clear_settings`

旧 `sys_*_memory` 工具名保留为兼容入口。

### DeepSeek 代理兼容性

新增 DeepSeek 专用内存缓存：

- 缓存 Key 使用 provider、模型和客户端维度隔离；
- 每个 Key 最多保留 32 条 reasoning；
- 默认 2 小时过期；
- 支持从非流式 JSON 响应提取 reasoning；
- 支持从 SSE 流式响应累积提取 reasoning；
- 请求前为 DeepSeek assistant 消息补写 `reasoning_content`。

客户端隔离优先读取：

- `X-Madorin-Session-Id`
- `X-Madorin-Conversation-Id`
- `X-Request-Id`
- 远端地址

### UI 与工作区体验

- 工作区文件浏览器标题改为显示当前工作区名称。
- 工作区变化时自动更新标题和文件树。
- 优化历史面板复选框查找逻辑，适配多种控件层级。
- 更新部分 UI 资源图片。

### 文档与方案

- 新增全局插件工具功能方案。
- 新增插件工具命名优化执行计划。
- 更新语音服务独立进程拆分方案，将 KWS / STT / TTS 统一规划为 Process 插件化。
- 更新 README 中的项目定位、插件体系、模型代理、WebSocket、发布流程和目录结构。
- 清理历史诊断脚本和旧发布脚本，收敛构建目录。

## 版本更新

### 主程序

- Madorin：`1.3.3` → `1.3.5`

### 内置插件

- 宝塔面板插件：`1.0.17` → `1.0.21`
- 谷歌搜索插件：`1.0.16` → `1.0.20`
- Memory Engine：`1.0.5` → `1.0.9`
- 办公文档插件：`1.0.16` → `1.0.20`
- 定时提醒插件：`1.0.20` → `1.0.24`
- C# Script Runner：`1.0.15` → `1.0.19`
- WebSocket 中转插件：`1.0.15` → `1.0.19`
- 应用启动插件：新增 `1.0.4`
- 窗口管理插件：新增 `1.0.4`

### 插件 SDK

- `Netor.Madorin.Plugin.Native` / 生成器：升级到 `1.0.36` 系列

## 不兼容变更与迁移提示

### 工作区插件目录不再作为默认加载来源

插件统一从用户全局插件目录加载。若此前插件放在 `.madorin/plugins`，需要迁移到用户插件目录后再在插件管理页中重载。

### 工具名更短

部分插件工具名已移除插件 ID 前缀。历史对话中的旧工具名可能仍可通过兼容入口处理，但新对话建议使用新短名。

### 文件记忆工具语义升级为项目设置

旧 `sys_*_memory` 入口保留兼容，但建议新流程改用 `sys_*_settings` 工具名。

## 升级建议

1. 更新到 v1.3.5 后，打开“插件管理”页检查全局插件状态。
2. 将旧工作区插件迁移到用户全局插件目录。
3. 对常用基础插件启用“全局插件”，避免每个智能体重复绑定。
4. 对需要隔离的插件继续使用智能体绑定，不要启用全局。
5. 如使用 DeepSeek reasoner 作为本地代理上游，建议验证一次连续对话与工具调用流程。
6. 如依赖旧工具名，可逐步迁移到新短名。

## 验证记录

已执行并通过：

```text
dotnet build .\Src\Netor.Madorin.UI\Netor.Madorin.UI.csproj -p:OutDir=.\artifacts\build-verify\ui\
```

```text
.\Plugins\publish.cmd
```

```text
dotnet build .\Plugins\Src\Madorin.Plugins.Memory\Madorin.Plugins.Memory.csproj
```

DeepSeek reasoning 回放测试：

```text
dotnet test Tests\Netor.Madorin.Networks.Tests\Netor.Madorin.Networks.Tests.csproj
```

测试结果：9 个测试全部通过。

## 文件参考

- 主程序版本：`Src/Netor.Madorin.UI/Netor.Madorin.UI.csproj`
- 全局插件实体：`Src/Netor.Madorin.Entitys/Entities/GlobalPluginEntity.cs`
- 全局插件服务：`Src/Netor.Madorin.Entitys/Services/GlobalPluginService.cs`
- 插件注入：`Src/Netor.Madorin.AI/AIAgentFactory.cs`
- 插件加载：`Src/Netor.Madorin.Plugin/PluginLoader.cs`
- 插件管理页：`Src/Netor.Madorin.UI/Views/Settings/PluginManagementPage.axaml`
- Native 插件 shortName：`Src/Netor.Madorin.Plugin/Native/NativePluginInfo.cs`
- Process 插件协议：`Src/Plugins/Netor.Madorin.Plugin.Process/Protocol/PluginInfoData.cs`
- DeepSeek reasoning 缓存：`Src/Netor.Madorin.Networks/Proxy/DeepSeekReasoningReplayCache.cs`
- DeepSeek 请求重写：`Src/Netor.Madorin.Networks/Proxy/DeepSeekReasoningRequestRewriter.cs`
- OpenAI 兼容代理：`Src/Netor.Madorin.Networks/Proxy/OpenAiCompatibleRawProxy.cs`
- 项目设置工具：`Src/Netor.Madorin.AI/Providers/ProjectSettingsProvider.cs`
- 应用启动插件：`Plugins/Src/Madorin.Plugins.ApplicationLauncher`
- 窗口管理插件：`Plugins/Src/Madorin.Plugins.WindowManagement`
