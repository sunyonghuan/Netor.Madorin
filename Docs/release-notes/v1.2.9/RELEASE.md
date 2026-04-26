# Cortana v1.2.9 Release Notes

发布日期：2026-04-25

## 概览
- 修复 OpenAI 兼容协议下的工具链断裂与 400 错误（reasoning_content 必须回传）。
- 新增“思考模式”交互能力位（Reasoning，默认关闭），按模型精确控制是否回传/持久化 reasoning。
- 工具配送与子智能体工具注入改为按“函数调用”能力位（FunctionCall）生效，设置与行为一致。
- UI：模型设置页新增“思考模式”开关；新增模型与远程拉取模型默认开启“函数调用”。
- 历史视图：隐藏仅 tool_call 占位的助手消息与 role=tool 消息，避免用户看到工具内部细节。

## 详细改动
### Chat 历史与工具链
- 修复 assistant 消息 ID 复用导致的 INSERT OR REPLACE 覆盖与乱序，确保完整链条入库（assistant tool_calls → tool result → assistant）。
- SaveGeneratedAssetsAsync 改为按已入库 assistant 消息 ID 一一对应保存生成资源。
- 新增链路诊断日志；当模型未启用 Reasoning 时，过滤 TextReasoningContent 后再持久化（节省存储/避免隐私暴露）。

### 思维模式（Reasoning）治理
- 交互能力位新增：InteractionCapabilities.Reasoning（默认关闭）。
- 请求侧：TokenTrackingChatClient 仅在开启/必要时回传 reasoning；流式阶段总是捕获上一轮 reasoning，并在需要时自动补回（含兜底），彻底规避 400。
- 持久化：关闭时不过账 reasoning（不写 ContentsJson 与文本快照）。

### 函数调用与工具配送
- 仅在模型启用 InteractionCapabilities.FunctionCall 时配送插件/MCP/子智能体工具。
- 子智能体工具注入也受主模型 FunctionCall 能力控制，避免禁用场景下仍被注入。

### UI 与默认策略
- 模型设置页新增“思考模式”复选框，读取/保存交互能力位。
- 新增模型默认勾选“函数调用”；“刷新远程模型”拉取后默认赋予 FunctionCall。
- 日志输出支持工作区目录或 CORTANA_LOG_DIR，方便测试与采集。

## 不兼容变更
- SaveGeneratedAssetsAsync 方法签名调整：接收已入库的 assistant 消息 ID 列表以稳定资源归属（项目内已统一调整）。

## 升级与验证
1. 更新代码并构建（Entitys → AI → AvaloniaUI）。
2. 在“模型设置”中：
   - 需要工具链的模型：确保勾选“函数调用”。
   - 强制要求 reasoning 的模型：可勾选“思考模式”；或保持关闭，依赖“自动捕获+必要时补回”的兼容策略（不会持久化）。
3. 端到端验证（OpenAI 兼容）：跑一轮 assistant tool_calls → tool result → assistant，确认无 400、链条完整、历史视图隐藏工具占位。

## 文件参考
- 交互能力位：Src/Netor.Cortana.Entitys/Entities/ModelCapabilities.cs
- 历史与资源：Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs
- 回传与捕获：Src/Netor.Cortana.AI/Providers/TokenTrackingChatClient.cs
- 工具配送 gating：Src/Netor.Cortana.AI/AIAgentFactory.cs
- 模型设置 UI：Src/Netor.Cortana.AvaloniaUI/Views/Settings/ModelSettingsPage.axaml(+.cs)

