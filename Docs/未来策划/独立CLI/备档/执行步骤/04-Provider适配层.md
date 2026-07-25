# 阶段 4：Provider 适配层 : 100%

> 阶段状态：已完成 · 总进度：100%
>
> 前置阶段：[03-Run状态机与持久化基线](./03-Run状态机与持久化基线.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 50 | 50 | 100% |
| 测试要求 | 7 | 7 | 100% |
| 完成标准 | 5 | 5 | 100% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

## 1. 目标

实现自有 Provider Adapter 边界、OpenAI Responses、Anthropic Messages 和 OpenAI Compatible 三个正式 Adapter，将厂商请求、流式事件、工具调用、用量、取消和错误映射为 Runtime 内部统一模型。

## 2. 前置门禁

- Run/Invocation 生命周期、取消、凭据等待和唯一终态已经稳定。
- Canonical content、Provider request/event、错误和能力契约已经冻结。
- 阶段 3 的 Fake Provider 可以驱动完整状态机和事件 Outbox。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Providers.Abstractions` | 稳定自有 Adapter API、能力、模型、请求和事件 |
| `Madorin.AI.Runtime.Providers.OpenAI` | OpenAI Responses API 实现 |
| `Madorin.AI.Runtime.Providers.Anthropic` | `Netor.Anthropic` Messages/Streaming 实现 |
| `Madorin.AI.Runtime.Providers.OpenAICompatible` | 可配置兼容端点实现 |
| `Madorin.AI.Runtime.Services` | Adapter 选择、能力校验和统一调用流程 |
| `Madorin.AI.Runtime.Server` | HttpClient、Adapter 和配置的 DI 装配 |
| `Madorin.AI.Runtime.Provider.Tests` | 三 Adapter 一致性、录制响应和故障测试 |

## 4. 执行步骤 : 100%

### 4.1 固化自有 Adapter 边界 : 100%

- [√] `IRuntimeProviderAdapter` 只暴露 `Madorin.AI.Runtime.*` 类型。
- [√] 核心方法覆盖 Provider ID、能力、模型目录、配置 Schema、token 估算和流式完成。
- [√] 所有调用接受 `CancellationToken`，请求包含 Invocation Snapshot 所需的实际选择信息。
- [√] MAF `IAgent`、MEAI `IChatClient`、厂商请求/响应类型不得出现在 Abstractions、Contracts 或 Client。
- [√] 为 Provider 扩展字段定义命名空间规则和最大大小，宿主不依赖扩展也能完成核心流程。

### 4.2 建立能力与模型目录 : 100%

- [√] 能力至少覆盖 Streaming、ToolCalling、Vision、Audio、StructuredOutput、Reasoning、PromptCaching、Files、ComputerUse、Embeddings。
- [√] V1 只对范围内能力声明 true；未实现能力不得根据模型名称猜测。
- [√] 模型目录记录上下文窗口、输出限制、能力、弃用状态和 Provider 原始模型 ID。
- [√] Provider 能力与模型能力分层；模型限制可以收紧 Provider 总能力。
- [√] 请求前执行能力校验，不支持时返回稳定 `CapabilityNotSupported`，禁止静默降级。

### 4.3 建立规范化消息转换 : 100%

- [√] 输入只来自本次 Invocation 的 ContextProjection，不直接读取 UI 或厂商私有 Session。
- [√] 将 text、reasoning、tool call、tool result 和 Blob reference 映射到目标 SDK。
- [√] 工具调用与结果作为不可拆分单元保留，跨 Provider 切换时使用 CanonicalHistory 重建。
- [√] 无法转发的 reasoning signature、缓存引用或私有内容产生明确投影调整/降级事件。
- [√] 路径、URL、JSON 参数和首尾空白按原值转发，不二次编码。

### 4.4 管理 `IChatClient` 与 HTTP 生命周期 : 100%

- [√] `HttpClient` 由 `IHttpClientFactory` 管理，设置端点、连接池、超时边界和脱敏日志。
- [√] `IChatClient`/SDK Client 按 Provider Profile 复用，不按 Invocation 反复创建和销毁。
- [√] `IAgent` 按 Invocation 创建并结束后释放，不跨 Session 复用内部状态。
- [√] 重试、日志、追踪和遥测中间件只存在于 Adapter 内部。
- [√] Runtime 层负责总超时和取消，Adapter 不用无限 SDK 重试覆盖上层策略。

### 4.5 实现 OpenAI Responses Adapter : 100%

- [√] 映射 Responses API 的输入、instructions、模型参数、工具和结构化输出。
- [√] 将文本增量、reasoning、工具调用参数增量、完成原因和用量归一化。
- [√] 工具调用参数只有在 JSON 完整后才进入 Runtime 工具网关。
- [√] 处理认证、限流、内容策略、网络、超时和协议错误，并保留受控原始码。
- [√] Provider 不支持的输入块在请求前失败，不发送部分请求。

### 4.6 实现 Anthropic Messages Adapter : 100%

- [√] 继续使用外部包 `Netor.Anthropic`，不得将包名改为 Madorin。
- [√] 映射 system、messages、tool use/result、thinking 和 usage。
- [√] thinking 增量映射为 `reasoning.delta`；签名作为 Provider 扩展保留。
- [√] 若 MAF 需要 `IChatClient`，适配逻辑完全封装在 Anthropic 项目内部。
- [√] 正确处理 Anthropic 停止原因、429、401、请求 ID 和流中错误。

### 4.7 实现 OpenAI Compatible Adapter : 100%

- [√] 配置 endpoint、认证头、模型 ID 映射和可选 API 路径，不硬编码单一厂商。
- [√] 启动或 `providers test` 时探测声明能力，探测结果带时间和配置版本。
- [√] 不支持标准工具调用、reasoning 或 usage 时明确标记能力缺失。
- [√] 对非标准响应只允许显式配置的兼容策略，不按自然语言或偶然字段猜测。
- [√] 原始响应扩展有大小和敏感信息过滤限制。

### 4.8 统一流式事件与完成语义 : 100%

- [√] Adapter 只产生 `RuntimeProviderEvent`，由 Services 转换为持久化 Runtime 事件。
- [√] Delta 顺序与厂商流一致；文本和 reasoning 使用不同事件类型。
- [√] 工具调用、完成原因和 usage 更新可以晚于文本，但 Invocation 结束前必须归并完成。
- [√] Provider 未返回精确用量时标为 Unknown/Estimated，不伪造精确值。
- [√] 流在中途失败时保留已发送 Delta，并以明确错误结束 Invocation/Run。

### 4.9 统一取消、限流与凭据续期 : 100%

- [√] CancellationToken 传递到 SDK/HTTP 流读取和响应处置。
- [√] Provider 不支持远端取消时停止本地读取并记录能力缺失。
- [√] 429/容量错误保留 retry-after，Runtime 不在 Adapter 内无限排队。
- [√] 401 触发 `WaitingForCredentials` 和凭据刷新请求；更新后只重试一次。
- [√] 重试使用新的内部 requestId，但保持原 invocationId；是否允许重试依据请求幂等性和是否已有不可逆工具副作用。

### 4.10 控制 Experimental 与 AOT 风险 : 100%

- [√] MAF/MEAI 包版本集中管理并锁定，不允许各 Adapter 漂移。
- [√] `MAAI001`、`MAAIW001`、`MEAI001` 仅在确实引用实验 API 的项目定向抑制，并注明移除条件。
- [√] 每次包升级先运行 Adapter 和协议一致性测试，确认 Runtime 事件不变。
- [√] 将所需序列化类型、反射元数据和动态依赖登记到 AOT 配置。
- [√] Release publish 出现 trimming/AOT 警告时阶段验收失败，不通过盲目增加整程序集保留掩盖问题。

## 5. 测试要求 : 100%

- [√] 建立 Adapter 共用一致性套件，同一测试向量对三个实现运行。
- [√] 覆盖文本、reasoning、工具调用、结构化输出、用量、完成原因和错误映射。
- [√] 使用本地 Fake HTTP Server 模拟分片、半包、慢流、401、429、5xx、断流、无效 JSON 和取消。
- [√] 验证能力不足在发送请求前失败，且没有静默参数丢失。
- [√] 验证跨 OpenAI/Anthropic 切换仍使用同一 CanonicalHistory，不依赖厂商会话 ID。
- [√] 日志、异常和诊断包不包含 API Key、Authorization、完整 Prompt 和敏感响应体。
- [√] 可选真实 Provider 冒烟测试与默认离线测试分离，不把外部网络稳定性作为普通单元测试前提。

## 6. 完成标准 : 100%

- [√] 三个 Adapter 通过相同的一致性测试集。
- [√] 文本流、reasoning、工具调用、用量、完成原因、取消和错误均能映射为统一事件。
- [√] `Providers.Abstractions`、`Contracts`、`Core`、`Client` 不引用 MAF、MEAI 或厂商 SDK。
- [√] Provider/模型能力可查询，能力不足有稳定错误。
- [√] Debug/Release 构建和 Native AOT 发布保持 0 警告。

## 7. 验收记录

| 验收项 | 结果 |
| --- | --- |
| 本机验收基线 | 2026-07-22；.NET SDK `10.0.301`；对比基线 `f7792ae2d12c` |
| 协议与 Schema | Runtime 协议 `1.0`；SQLite Schema `7` |
| Provider 专项测试 | `77` 通过，`3` 个 `External` 真实 Provider 冒烟测试按设计跳过；设置 `MADORIN_PROVIDER_SMOKE=1` 并提供凭据后运行 |
| 全量 Debug 测试 | `323` 通过，`3` 跳过，`0` 失败 |
| Release 构建 | 全解决方案非增量构建成功，`0` 警告，`0` 错误 |
| Native AOT | `win-x64` 自包含发布成功，`0` 条 trimming/AOT 警告；原生产物启动输出 `madorin 0.1.0 (protocol 1.0)` |
| 未关闭限制 | 真实 Provider 冒烟测试仍依赖外部网络和用户凭据；远端取消能力显式声明为不支持，已验证本地流终止语义 |
| 回退参考 | 以阶段 4 开始前的 Git 基线 `f7792ae2d12c` 作为差异对比点；回退时保留后续阶段的工作树改动 |

## 8. 风险与禁止事项

- 不得公开 `IChatClient` 作为 Provider SDK。
- 不得按 Invocation 创建新的 HttpClient 连接池。
- 不得把 Provider 私有 Session 当作对话权威状态。
- 不得用 `NoWarn` 隐藏 trimming、AOT、空引用或一般分析器问题。
- 不得为“兼容所有 API”加入无边界反射、动态类型或自然语言响应解析。
