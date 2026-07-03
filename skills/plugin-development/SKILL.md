---
name: plugin-development
description: 'Madorin 插件开发入口技能。用于统一编排 Native、MCP、Process 三类通道，约束工程规范、插件元数据协议、宿主能力申请、设置界面 schema、外部包 AOT 校验、发布安装与运行时更新流程。触发关键词：插件开发、Native 插件、MCP 插件、Process 插件、进程插件、发布插件、安装插件。'
version: 8
user-invocable: true
---

# Plugin Development

## Structure

- 主技能目录：plugin-development
- 子技能目录：subskills
- 共享脚本目录：scripts
- 子技能可有独立 scripts 和 resources；需要复用时由子技能脚本包装共享脚本

## Entry Flow

1. 先判断插件通道：Native（AOT DLL）、Process（任意 exe）、MCP（远程服务）。
2. 读取 `subskills/architecture/SKILL.md`。
3. Native 或 Process 需要声明插件协议、宿主权限申请或设置界面时，同时读取 `subskills/architecture/resources/plugin-metadata-protocol.md`。
4. 需要外部包时读取 `subskills/packages/SKILL.md`。
5. 按通道读取对应子技能：
   - Native → `subskills/native/SKILL.md`
   - Process → `subskills/process/SKILL.md`
   - MCP → `subskills/mcp/SKILL.md`
6. Native 或 Process 必须先完成本地调试验证，再进入发布。
7. Native 或 Process 需要发布/安装/更新时读取 `subskills/publish-install/SKILL.md`；MCP 场景走连接配置，不走插件包安装。

## Mandatory Rules

- 所有插件代码默认遵循职责分离和依赖注入。
- 新增外部包前必须先查最新稳定版，再做 AOT 发布探测。
- 运行时安装和更新必须走仓库内 `skills/skill-plugin-installation`，不手工复制文件。
- 更新已加载插件必须调用 sys_list_loaded_plugins → sys_unload_plugin → 安装脚本 → sys_reload_plugin。
- 新开发优先选择 Native（AOT 性能最优）或 MCP（远程服务）；需要完整 JIT 生态时选择 Process 通道。
- C# 插件发布前必须本地调试通过；Native 使用 `Netor.Cortana.Plugin.Native.Debugger`，Process 使用 Generator 生成的 `{PluginClass}Debugger`。
- 不要手写 Native/Process 宿主协议、工具路由、消息循环或 `plugin.json`，这些由框架和 Generator 负责。
- Native/Process 的共享插件协议必须通过 Attribute 声明，由 Generator 输出到 `plugin.json` / `get_info`：不要手工维护 `requiredHostCapabilities`、`settingsSchema`、`providedCapabilities` JSON。
- 插件向宿主申请权限时，使用类级别 `RequiredHostCapabilityAttribute`；这只是“申请声明”，不等于宿主已经授权。
- 插件需要在宿主设置界面生成配置项时，使用类级别 `PluginSettingAttribute`；敏感项必须标记 `Sensitive = true`，且插件代码不得回显或写日志。
- 插件在运行时只通过 `PluginSettings` 读取宿主注入的目录、端口和 `Extensions` 扩展参数，不手工解析原始 `init` JSON。

## Shared Protocol Surface

Native / Process 通道共享的插件元数据协议包含四个层面：

- `[Plugin]`：声明插件基础信息，以及插件“提供给宿主”的 `Capabilities`。
- `[RequiredHostCapability]`：声明插件“希望宿主提供”的能力申请，会生成 `requiredHostCapabilities`。
- `[PluginSetting]`：声明插件设置界面字段，会生成 `settingsSchema`。
- `PluginSettings`：宿主在 `init` 阶段注入的运行时配置，供工具和服务读取。

当用户提到“权限申请”“设置面板”“插件协议”“宿主注入参数”时，优先按这套协议建模，而不是新造自定义清单字段。

## Debug Policy

| 通道 | 调试方式 | 最低要求 |
|------|----------|----------|
| Native C# | 运行脚手架生成的 `Debug\*.Debug.csproj`，通过 `PluginDebugRunner` 进入 REPL | `dotnet build` 成功；REPL 能发现唯一 `[Plugin]`；每个 `[Tool]` 至少调用一次 |
| Process C# | `dotnet run -- --self-test`，使用 `{PluginClass}Debugger` 强类型调用工具 | `InitAsync()` 成功；每个 `[Tool]` 至少调用一次；失败时返回非 0 退出码 |
| MCP | 连接配置校验和服务可达性验证 | 配置校验通过；服务能正常响应 |

Native 调试验证的是开发态托管程序集逻辑；发布前仍必须执行 Native AOT 发布验证。Process 调试器通过内存管道走同一消息循环路径，不要再手工拼协议 JSON。

## Default Commands

```powershell
scripts\setup-dev-environment.ps1
scripts\create-native-plugin.ps1 -Name MyPlugin -Id my_plugin
dotnet run --project Samples\MyPlugin\Debug\MyPlugin.Debug.csproj
scripts\create-process-plugin.ps1 -Name MyPlugin -Id my_plugin
dotnet run --project Samples\MyPlugin -- --self-test
subskills\mcp\scripts\validate-mcp-server-config.ps1 -TransportType stdio -Name github -Command npx -Arguments '-y','@modelcontextprotocol/server-github'
scripts\resolve-package-version.ps1 -PackageId Polly
scripts\test-aot-package.ps1 -PackageId Polly
```

## Dispatch

| 场景 | 读取文件 |
|------|---------|
| 设计/重构/规范问题 | `subskills/architecture/SKILL.md` |
| 插件协议/权限申请/设置 schema | `subskills/architecture/resources/plugin-metadata-protocol.md` |
| Native AOT 插件 | `subskills/native/SKILL.md` |
| MCP 服务接入 | `subskills/mcp/SKILL.md` |
| Process 进程插件（JIT/AOT exe） | `subskills/process/SKILL.md` |
| 第三方包 AOT 评估 | `subskills/packages/SKILL.md` |
| 发布/安装/热更新（Native/Process） | `subskills/publish-install/SKILL.md` |
