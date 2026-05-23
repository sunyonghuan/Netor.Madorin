# 插件能力授权、全局插件与 LLM 模型绑定实现计划 : 0%

## Step 1 梳理现状与边界 : 0%
- [×] 确认当前插件加载、智能体绑定、工具发现和全局目录结构。
- [×] 确认 `Memory.ModelId` 当前仅为设置端预留，未进入真实插件模型调用链路。
- [×] 确认当前 `SystemSettingsService`、设置页和插件管理相关代码的可复用点。
- [×] 确认当前 `/internal/conversation-feed/` 实际运行服务，避免新增控制面时出现双实现分叉。
- [×] 确认 Native AOT JSON 序列化上下文边界，列出宿主可绑定的通用 DTO。

## Step 2 建立插件授权配置模型 : 0%
- [×] 设计插件授权配置键或专用实体，覆盖插件启用、全局插件、安装来源、能力授权和 purpose 绑定。
- [×] 删除或废弃 `Memory.ModelId` 设置种子与设置页入口。
- [×] 如存在历史 `Memory.ModelId` 值，提供迁移策略，将其转换为 `memory_engine` 的 LLM purpose 默认绑定。
- [×] 增加插件授权读取服务，用于按 `pluginId + capability + purpose` 查询授权、预算、超时和模型绑定。
- [×] 增加 `plugin-push` capability 授权配置，并预置 Memory 插件的 `memory-context` purpose。
- [×] 增加授权配置的默认值策略：未配置即不授权，不隐式回退主对话模型。

## Step 3 实现全局插件能力 : 0%
- [×] 在插件设置中增加“设为全局插件”配置。
- [×] 实现全局插件目录校验：仅允许用户数据目录全局插件设为全局。
- [×] 禁止工作区插件、临时插件默认提升为全局插件。
- [×] 调整智能体可用插件计算逻辑：最终集合 = 全局插件 + 智能体显式绑定插件。
- [×] 增加插件移动、删除、来源校验失败时撤销全局状态的保护逻辑。

## Step 4 扩展系统设置面板 : 0%
- [×] 新增“插件设置 / 插件授权”分组或页面。
- [×] 展示插件启用状态、安装来源、全局插件状态和可授权能力。
- [×] 为 LLM capability 增加 purpose 列表配置。
- [×] 为每个 LLM purpose 增加 Provider / Model 选择、输入 token、输出 token、超时、并发和后台调用配置。
- [×] 在界面上对不可设为全局的插件给出明确原因。

## Step 5 定义 AOT 友好的授权下发 : 0%
- [×] 在 `PluginSettings.Extensions` 中增加 `pluginAuthorizationVersion`、`pluginGlobalEnabled`、`pluginInstallScope` 等字段。
- [×] 增加 `modelCapabilityEnabled`、`modelCapabilityEndpoint`、`modelCapabilityProtocol`、`modelCapabilityVersion`、`modelCapabilityPurposes`、`modelCapabilityToken` 字段。
- [×] 增加 `modelCapabilityGrantsJson` 字符串，承载授权摘要。
- [×] 增加 `pluginPushEnabled`、`pluginPushPurposes`、`pluginPushMaxItems`、`pluginPushMaxChars` 字段。
- [×] 确保授权下发只使用字符串字典和 JSON 字符串，不引入插件业务 DTO。
- [×] 插件侧使用 `JsonDocument` / `JsonElement` 或轻量字符串解析读取授权摘要。

## Step 6 建立 model-capability WebSocket 控制面 : 0%
- [×] 在 `MadorinWsEndpoints` 增加 `ModelCapabilityPath`、`ModelCapabilityProtocol`、`ModelCapabilityVersion`。
- [×] 在当前实际运行的内部 WebSocket 服务中挂载 `/internal/model-capability/`。
- [×] 增加 `/internal/plugin-push/` 或在同一控制面支持通用插件推送操作。
- [×] 实现 connected / subscribe / subscribed / error 握手流程。
- [×] 定义通用 envelope、request、response、error、cancel DTO，并加入宿主 JSON source generation 上下文。
- [×] 实现 requestId 与 pending 调用管理，连接关闭时取消该连接上的 pending 调用。

## Step 7 实现宿主侧 LLM Broker : 0%
- [×] 新增 `IPluginModelCapabilityBroker` 和实现类。
- [×] 在 Broker 中校验本机连接、pluginId、token、插件启用状态、purpose 授权、预算、超时和并发。
- [×] 按 `pluginId + capability=llm + purpose` 读取 ProviderId / ModelId。
- [×] 复用现有 Provider、Model、Driver Registry 和 `IChatClient` 调用链。
- [×] 将 `prompt.system`、`prompt.instructions`、`prompt.input` 转换为模型消息。
- [×] 返回 providerId、modelId、usage、duration、traceId 和模型输出文本。
- [×] 写入宿主侧模型调用审计日志。

