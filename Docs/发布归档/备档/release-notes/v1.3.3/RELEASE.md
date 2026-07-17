# Madorin v1.3.3 Release Notes

发布日期：2026-05-06

## 概览

v1.3.3 是一次面向 AI 工具调用稳定性的修复版本，重点解决 OpenAI-compatible / DeepSeek 工具调用历史在多工具结果场景下可能触发的 HTTP 400 协议错误。

本版本同时加入可配置的 AI 全量调试日志，用于记录请求、响应、流式更新、异常与工具协议诊断；默认关闭，排障时可在系统设置中开启。

一句话：**本版本让工具调用历史更稳，也让 AI 协议问题更容易定位。**

## 核心修复：工具历史重排不再重复追加同一条 tool 消息

此前在部分工具调用链中，assistant 一次发起多个 tool_calls，而持久化历史中可能用同一条 `role=tool` 消息保存多个 tool result。

旧的历史重排逻辑按 callId 逐个追加 tool 消息，当多个 callId 都映射到同一条 tool message 时，会把同一条消息重复追加多次：

```text
assistant tool_calls: [A, B, C]
tool message:       [A, B, C]
```

错误重排后可能变成：

```text
assistant
tool
tool
tool
```

后两条 `tool` 消息不再是紧邻 assistant tool_calls 的合法响应，DeepSeek / OpenAI-compatible 接口会返回：

```text
Messages with role 'tool' must be a response to a preceding message with 'tool_calls'
```

v1.3.3 已修复该问题：重排时先按 tool message index 去重，同一条 tool 消息即使包含多个 callId，也只会输出一次。

## 新能力

### 1. AI 全量调试日志

新增可配置的 AI trace 能力，支持记录：

- 请求消息
- 响应消息
- 流式请求
- 流式响应文本
- 流式更新片段
- usage 统计
- 异常堆栈
- 工具协议诊断

默认写入工作区：

```text
.cortana/logs/ai-traces
```

也可以通过环境变量指定：

```text
MADORIN_AI_TRACE_DIR
```

### 2. 工具协议诊断

trace 中新增协议诊断信息，可帮助定位：

- orphan tool message
- missing tool response
- non-adjacent tool message
- assistant tool_call 和 tool result 的 callId 对应关系

这类信息可以直接用于排查 OpenAI-compatible 400 错误。

### 3. 调试日志开关

系统设置中新增：

```text
AI.Trace.Enabled
```

也可以通过环境变量控制：

```text
MADORIN_AI_TRACE_ENABLED=true
```

发布版默认关闭，避免持续写入大体积请求日志。

### 4. HTTP 请求/响应原文追踪

AI HTTP Handler 支持在 trace 开启时记录请求和响应原文。

安全处理：

- `Authorization: Bearer ...` 会脱敏为 `Authorization: Bearer ***`
- 读取响应体后会重新包装内容，避免影响下游 SDK 读取

## 改进

### 聊天历史持久化更完整

- 文本快照从结构化 `AIContent` 构建，不再只依赖 `message.Text`。
- `FunctionCallContent` 参数缺失或序列化失败时使用 `{}` 兜底。
- 恢复 function call 历史时使用空字典兜底。
- 工具 call/result 内容支持 Function、Tool、MCP 三类内容。

### 聊天窗口显示更干净

- 前端历史不再显示 `role=tool` 消息。
- 包含工具调用的 assistant 占位消息不再显示。
- reasoning 内容默认不渲染到聊天气泡。
- 历史文本中的 `[工具调用]`、`[工具结果]` 块会被清理。

### 日志与 UI 细节

- 默认应用日志目录调整为工作区 `logs`。
- 文件日志滚动粒度调整为分钟级。
- AI 代理窗口高度略微增加。
- 托盘提示统一使用应用名称。

## 版本更新

### 主程序

- Madorin：`1.3.2` → `1.3.3`

### 内置插件

- 宝塔面板插件：`1.0.16` → `1.0.17`
- 谷歌搜索插件：`1.0.15` → `1.0.16`
- Memory Engine：`1.0.4` → `1.0.5`
- 办公文档插件：`1.0.15` → `1.0.16`
- 定时提醒插件：`1.0.19` → `1.0.20`
- C# Script Runner：`1.0.14` → `1.0.15`
- WebSocket 中转插件：`1.0.14` → `1.0.15`

## 技能安装流程调整

`skill-plugin-installation` 技能与脚本同步调整：

- 安装说明改为更清晰的 zip 安装流程。
- 脚本输出编码设置为 UTF-8。
- skill 包结构统一要求根目录存在 `skill.md`。
- 插件包仍要求根目录直接存在 `plugin.json`。

## 升级建议

1. 更新到 v1.3.3 后，建议重新验证一次带工具调用的对话。
2. 如遇 AI 400 协议错误，可在系统设置中开启“AI 全量调试日志”。
3. 开启调试后复现问题，再查看 `.cortana/logs/ai-traces` 中对应 `stream-request` / `stream-error` 文件。
4. 排障完成后建议关闭 `AI.Trace.Enabled`，避免日志持续增长。

## 验证记录

- 已通过实际 DeepSeek 请求上下文定位 tool 消息重复问题。
- 已验证数据库原始历史中不存在重复 tool result。
- 已确认重复由历史重排逻辑引入。
- 已修复重排逻辑，并删除临时 `[ToolProtocolProbe]` 诊断代码。
- 已确认临时诊断标记无残留。

## 文件参考

- AI trace：`Src/Netor.Cortana.AI/Providers/TokenTrackingChatClient.cs`
- HTTP trace：`Src/Netor.Cortana.AI/Drivers/UserAgentOverrideHandler.cs`
- 历史重排：`Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs`
- 内容持久化：`Src/Netor.Cortana.AI/Extensions/ChatMessageExtensions.cs`
- 历史显示：`Src/Netor.Cortana.UI/Views/Main/MainWindow.Sessions.cs`
- 设置项：`Src/Netor.Cortana.Entitys/Services/SystemSettingsService.cs`
