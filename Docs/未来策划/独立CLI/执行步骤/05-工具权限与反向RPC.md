# 阶段 5：工具权限与反向 RPC : 89.5%

> 阶段状态：执行中 · 总进度：89.5%
>
> 前置阶段：[04B-全局与项目记忆](./04B-全局与项目记忆.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 60 | 62 | 96.8% |
| 测试要求 | 5 | 9 | 55.6% |
| 完成标准 | 3 | 5 | 60% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

本轮验收记录（2026-07-22）：

- 构建制品为 `madorin 0.1.0`；协议当前版本为 `1.1` 并继续支持 `1.0`，SQLite Schema 为 `v8`。
- Debug/Release 均为 0 警告、0 错误；全量测试连续两次均为 443 项通过、3 项跳过、0 项失败。跳过项为未配置凭据的 OpenAI、Anthropic 和 OpenAICompatible 真实 Provider 测试。
- Windows `win-x64` Release Native AOT 发布成功，0 条 trimming/AOT 警告；发布版 `madorin.exe --version` 输出 `madorin 0.1.0 (protocol 1.1)`。
- 已知限制：Unix 文件后端尚未改为完整的 `openat/renameat/unlinkat` 目录句柄相对实现；Linux/macOS 尚未执行本阶段实机安全矩阵；`UnrestrictedShell` 尚无独立授权与非沙箱报告；路径、Process 和 HTTP 的完整攻击/取消矩阵仍未闭合；真实 Provider 工具循环因无凭据未运行。
- 回退点：代码基线为 `f7792ae2d12cf563e3a0c4e34115a424ffe5ea5d`；协议保留 `1.0` 协商路径，数据库 `v7 -> v8` 迁移保持事务回滚并要求部署前保留 `state.db` 备份。
- 主要修改范围：`Contracts/Tools` 与 `RuntimeJsonContext`、`InvocationSnapshot`、`Tools.Abstractions`、`Services/Tools`、`Persistence.Abstractions`、`SqliteSchema`/`SqliteToolIntentRepository`、`Tools.Builtin`、`IDuplexRpcPeer`/`FramedControlChannel`、`RuntimeServer`/`ReconnectableDuplexRpcPeer`/`ReverseRpcToolExecutor`、`ExpertModeOrchestrator` 及对应 Protocol、Provider、Persistence、Modes、EndToEnd 测试。

## 1. 目标

实现版本化工具目录、Runtime 内置工具、宿主/MCP 反向 RPC、权限 Grant、用户审批、委派链、撤销和工具调用幂等。阶段结束时模型可以安全完成“申请权限 -> 调用 -> 获得结果 -> 继续推理”。

## 2. 前置门禁

- 三个 Provider Adapter 能产生统一工具调用事件。
- 个人 CLI 配置可以转换为现有 Provider、模型和 Agent 输入，宿主模式不依赖该配置。
- Memory File Service、两个固定 `memory.md` 以及 `builtin.memory.read/append` 的工具契约已经冻结。
- `WaitingForTool`、`WaitingForApproval`、取消和唯一终态已经稳定。
- `state.db`、Blob Store 和 GSN Outbox 可以在故障后恢复。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Tools.Abstractions` | 工具目录、执行器、权限上下文和结果接口 |
| `Madorin.AI.Runtime.Tools.Builtin` | memory、fs、content、process、powershell、http 实现 |
| `Madorin.AI.Runtime.Services` | 工具网关、Grant 计算、审批和反向 RPC 编排 |
| `Madorin.AI.Runtime.Persistence.Sqlite` | 工具意图、结果、Grant、委派和审计记录 |
| `Madorin.AI.Runtime.Transport.Abstractions` | 反向请求/响应和取消关联 |
| `Madorin.AI.Runtime.Server` | 工具注册、策略和宿主连接装配 |
| `Madorin.AI.Runtime.Protocol.Tests` | 工具/权限消息快照和兼容测试 |
| `Madorin.AI.Runtime.EndToEnd.Tests` | 权限、审批、重连、幂等与边界攻击测试 |

## 4. 执行步骤 : 96.8%

### 4.1 实现版本化工具目录 : 100%

- [√] 工具定义包含稳定 toolId、显示名、描述、参数/结果 Schema、风险、超时和能力标签。
- [√] 宿主以 `tool.catalog.replace` 发布完整版本，以 `tool.catalog.patch` 增量更新。
- [√] 每次 Invocation Snapshot 固定 `toolCatalogVersion`。
- [√] 更新只在下一次模型请求或 Workflow 安全点生效；已发请求和已开始调用继续使用旧版本。
- [√] toolId 使用 `builtin.*`、`host.*`、`mcp.<server>.*` 等明确命名空间，冲突时拒绝目录。
- [√] 参数 Schema 在发送给模型前验证，模型返回参数在执行前再次验证。
- [√] `builtin.memory.read/append` 纳入同一目录；`append` 标记为需要用户审批的写操作。
- [√] 两个记忆工具直接提供给 LLM 并在当前进程执行，不路由到宿主/MCP 反向 RPC。

### 4.2 建立最小权限计算器 : 100%

- [√] 有效权限是宿主 Grant、父级可委派范围、Run 工具目录和 Runtime 安全策略的交集。
- [√] 默认 Restricted：工作区内读/新建/写入允许；覆盖、移动、删除需额外权限；工作区外、任意进程、PowerShell 和任意网络拒绝。
- [√] Grant 包含 grantId、runId、根目录、读写范围、危险动作、程序、PowerShell、网络、过期和委派字段。
- [√] 所有策略先规范化为同一内部模型，再执行交集；禁止用“后配置覆盖前配置”扩大权限。
- [√] 权限失败返回稳定错误和所缺能力，不尝试其他目录、工具或更宽策略。

### 4.3 实现文件路径边界 : 87.5%

- [√] 相对路径只以冻结的 `workspaceRoot` 为基准。
- [√] 保留调用方原始路径用于显示，另生成规范化绝对路径用于授权。
- [√] 分别校验源、目标、最终父目录和实际打开目标，不能使用普通字符串前缀判断父子关系。
- [×] 解析符号链接、目录联接点和 Reparse Point 后再次校验，处理检查与打开之间的替换竞态。
- [√] 显式拒绝越权 `..`、未授权 UNC、设备路径、备用数据流和平台特殊路径。
- [√] `.madorin` 及其真实目标路径始终由默认 Agent 工具拒绝，即使软链接从工作区其他位置指向它。
- [√] 复制、移动、覆盖、删除同时检查源权限、目标权限和动作权限。
- [√] Memory File Service 是访问两个固定 `memory.md` 的唯一例外，普通 `builtin.fs` 不获得该例外。

### 4.4 实现 `builtin.fs` 与 `builtin.content` : 100%

- [√] fs 覆盖列表、状态、读文本/Blob、写文本/Blob、建目录、复制、移动、删除、搜索和补丁。
- [√] 所有大内容走流或 Blob，不一次性读取无限文件。
- [√] 文本读取显式返回编码、截断、偏移和是否完整；无法可靠识别编码时按 Blob 处理。
- [√] 写入优先使用临时文件加原子替换；覆盖和删除必须单独授权。
- [√] content 提供分页、哈希、内容类型和摘要前处理，不修改原内容。
- [√] 搜索限制根目录、结果数、总字节和耗时，支持取消。

### 4.5 实现 `builtin.process` 与 `builtin.powershell` : 83.3%

- [√] `AllowlistedProcess` 只运行 Grant 中的可执行程序，参数使用数组，环境变量使用白名单。
- [√] WorkingDirectory 必须在允许范围内，但不得宣称它能限制子进程文件权限。
- [√] stdout/stderr 并发读取，具有独立和总字节上限、截断标记、超时与取消。
- [√] 使用平台进程树回收机制，取消或超时后不得遗留子进程。
- [√] PowerShell 默认关闭；启用时使用 `-NoProfile`、`-NonInteractive`，脚本经 stdin 或受控文件传递。
- [×] V1 `UnrestrictedShell` 必须显式授权并报告没有 OS 级工作区隔离；不得标为 Sandboxed。

### 4.6 实现 `builtin.http` : 100%

- [√] 默认网络策略为 Deny；只允许 Grant 中的协议、主机、端口和地址范围。
- [√] 每次重定向和 DNS 解析后重新校验目标，防止跳转到本地/保留网络。
- [√] 限制请求体、响应头、响应体、重定向次数和总耗时。
- [√] 大响应写入 Blob，日志不记录 Authorization、Cookie 或敏感正文。
- [√] Provider 自身端点访问与 Agent `builtin.http` 权限分离，二者不可互相借用。

### 4.7 实现工具意图执行前落库 : 100%

- [√] 在发送权限或调用请求前生成稳定 `callId`。
- [√] 事务性写入 invocationId、runId、sessionId、agentId、toolId、argumentsHash 和 Pending 状态。
- [√] 获权并即将发送时更新为 Sent；响应后写入 Succeeded/Failed/Cancelled。
- [√] 成功/失败结果内容持久化：小结果内联，大结果写 Blob，并记录 resultHash。
- [√] 只有终态且结果内容完整时才允许在恢复中直接复用。
- [√] Pending 可安全发送；Sent 必须先 `tool.result.query(callId)`，不得直接重发有副作用调用。

### 4.8 实现宿主/MCP 反向 RPC : 100%

- [√] 无有效 Grant 时先发送 `tool.permission.request`。
- [√] Granted 后发送 `tool.call.request`；宿主执行实际 Host/MCP 工具并返回 `tool.call.response`。
- [√] 每个反向请求使用 correlationId、callId、超时和取消关联。
- [√] 宿主重连后可按 callId 查询已执行结果，Unknown 交由宿主策略决定失败、重发或人工介入。
- [√] 非幂等操作（即已有副作用的工具调用）在 Unknown 状态下不得自动重发；宿主执行器必须对 callId 持久化去重，Unable-to-determine 时只允许走人工介入流程。
- [√] Runtime 不持有宿主业务密钥，也不直接加载宿主 MCP 插件程序集。
- [√] 内置工具和宿主工具共享状态、事件、Grant、审批、大小限制和审计语义。

### 4.9 实现审批、委派和撤销 : 100%

- [√] `NeedsUserApproval` 返回稳定 `approvalRequestId`，Run 进入 WaitingForApproval。
- [√] 审批响应可以拒绝或返回新的 Grant；拒绝后工具调用形成明确失败结果。
- [√] 权限请求记录实际 agentId 和 parentAgentId，不能只记录最外层总经理。
- [√] 子智能体默认不继承权限；只有 `allowDelegation=true` 且 agent/作用域被允许时才能继承。
- [√] 委派后的工具、路径、网络、程序、有效期和风险不得超过父 Grant。
- [√] 保存 grantId、rootGrantId 和完整委派链。
- [√] `grant.revoke` 默认撤销后代；未发送调用立即失败，已发送调用返回后不得继续用于推理，已完成记录保留。

### 4.10 建立工具审计 : 100%

- [√] 审计记录包含调用方、有效 Grant、规范化目标摘要、参数哈希、审批、结果、耗时和 diagnosticId。
- [√] 默认不记录完整文件内容、Prompt、脚本、凭据和敏感工具结果。
- [√] 审计写入失败时，高风险操作不得继续执行。
- [√] 为读取敏感文件、覆盖、移动、删除、进程、PowerShell 和网络建立明确风险等级。

## 5. 测试要求 : 55.6%

- [√] 工具目录更新在安全点生效，进行中的 Invocation 不被新版本污染。
- [√] 同一 callId 在正常、超时、响应丢失、重连和 Runtime 重启场景最多执行一次。
- [×] 路径测试覆盖 `..`、大小写、尾分隔符、符号链接、联接点、UNC、设备路径、ADS 和竞态替换。
- [√] `.madorin` 无法通过直接路径或链接绕过保护。
- [√] `builtin.memory.append` 未经审批不能写入，且普通文件工具不能模拟该操作。
- [×] Process/PowerShell 覆盖参数注入、环境泄漏、stdout/stderr 填满、超时和进程树清理。
- [×] HTTP 覆盖 DNS rebinding、重定向、保留地址、超大响应和取消。
- [√] 子智能体默认越权失败；显式委派后仍不能扩大任何权限维度。
- [×] Grant 撤销和审批拒绝在所有等待阶段行为确定。

## 6. 完成标准 : 60%

- [×] 真实 Provider 可连续完成权限申请、工具调用、结果续调和最终回答。
- [√] 内置与宿主/MCP 工具均通过统一网关和审计。
- [√] 故障注入后不重复执行有副作用工具。
- [×] 文件、进程、PowerShell、HTTP 和保留目录安全测试全部通过。
- [√] 权限链可追溯到真实 Agent、父 Agent、Grant 和委派链。

## 7. 风险与禁止事项

- 不得在工具调用发送后才首次记录 callId。
- 不得把模型工具调用视为已授权。
- 不得让子智能体隐式继承总经理权限。
- 不得用 WorkingDirectory、字符串前缀或路径规范化副本冒充 OS 沙箱。
- 不得让 Runtime 直接执行宿主/MCP 工具或读取宿主业务密钥。
