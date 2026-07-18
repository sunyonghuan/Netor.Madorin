# Kimi 专用协议接入执行计划

## 当前状态

- 状态：已完成
- 完成日期：2026-07-18
- 关联提交：`fd82216`

## 已完成内容

1. 接入 `KimiProviderDriver`，保留 `ProviderType = "Kimi"` 与显示名 `Kimi 专用协议`。
2. 接入命名 `HttpClient`：`Kimi`，并通过 `KimiOverrideHandler` 在发送前适配 Kimi 专用请求格式。
3. `KimiOverrideHandler` 已处理：
   - 顶层缺少 `thinking` 时注入 `{ "type": "enabled" }`。
   - assistant 消息包含 `tool_calls` 且缺少 `partial` 时注入 `partial = true`。
   - 已存在的 `thinking` 与 `partial` 保持原值。
   - 非 JSON、空 body、无 `messages` 或 `messages` 非数组时保持透传。
   - 清理空文本 content part，避免 Kimi 返回 `Invalid request: text content is empty`。
4. 新增 Kimi 协议自动化测试，覆盖 thinking、partial、透传、空文本清理等场景。
5. 新增工具数量限制能力：
   - Kimi 默认最大工具数为 128。
   - 通过 `AI.Provider.Kimi.MaxTools` 可配置工具上限。
   - 环境变量 `CORTANA_KIMI_MAX_TOOLS` 优先于系统设置。
   - 配置值小于等于 0 表示不限制，用于兼容 Kimi 后续放开限制的情况。
   - 超出工具上限时，会在发送给模型前裁剪工具列表，并通过 system.notice 与系统指令提醒用户如何处理。

## 用户可见影响

- 设置页会出现 `Kimi 最大工具数量` 配置项，默认值为 `128`。
- 当当前对话工具数量超过 Kimi 上限时，软件不会直接让模型接口拒绝请求，而是自动限制本轮工具数量。
- 用户会收到系统提醒，说明工具过多、已临时停用部分工具，并提示可禁用不需要的插件/MCP 或只挂载当前任务所需工具。
- 如果 Kimi 后续不再限制工具数量，可将 `AI.Provider.Kimi.MaxTools` 或 `CORTANA_KIMI_MAX_TOOLS` 设置为 `0`，无需修改源代码。

## 验证记录

- 通过：`dotnet test .\Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --no-restore --verbosity minimal`
  - 结果：140 个测试通过，0 个失败。
- 通过：`dotnet build .\Netor.Cortana.slnx --no-restore --verbosity minimal`
  - 结果：0 warning，0 error。

## 结论

Kimi 专用协议接入与多轮工具调用兼容问题已修复；工具数量限制已改为配置驱动，并能向用户说明处理方式，避免未来模型限制变化时必须修改源代码。