## Step 7.5 实现宿主侧通用插件推送接收 : 0%
- [×] 新增 `IPluginPushReceiver`。
- [×] 校验 `plugin-push:{purpose}` 授权、pluginId、token、targetKind、targetId、workspaceId、sessionId。
- [×] 校验推送包大小、schemaId、packageId 幂等性。
- [×] 按 purpose 和 op 路由到宿主处理器。
- [×] 将 `memory-context` 推送写入宿主上下文候选池。
- [×] 在智能体上下文构建阶段从候选池选择记忆注入。
- [×] 记录实际注入事件，支持回复追溯。

## Step 8 实现插件侧 model-capability 客户端 : 0%
- [×] 在 Memory 插件中新增 `IHostModelCapabilityClient`。
- [×] 实现 WebSocket 连接、握手、订阅、延迟连接、断线重连和取消。
- [×] 实现 requestId 与 pending task 映射。
- [×] 使用方案 B 协议发送 prompt，业务输入作为 opaque 字符串。
- [×] 处理宿主 error 响应，并映射为插件侧 fallback 决策。

## Step 9 接入 Memory 插件双 purpose : 0%
- [×] 实现或替换 `IMemorySemanticProcessor` 的模型感知代理。
- [×] 实现或替换 `IMemoryAbstractionGenerator` 的模型感知代理。
- [×] 为 `memory-processing` 构造 prompt，并将 observation 批次序列化为 opaque JSON 字符串。
- [×] 为 `memory-abstraction` 构造 prompt，并将 fragment 集合序列化为 opaque JSON 字符串。
- [×] 未授权、未连接、模型失败或 MCP 独立模式时继续使用 fallback。

## Step 9.5 实现通用插件主动推送与 Memory 用例 : 0%
- [×] 在插件侧实现通用 `plugin-push` 客户端能力。
- [×] 在 Memory 插件中生成 Memory Context Package。
- [×] 推送内容使用 opaque JSON 字符串，宿主不绑定 Memory 专用 DTO。
- [×] 根据 `pluginPushEnabled`、`pluginPushPurposes`、条目数、字符数和频率限制决定是否推送。
- [×] 使用 `purpose=memory-context` 与 `op=memory.context.upsert` 推送。
- [×] 处理宿主 `plugin.push.accepted` 和拒绝错误。
- [×] 记录 `plugin_push_started`、`plugin_push_accepted`、`plugin_push_rejected` 事件。

## Step 10 增加输出契约校验与证据记录 : 0%
- [×] 对 `memory-processing` 输出执行 JSON 解析和字段校验。
- [×] 对 `memory-abstraction` 输出执行 JSON 解析和字段校验。
- [×] 校验证据引用必须来自本次输入或可回查窗口。
- [×] 不合法候选或抽象不入库。
- [×] 通过 `MemoryEvent.PayloadJson` 记录 `model_call_started`、`model_call_completed`、`model_call_failed`、`model_call_fallback`、`model_output_invalid`。
- [×] 记录 requestId、traceId、purpose、providerId、modelId、promptHash、inputIds、outputIds、status、errorCode、durationMs。

## Step 11 增加测试与验证 : 0%
- [×] 增加插件授权配置读取测试。
- [×] 增加全局插件目录校验测试。
- [×] 增加智能体可用插件集合测试。
- [×] 增加 init extensions 授权下发测试。
- [×] 增加 model-capability 握手、授权失败、模型未配置、预算超限、取消和断线测试。
- [×] 增加 Memory 插件模型输出契约测试。
- [×] 增加端到端验证：observation -> fragment -> abstraction 全链路带 modelId、providerId、traceId。
- [×] 增加通用插件主动推送端到端验证：fragment / abstraction -> plugin push -> context pool -> 智能体上下文注入。

## Step 12 更新插件开发文档 : 0%
- [×] 更新插件开发文档，说明插件能力授权模型。
- [×] 更新插件开发文档，说明全局插件规则和用户数据目录限制。
- [×] 更新插件开发文档，说明 LLM capability 的 purpose、授权配置和预算字段。
- [×] 更新插件开发文档，说明 init extensions 授权下发字段。
- [×] 更新插件开发文档，说明 model-capability WebSocket 协议。
- [×] 更新插件开发文档，说明 plugin-push 通用主动推送协议、purpose/op 路由和 Memory 上下文候选池机制。
- [×] 更新插件开发文档，强调 AOT 边界：宿主只绑定通用 DTO，插件业务输入使用 opaque 字符串。
- [×] 给出 Memory 插件作为示例的接入流程。

## Step 13 收尾与发布前检查 : 0%
- [×] 运行相关单元测试和集成测试。
- [×] 构建主解决方案和插件解决方案。
- [×] 检查 Native AOT 发布路径是否存在 JSON 序列化告警。
- [×] 检查旧 `Memory.ModelId` 入口是否已删除或标记废弃。
- [×] 检查用户升级路径和默认授权行为。
- [×] 整理修改文件清单和发布说明。

## 修改文件清单

- [×] 待执行过程中补充。
