# 插件开发指南

本目录面向第三方插件开发者，按当前 Madorin 插件框架整理。Native / Process 插件通过 Attribute 声明元数据，由 Generator 自动生成 `plugin.json`、工具路由和宿主协议；MCP 通道通过连接配置接入外部服务，不生成 `plugin.json`。

| 文档 | 说明 |
| --- | --- |
| [插件开发总览.md](插件开发总览.md) | 插件通道选择、开发流程、运行时配置、现行约束 |
| [Process插件开发指南.md](Process插件开发指南.md) | 独立进程插件、调试自测、JIT/AOT 发布 |
| [Native插件开发指南.md](Native插件开发指南.md) | Native AOT 插件、Debug Console、本地工具实现 |
| [插件清单与能力声明.md](插件清单与能力声明.md) | `plugin.json`、能力声明、宿主能力申请、事件契约声明、设置 schema |
| [工具声明与参数规范.md](工具声明与参数规范.md) | `[Tool]` / `[Parameter]` 写法、命名、参数、返回值 |
| [插件事件总线开发指南.md](插件事件总线开发指南.md) | PluginBus 1.4.0 事件总线、`[PublishesEvent]` / `[SubscribesEvent]`、SDK 用法、AOT 约束 |
| [宿主LLM模型能力调用.md](宿主LLM模型能力调用.md) | 插件申请并调用宿主授权大模型的完整案例 |
| [发布安装与调试.md](发布安装与调试.md) | 构建、发布、zip 安装、更新、调试门禁 |
| [语音插件开发指南.md](语音插件开发指南.md) | KWS / STT / TTS 语音插件、PluginBus 事件接入与订阅、模型目录 |

> 注意：不要手写 Native / Process 的宿主协议、工具路由或 `plugin.json`。这些由 `Netor.Cortana.Plugin.Native` / `Netor.Cortana.Plugin.Process` 及其 Generator 负责。
