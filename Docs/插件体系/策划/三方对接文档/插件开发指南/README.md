# 插件开发指南

Madorin 按插件通道的运行时契约加载扩展，不检查插件的源码语言：

- Process 插件是实现 stdin/stdout 单行 JSON 协议的可执行程序。
- Native 插件是导出约定入口并遵守 UTF-8 字符串内存规则的原生 DLL。
- MCP 服务按 Model Context Protocol 的连接配置接入。

仓库同时提供完整的 C# 开发工具链。`Netor.Cortana.Plugin.Native`、`Netor.Cortana.Plugin.Process`、Generator、脚手架和调试器负责生成协议适配代码与 `plugin.json`，是当前维护最完整的实现方式。文档中的 C# 代码均为官方 SDK 示例；使用其他语言时，以对应通道的协议章节和 JSON 字段为准。

## 协议与清单

| 文档 | 说明 |
| --- | --- |
| [插件开发总览.md](插件开发总览.md) | 通道契约、选择依据、生命周期和开发流程 |
| [Process插件开发指南.md](Process插件开发指南.md) | stdin/stdout 请求、响应、生命周期及 C# SDK 用法 |
| [Native插件开发指南.md](Native插件开发指南.md) | Native DLL 导出入口、字符串所有权及 C# AOT 用法 |
| [插件清单与能力声明.md](插件清单与能力声明.md) | `plugin.json`、`get_info`、能力、事件契约和设置 schema |
| [插件与工具分类属性.md](插件与工具分类属性.md) | 插件级 Category / RiskLevel / Tags / SearchHints / Idempotent，以及工具级覆盖 |
| [工具声明与参数规范.md](工具声明与参数规范.md) | 工具元数据、参数类型、`invoke` 操作和 C# 映射 |
| [插件事件总线开发指南.md](插件事件总线开发指南.md) | PluginBus 1.4.0 发布、订阅、转发和宿主事件 |
| [宿主LLM模型能力调用.md](宿主LLM模型能力调用.md) | `model.capability.request` 请求与响应协议 |

## 开发与专题

| 文档 | 说明 |
| --- | --- |
| [发布安装与调试.md](发布安装与调试.md) | 发布物结构、C# 发布脚本、zip 安装与调试门禁 |
| [语音插件开发指南.md](语音插件开发指南.md) | KWS / STT / TTS 能力、事件 payload 和宿主桥接行为 |
