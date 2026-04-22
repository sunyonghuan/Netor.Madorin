---
name: native
description: 'Cortana Native 插件开发子技能。位置：plugin-development/subskills/native。用于 Native AOT 插件创建、Startup 组合、Tool 编写、JsonContext、后台服务、AOT 规则。触发关键词：Native 插件、AOT 插件、Startup.Configure、ToolAttribute、原生插件。'
version: 1
user-invocable: true
---

# Plugin Development Native

## Flow

1. setup-dev-environment.ps1。
2. create-native-plugin.ps1。
3. 有外部包时切到 packages 子技能。
4. 按 architecture 子技能约束实现。
5. publish-native-plugin.ps1；需要安装包时加 -CreateZip。
6. 运行时安装切到 publish-install 子技能。

## Rules

- 只能有一个 PluginAttribute 入口。
- Startup 只注册依赖。
- Tool 参数只用支持的基础类型。
- 自定义返回模型必须进入 PluginJsonContext。
- 后台任务用 IHostedService。

## Scripts

- scripts/setup-dev-environment.ps1
- scripts/create-native-plugin.ps1
- scripts/publish-native-plugin.ps1

## Resources

- resources/layout.md