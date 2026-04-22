---
name: plugin-development
description: 'Cortana 插件开发入口技能。用于统一编排 Native 和 MCP 两类通道，约束工程规范、外部包 AOT 校验、发布安装与运行时更新流程。触发关键词：插件开发、Native 插件、MCP 插件、发布插件、安装插件。'
version: 1
user-invocable: true
---

# Plugin Development

## Structure

- 主技能目录：plugin-development
- 子技能目录：plugin-development/subskills
- 共享脚本目录：plugin-development/scripts
- 子技能可有独立 scripts 和 resources；需要复用时由子技能脚本包装共享脚本

## Entry Flow

1. 先判断插件通道：Native、MCP。
2. 先加载 architecture。
3. 需要外部包时加载 packages。
4. Native 场景加载 native；MCP 场景加载 mcp。
5. Native 需要发布、安装、更新时加载 publish-install；MCP 场景走连接配置，不走插件包安装。

## Mandatory Rules

- 不在主技能里直接展开全部细节；按子技能分流。
- 所有插件代码默认遵循职责分离和依赖注入。
- 新增外部包前必须先查最新稳定版，再做 AOT 发布探测。
- 运行时安装和更新必须走 skill-plugin-installation，不手工复制文件。
- 更新已加载插件必须调用 sys_list_loaded_plugins → sys_unload_plugin → 安装脚本 → sys_reload_plugin。
- 新开发优先选择 Native；远程服务接入使用 MCP。

## Default Commands

```powershell
.\skills\plugin-development\scripts\setup-dev-environment.ps1
.\skills\plugin-development\scripts\create-native-plugin.ps1 -Name MyPlugin -Id my_plugin
.\skills\plugin-development\subskills\mcp\scripts\validate-mcp-server-config.ps1 -TransportType stdio -Name github -Command npx -Arguments '-y','@modelcontextprotocol/server-github'
.\skills\plugin-development\scripts\resolve-package-version.ps1 -PackageId Polly
.\skills\plugin-development\scripts\test-aot-package.ps1 -PackageId Polly
```

## Dispatch

- 设计/重构/规范问题：architecture
- Native AOT：native
- MCP 服务接入：mcp
- 第三方包：packages
- 发布/安装/热更新（仅 Native）：publish-install
