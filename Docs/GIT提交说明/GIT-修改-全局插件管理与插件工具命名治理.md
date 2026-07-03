# Git 提交记录 - 全局插件管理与插件工具命名治理

## 提交信息

**提交日期**: 2026-05-07  
**提交类型**: Feature / Refactor / Docs / Plugin  
**影响范围**: 插件管理、全局插件、MCP 展示、插件 SDK 生成器、内置工具插件化、插件工具命名、项目设置、语音插件化方案文档

---

## 修改概述

本次提交围绕插件体系进行集中治理：

1. 新增“全局插件”能力，支持全局目录插件无需绑定智能体即可被所有智能体使用。
2. 将原“工具管理”升级为“插件管理”，插件列表不再依赖智能体选择，并补充 MCP 服务展示、重连和智能体绑定。
3. 移除工作区插件加载路径，插件统一从用户全局插件目录加载。
4. 调整 AI 工具装配逻辑，先注入全局插件，再注入智能体绑定插件，并避免重复注入。
5. 为 Native / Process 插件协议补充 `shortName`，宿主优先暴露短工具名，避免插件 ID 前缀污染工具列表。
6. 升级多个插件到新版 `Netor.Cortana.Plugin.Native`，修复旧生成器导致的工具名前缀与导出入口问题。
7. 将内置应用启动、窗口管理能力迁移为独立 Native 插件，主程序内置 Provider 收口。
8. 批量清理 Bt、Office 等插件工具名前缀，统一改为插件内短名。
9. 将项目记忆 Provider 改造为项目设置 Provider，并保留旧工具名兼容。
10. 更新语音服务拆分文档，将方案调整为 KWS / STT / TTS Process 插件化。
11. 清理历史诊断脚本和旧发布脚本，收敛构建目录。

---

## 主要修改内容

### 1. 全局插件与插件管理页

涉及文件：

- `Src/Netor.Cortana.Entitys/CortanaDbContext.cs`
- `Src/Netor.Cortana.Entitys/Entities/GlobalPluginEntity.cs`
- `Src/Netor.Cortana.Entitys/Services/GlobalPluginService.cs`
- `Src/Netor.Cortana.AI/AIAgentFactory.cs`
- `Src/Netor.Cortana.Plugin/PluginLoader.cs`
- `Src/Netor.Cortana.UI/Views/Settings/PluginManagementPage.axaml`
- `Src/Netor.Cortana.UI/Views/Settings/PluginManagementPage.axaml.cs`
- `Src/Netor.Cortana.UI/Views/SettingsWindow.axaml`
- `Src/Netor.Cortana.UI/Views/SettingsWindow.axaml.cs`

修改内容：

- 新增 `GlobalPlugins` 表与 `GlobalPluginService`。
- 新增 `LoadedPluginInfo` 与 `PluginInstallScope`，用于插件管理页展示插件目录与来源。
- `AIAgentFactory` 支持全局插件注入，并通过 `injectedPluginIds` 避免重复注入。
- 原 `ToolManagementPage` 删除，替换为 `PluginManagementPage`。
- 插件管理页支持：
  - 插件列表直接展示；
  - 搜索插件 / MCP 服务；
  - 查看插件名称、简介、版本、ID、目录、工具数量；
  - 全局插件开关；
  - 卸载、重载、删除、打开目录；
  - 智能体绑定勾选即保存；
  - MCP 服务展示、工具列表、重连和智能体绑定。

### 2. 工作区插件支持移除

涉及文件：

- `Src/Netor.Cortana.UI/App.axaml.cs`
- `Src/Netor.Cortana.UI/AppPaths.cs`
- `Src/Netor.Cortana.Entitys/Interfaces/IAppPaths.cs`
- `Src/Netor.Cortana.Plugin/PluginLoader.cs`
- `Src/Netor.Cortana.UI/Providers/WindowToolProvider.cs`

修改内容：

