---
name: publish-install
description: 'Cortana 插件发布与安装子技能。位置：plugin-development/subskills/publish-install。用于发布 zip 包、遵循安装规范、更新已加载插件时调用 sys_list_loaded_plugins/sys_unload_plugin/sys_reload_plugin。触发关键词：发布插件、安装插件、更新插件、热重载插件、插件 zip。'
version: 1
user-invocable: true
---

# Plugin Development Publish Install

> 仅适用于 Native / Process 插件包。MCP 通道通过设置界面或数据库配置连接信息，不走 zip 安装。

## Runtime Install Flow

1. publish-native-plugin.ps1 或 publish-process-plugin.ps1，带 -CreateZip。
2. sys_list_loaded_plugins。
3. 更新时 sys_unload_plugin(dirName)。
4. install-plugin-package.ps1 或 skill-plugin-installation/install-package.ps1。
5. sys_reload_plugin(dirName)。
6. 验证已加载。

## Rules

- 运行时安装或更新不手工复制文件。
- 必须使用 zip 安装规范。
- 已加载插件更新必须先 unload 后 install 再 reload。

## Scripts

- scripts/install-plugin-package.ps1

## Resources

- resources/runtime-flow.md