- `PluginDirectory` 改为指向 `UserPluginsDirectory`。
- `WorkspacePluginsDirectory` 标记为废弃。
- 工作目录切换不再创建 `.cortana/plugins`。
- `PluginLoader` 只扫描全局插件目录。
- 移除工作区变化触发插件全量重载的订阅。
- `sys_get_workspace_plugins_directory` 改为废弃提示，推荐使用全局插件目录。

### 3. 插件工具命名与 shortName 支持

涉及文件：

- `Src/Netor.Cortana.Plugin/Native/NativePluginInfo.cs`
- `Src/Netor.Cortana.Plugin/Native/NativePluginWrapper.cs`
- `Src/Plugins/Netor.Cortana.Plugin.Native.Generator/Analysis/*`
- `Src/Plugins/Netor.Cortana.Plugin.Native.Generator/Emitters/InfoJsonEmitter.cs`
- `Src/Plugins/Netor.Cortana.Plugin.Process.Generator/Analysis/ToolNameGenerator.cs`
- `Src/Plugins/Netor.Cortana.Plugin.Process.Generator/Emitters/ProgramEmitter.cs`
- `Src/Plugins/Netor.Cortana.Plugin.Process/Protocol/PluginInfoData.cs`

修改内容：

- Native / Process 生成器输出 `shortName`。
- 宿主优先使用 `ShortName` 暴露工具名，内部调用仍使用完整工具名。
- 工具名生成器避免重复拼接插件 ID 前缀。
- Native 生成器同时兼容新旧命名空间：
  - `Netor.Cortana.Plugin.Native.*`
  - `Netor.Cortana.Plugin.*`
- 插件 SDK 版本提升到 `1.0.36`。

### 4. 插件升级与工具名前缀清理

涉及插件：

- `Cortana.Plugins.Bt`
- `Cortana.Plugins.GoogleSearch`
- `Cortana.Plugins.Memory`
- `Cortana.Plugins.Office`
- `Cortana.Plugins.Reminder`
- `Cortana.Plugins.ScriptRunner`
- `Cortana.Plugins.WsBridge`

修改内容：

- 多个 Native 插件升级到 `Netor.Cortana.Plugin.Native` `1.0.34`。
- Memory 插件从旧命名空间切换到 `Netor.Cortana.Plugin.Native`，修复 `#sym:memory_engine` / 插件 ID 前缀问题。
- Office 插件工具名移除 `office_` 前缀，改为：
  - `word_*`
  - `excel_*`
  - `ppt_*`
- Bt 插件工具名移除 `bt_` 前缀，改为插件内短名。
- 同步更新插件版本号与启动说明。

### 5. 内置应用启动与窗口管理迁移为插件

新增文件：

- `Plugins/Src/Cortana.Plugins.ApplicationLauncher/*`
- `Plugins/Src/Cortana.Plugins.WindowManagement/*`

删除文件：

- `Src/Netor.Cortana.Plugin/BuiltIn/ApplicationLauncher/*`
- `Src/Netor.Cortana.Plugin/BuiltIn/WindowManagement/*`

修改内容：

- 应用启动能力迁移为 `application_launcher` Native 插件。
- Windows 窗口管理能力迁移为 `window_management` Native 插件。
- `PluginServiceExtensions` 移除对应内置 Provider 和辅助服务注册。
- `Plugins/Cortana.Plugins.slnx` 加入新插件工程。

### 6. 项目设置 Provider 替换文件记忆 Provider

涉及文件：

- `Src/Netor.Cortana.AI/AIServiceExtensions.cs`
- `Src/Netor.Cortana.AI/Providers/ProjectSettingsProvider.cs`
- 删除 `Src/Netor.Cortana.AI/Providers/FileMemoryProvider.cs`

修改内容：

- `FileMemoryProvider` 替换为 `ProjectSettingsProvider`。
- 新增项目设置文件优先路径：`.cortana/project-settings.md`。
- 兼容旧 `.cortana/memory.md`。
- 新增 `sys_read_settings` / `sys_write_settings` / `sys_edit_settings` / `sys_delete_settings` / `sys_clear_settings`。
- 保留旧 `sys_*_memory` 工具名，作为兼容入口。

### 7. UI 与工作区细节调整

涉及文件：

- `Src/Netor.Cortana.UI/Controls/WorkspaceExplorer.axaml`
- `Src/Netor.Cortana.UI/Controls/WorkspaceExplorer.axaml.cs`
- `Src/Netor.Cortana.UI/Controls/ChatHistoryPanel.axaml.cs`
- `Src/Netor.Cortana.UI/Assets/jl.png`
- `Src/Netor.Cortana.UI/Assets/mk.png`

修改内容：

- 工作区文件浏览器标题改为显示当前工作区名称。
- 订阅工作区变化事件，自动更新工作区标题和文件树。
- 优化历史面板中复选框查找逻辑，适配 Grid / StackPanel / CheckBox 多种结构。
- 更新/新增部分 UI 资源图片。

### 8. 文档更新

新增文档：

- `Docs/全局插件工具功能方案.md`
- `Docs/执行计划(插件工具命名优化).md`
- `Docs/GIT提交说明/GIT-修改-全局插件管理与插件工具命名治理.md`

更新文档：

- `README.md`
- `Docs/语音服务独立进程拆分/README.md`
- `Docs/语音服务独立进程拆分/01-架构设计.md`
- `Docs/语音服务独立进程拆分/03-通信协议.md`
- `Docs/语音服务独立进程拆分/04-设置项设计.md`
- `Docs/语音服务独立进程拆分/05-执行计划.md`
- `Docs/语音服务独立进程拆分/06-优缺点与风险.md`

修改内容：

- 全局插件功能方案记录设计、执行计划和进度。
- 插件工具命名优化方案记录生成器、Bt 工具名、SDK 发布和验证结果。
- 语音服务拆分方案从固定 STT/TTS 子进程调整为 KWS/STT/TTS 全部 Process 插件化。
- 根 README 更新项目定位、插件体系、模型代理、WebSocket、发布流程和目录结构。

### 9. 构建脚本与临时诊断脚本清理

涉及文件：

- 删除多个 `Build/inspect_*`、`query.ps1`、历史发布脚本和临时 session dump。
- 更新 `Build/ui.publish.ps1`。

修改内容：

- 清理历史临时诊断脚本。
- 收敛发布入口，减少旧链路干扰。

---

## 验证记录

已执行并通过：

```powershell
dotnet build .\Src\Netor.Cortana.UI\Netor.Cortana.UI.csproj -p:OutDir=.\artifacts\build-verify\ui\
```

构建结果：成功。

插件发布命令最近执行结果：

```powershell
.\Plugins\publish.cmd
```

执行结果：成功。

Memory 插件单独构建：

```powershell
dotnet build .\Plugins\Src\Cortana.Plugins.Memory\Cortana.Plugins.Memory.csproj
```

执行结果：成功。

---

## 提交范围说明

本次提交包含：

- 全局插件功能源码；
- 插件管理页重构源码；
- MCP 服务在插件管理页展示与绑定；
- 插件 SDK / 生成器 shortName 支持；
- Native / Process 插件协议类型调整；
- 插件工具名前缀清理；
- 应用启动、窗口管理插件迁移；
- 项目设置 Provider；
- 语音插件化方案文档；
- README 更新；
- 历史诊断脚本清理。

---

## 建议提交信息

```text
feat(plugin): add global plugin management and short tool names
```

建议提交描述：

- add global plugin persistence and inject global plugins for all agents
- replace tool management page with plugin management page
- show MCP services in plugin management and support agent binding
- load plugins only from the global user plugin directory
- add shortName support for native/process plugin tools
- upgrade bundled plugins to newer native SDK and clean tool prefixes
- move application launcher and window management to native plugins
- replace file memory provider with project settings provider
- update voice pluginization design docs and root README
