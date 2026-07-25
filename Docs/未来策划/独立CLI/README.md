# 独立 AI CLI / Runtime 总体方案与总需求

> 文档状态：总体策划
>
> 适用范围：全新、独立建设的 AI CLI / Runtime 项目
>
> **文档体系与优先级**：
> - 本文（README）：总体方案与核心需求，不包含实现级细节
> - **方案设计**
>   - [01-实施方案-V1](./方案设计/01-实施方案-V1.md)：V1 实施范围、契约草案、阶段和验收矩阵
>   - [02-架构修订-V1](./方案设计/02-架构修订-V1.md)：经评审修订的协议决策，**与 01 冲突时以 02 为准**
>   - [03-实现框架参考](./方案设计/03-实现框架参考.md)：C# 实现时的 MAF/MEAI 使用规范
>   - [05-Skills加载与调用方案](./方案设计/05-Skills加载与调用方案.md)：Skills 来源、合并、MAF 注入和工具权限边界
> - **命令规范**
>   - [04-CLI命令规范](./命令规范/04-CLI命令规范.md)：所有 CLI 命令定义
>   - [09-CLI命令矩阵](./命令规范/09-CLI命令矩阵.md)：46 条命令路径、参数、默认值、锁、退出码和快照契约
> - **使用指南**
>   - [06-安装与CLI使用](./使用指南/06-安装与CLI使用.md)：PATH 安装、首次配置、单次运行和单行 REPL 使用指南
>   - [07-宿主集成指南](./使用指南/07-宿主集成指南.md)：Client SDK 生命周期、事件确认、回调、恢复和升级指南
>   - [08-安全与数据运维](./使用指南/08-安全与数据运维.md)：权限边界、数据库、Blob、备份和故障处置手册
> - [待评审策划](./策划/)
> - [执行步骤](./执行步骤/README.md)
> - [备档](./备档/README.md)：已完成阶段的执行计划、执行步骤和历史批次材料
>
> **实施前必读**：02-架构修订-V1 包含对 01 的 8 项阻断级修订，开发人员不应直接按 01 实施。

## 1. 项目定位

建设一个不依赖 Madorin 内部程序集、可以独立安装、独立运行、独立升级的 AI Runtime 产品，并通过 CLI 提供启动、单次执行、诊断和管理入口。

主程序不直接集成各家 AI SDK，也不负责 Agent 的实际执行。主程序负责业务任务调度、用户交互、权限审批、宿主工具和业务投影；AI Runtime 负责模型访问、内容处理、Agent/Workflow 运行、流式解析，以及对话历史和可恢复执行状态的权威持久化。

本方案不是在当前项目中修补或搬迁 `Netor.Cortana.AI`，也不以兼容现有内部类型作为设计前提。新项目首先建立稳定的公开协议和 Client SDK，当前主程序只是它的一个调用方。

## 2. 核心结论

1. 产品形态应是“带 CLI 入口的常驻 AI Runtime”，而不是只运行一次、打印文本后退出的传统命令行程序。
2. 主程序和 Runtime 之间通过版本化、强约束、可恢复的双向协议通信，不解析面向人的控制台文本。
3. 主程序负责全局任务调度，Runtime 负责被分配任务的执行状态和资源约束，双方不重复拥有同一状态的最终解释权。
4. Provider 采用“统一核心能力 + 能力声明 + Provider 扩展”的适配模型，不承诺用一个最低公共接口抹平所有厂商差异。
5. MCP 和宿主工具通过反向 RPC 调用。Runtime 可以动态获得工具目录，但默认不持有宿主权限和业务密钥。
6. 多任务、取消、断线恢复、事件重放、工具幂等和版本热切换必须从协议第一版开始设计，不能作为后续补丁。
7. 专家、会议和工作模式属于 Runtime 的正式执行能力；宿主传入模式和配置并负责调度，Runtime 负责模式内部的 Agent 构建和执行。
8. 对话记录由 Runtime 保存到工作区的 `.madorin` 数据区；宿主通过 Client SDK 查询和恢复，不把物理目录布局当作公开协议。
9. Skills 由 Runtime 通过 MAF 加载：工作区来源自动发现，宿主通过有序数组传入额外目录和远程地址；宿主是来源信任权威，Skill 引用的普通工具仍走既有反向 RPC。

## 3. 建设目标

### 3.1 功能目标

- 支持主程序同时调度多个独立任务和会话。
- 支持文本、推理过程、工具调用、用量和状态的实时输出。
- 支持 OpenAI、Anthropic、Gemini、OpenAI Compatible 及自定义 AI API 的渐进接入。
- 支持运行时动态更新 MCP/工具目录，并由宿主完成真实工具执行。
- 支持用户审批、人机协作和需要确认的高风险动作。
- 支持单 Agent、多 Agent、Workflow 和后续自定义编排引擎。
- 支持独立配置目录、工作目录、数据目录和日志目录。
- 支持 Runtime 独立升级、并行版本运行和故障回退。

### 3.2 架构目标

- 主程序与 Runtime 只依赖公开协议，不共享内部业务程序集。
- Client SDK 隐藏进程启动、连接、重连、事件排序和协议细节。
- 同一个 Runtime 可被桌面程序、服务程序、测试程序或其他 CLI 调用。
- Provider、工具协议和传输层均可扩展，但不破坏稳定的任务模型。
- Runtime 内部可以在不改变宿主协议的情况下切换隔离级别。

### 3.3 非目标

- 不以复刻 Claude CLI 或 Codex CLI 的命令、配置和交互行为为目标。
- 不依赖解析第三方 CLI 的 stdout、终端动画或私有配置目录。
- 不要求第一版一次性实现所有 AI 厂商和所有多模态能力。
- 不允许宿主直接获取 Runtime 内部 C# 对象、DI Scope 或 SDK Session 对象。
- 不把进程隔离等同于安全沙箱；严格沙箱属于额外的操作系统安全能力。

## 4. 总体架构

```text
主程序
├── 业务任务调度器
├── AI Runtime Client SDK
├── MCP/插件/宿主工具
├── 用户审批与权限控制
└── 业务状态及会话投影
                 │
                 │ 本地双向 IPC
                 ▼
独立 AI Runtime
├── CLI 入口
├── Runtime Server
├── Run / Session Manager
├── Conversation Store（.madorin）
├── Content Pipeline
├── Agent / Workflow Engine
├── Remote Tool Gateway
└── Provider Adapters
    ├── OpenAI
    ├── Anthropic
    ├── Gemini
    ├── OpenAI Compatible
    └── Custom Provider
```

### 4.1 主程序职责

- 创建业务任务，决定排队、优先级、暂停、取消、重试和任务分配。
- 启动或连接指定版本的 Runtime，并执行健康检查。
- 注册当前可用的宿主工具和 MCP 工具。
- 执行工具权限校验、用户审批、安全审计和真实调用。
- 接收、排序并展示 Runtime 事件，可按业务需要保存只读投影和恢复游标。
- 保存业务任务的排队、优先级、关联单据等宿主业务状态，不重复写入权威对话历史。
- 决定 Runtime 失联后的继续等待、重新连接、重新执行或终止策略。

### 4.2 AI Runtime 职责

- 验证任务请求及 Provider 能力。
- 组装内容、模型参数、工具描述和 Agent 上下文。
- 管理 Run、Session、模型调用和 Workflow 生命周期。
- 持久化对话消息、模式快照、Run、工具审计、附件引用和恢复游标。
- 发起流式模型调用并归一化输出事件。
- 识别工具调用并向宿主发起反向 RPC。
- 管理并发上限、Provider 限流和运行资源。
- 生成可恢复事件、检查点、错误和用量记录。
- 提供健康检查、能力查询和协议协商。

## 5. 产品入口

Runtime 至少提供以下命令形态：

```text
ai-runtime serve       启动常驻 Runtime Server
ai-runtime run         执行一次独立任务
ai-runtime doctor      检查配置、Provider 和运行环境
ai-runtime providers   查看 Provider 及能力
ai-runtime version     查看程序和协议版本
```

`serve` 是主程序集成入口；`run` 主要用于自动化、调试和无宿主场景。面向人的命令输出与机器协议严格分离。

## 6. 通信与协议总要求

### 6.1 传输原则

- 本地 Windows 第一传输为 Named Pipe。
- 协议层不绑定 Named Pipe，后续可以增加 Unix Domain Socket 或 gRPC/TCP。
- 控制/RPC 通道与高频事件通道逻辑分离，避免输出洪峰阻塞取消和心跳。
- stdout 不作为常驻模式的业务数据通道；stderr 只用于启动期诊断和兜底日志。
- 图片、音频、大文件和超大工具结果使用受控 Blob 引用，不塞入普通事件帧。

### 6.2 通道模型

```text
Control Channel
  双向请求/响应：初始化、启动、取消、状态查询、工具调用、审批、心跳

Event Channel
  Runtime -> Host：按 Run 排序的流式事件，支持游标确认和断线重放

Blob Channel / Store
  双方交换大内容：路径或流句柄、长度、SHA-256、租约和访问范围
```

### 6.3 消息信封

所有协议消息至少包含：

```text
protocolVersion
messageId
correlationId
hostInstanceId
runtimeInstanceId
runId
sessionId
sequence
timestamp
messageType
payload
```

并非所有消息都必须具有 `runId` 或 `sessionId`，但字段语义和缺省规则必须固定。

### 6.4 核心命令与事件

```text
initialize / initialized
runtime.getCapabilities
runtime.shutdown

run.start / run.accepted
run.cancel / run.cancelAccepted
run.query
run.completed
run.failed
run.cancelled

session.selection.update
session.selection.get
session.new
session.list
session.get
session.resume
session.messages.list

output.delta
reasoning.delta
progress.changed
usage.updated
checkpoint.created
turn.execution.resolved
selection.updated

tool.catalog.replace
tool.catalog.patch
tool.permission.request
tool.permission.response
tool.call.request
tool.call.response

approval.request
approval.response

events.acknowledge
events.replay
heartbeat
```

### 6.5 交付语义

- 命令使用 `messageId` 和业务幂等键去重。
- 事件通道引入**全局单调序号（GSN）**：Runtime 实例内所有 Run 的事件共享一条递增 GSN，每个事件帧同时携带 `gsn`（全局序）和 `runSequence`（Run 内序）。`gsn` 用于游标确认和断线重放，`runSequence` 用于 Run 内顺序校验。两个值均不跳号——Delta 丢弃时发出占位事件 `delta.dropped`，以正常 GSN 填充，标明被丢弃的 `runSequence` 范围。
- 宿主确认水位：`events.acknowledge(lastConfirmedGsn)` 覆盖所有 Run，Runtime 释放 `gsn ≤ lastConfirmedGsn` 的缓冲；不同 Run 无法独立确认，避免多游标管理复杂性。
- 系统以”至少送达一次 + 幂等去重”为基础，不虚假承诺跨进程 exactly-once。
- 每个 Run 必须且只能形成一个可确认的终态：Completed、Failed 或 Cancelled。

### 6.6 数据格式与字符串无损传递

进程间通信必须传递结构化对象，禁止通过拼接命令行、按空格拆分或自定义分隔符传递业务数据。

以下内容必须逐字符或逐字节保持原始语义：

- 包含空格、中文、括号、引号和长文件名的 Windows/Linux 路径。
- URL 中的协议、端口、路径、查询参数、片段和百分号编码。
- URL 中的 `#`、`?`、`&`、`+`、`%`、`=`、空格和前导数字。
- Prompt、代码、JSON、Markdown、换行符和首尾空白。
- 大整数 ID、十进制数、时间、枚举和空值。

协议统一遵循以下规则：

- JSON 文本使用 UTF-8；大小限制按 UTF-8 字节数计算，不按字符数计算。
- 路径、URL、命令和参数分别放在独立字段中，不放入待二次解析的组合字符串。
- 启动子进程时使用参数数组，不把参数拼成一条 Shell 命令。
- 字符串默认不执行 `Trim`、按空格切分、URL Decode、路径替换或 Unicode 归一化。
- URL 同时允许保存原始值和解析结果；转发时使用原始值，安全判断时使用解析后的结构，禁止重复编码或重复解码。
- 文件路径的规范化副本只用于权限判断，不覆盖调用方传入的原始显示值。
- 跨语言可能溢出的 ID 和高精度数字使用字符串或明确的十进制表示，不依赖 JSON 浮点数。
- 时间统一使用带时区的 ISO 8601/UTC 表示，枚举在公开协议中使用稳定字符串值。
- 文本内容显式声明编码；无法可靠识别时按二进制 Blob 处理，不擅自转换。

例如，下列值都必须作为一个完整字段传递，不能被命令行或 URL 解析过程改变：

```text
E:\Projects\示例 项目\01 data\input file.json
https://example.com/a%20b/01?q=a+b&next=%2Ffolder%2Ffile#part-1
```

### 6.7 数据大小分级

协议按数据规模选择传输方式：

| 数据级别 | 传输方式 | 典型内容 |
| --- | --- | --- |
| 小型结构化数据 | Control/Event 消息内联 | 命令、状态、短文本、工具参数 |
| 中型连续内容 | Event 分片或 Blob 流 | 长回答、较大工具结果、长文本 |
| 大型或二进制内容 | Blob Channel/Store | 文件、图片、音频、归档、数据库 |

连接初始化时双方协商以下限制，禁止在 Client SDK 中散落硬编码值：

```text
maxControlMessageBytes
maxEventFrameBytes
maxInlineContentBytes
maxBlobBytes
maxBlobBytesPerRun
blobChunkBytes
```

超过协商阈值的数据必须自动切换为 Blob，不允许静默截断，也不建议把大型二进制 Base64 后塞进 JSON。Base64 只允许用于阈值以内的小型二进制内容。

### 6.8 大型文件和 Blob 协议

Blob 描述符至少包含：

```text
blobId
contentType
length
sha256
transferMode
fileName
textEncoding
locator
expiresAt
accessMode
```

V1 允许两种受控传输模式：

1. Blob Stream：通过独立流分块传输，具有背压、超时、取消、长度和哈希校验。
2. Managed Staging File：使用双方协商的临时交换目录，文件写入完成并校验后再原子发布。

大型文件传输必须满足：

- 长度使用 64 位整数，不能以一次性内存分配作为前提。
- 接收方同时校验声明长度和 SHA-256，校验失败不得交给模型或工具。
- 临时文件具有租约、所有者、访问模式和清理规则。
- 只允许引用协商的 Blob/交换目录，不能用任意宿主绝对路径冒充已授权文件。
- 支持流式读取和背压，发送方不能无限占用接收方内存。
- 取消或断线时清理未完成文件；需要续传时使用 `blobId + offset`，不能重新解释文件名。
- 大文件进入 Runtime 不代表整文件进入模型上下文；内容处理层应按范围、分页、检索或摘要读取。
- URL 指向的大文件默认由拥有网络权限的一方下载并转换为 Blob，Runtime 不因收到 URL 自动获得网络访问权。

## 7. 任务、Run 与 Session 模型

### 7.1 概念边界

- Task：宿主业务任务，由主程序创建和调度。
- Run/Turn：Session 内的一次流式执行，有唯一 `runId`；Run 结束不代表对话结束。
- Session：用户可见的连续对话及其规范化历史，有唯一 `sessionId`；切换 Provider、模型、Agent 或提示词不得改变 `sessionId`。
- Attempt：同一个业务任务的一次执行尝试，用于重试和故障转移。
- Tool Call：Run 内的一次工具调用，有唯一且稳定的 `callId`。

### 7.2 状态机

```text
Accepted
  -> Preparing
  -> Running
  -> WaitingForTool
  -> WaitingForApproval
  -> WaitingForCredentials   Provider 凭据过期/401，等待宿主推送续期凭据
  -> Running                 （凭据续期后恢复）
  -> Persisting
  -> Completed | Failed | Cancelled

崩溃后重建（仅 Runtime 重启时批量设置，正常流程不产生）：
  -> Interrupted             无法确定终态的 Run，宿主可按幂等策略决定重试或放弃
```

状态可以按合法路径跳转，但不能从终态返回运行态。`WaitingForCredentials` 只能由 Runtime 在收到 Provider `401` 或凭据接近过期时触发，不由宿主直接设置。进度默认表达为阶段和已完成工作量，不伪造无法证明的百分比。

### 7.3 状态权威

| 状态范围 | 权威方 |
| --- | --- |
| 业务排队、优先级、任务分配 | 主程序 |
| Run 当前执行阶段 | Runtime |
| Provider 调用与工具等待 | Runtime |
| 工具权限和用户审批 | 主程序 |
| Runtime Session 活状态 | Runtime |
| 对话历史、模式快照和可恢复检查点 | Runtime |
| 业务最终记录和展示状态 | 主程序 |
| 事件确认游标 | 双方按协议维护 |

### 7.4 同一对话内实时切换执行配置

同一个 Session 必须支持在相邻对话轮次之间直接切换：

- AI Provider 和凭据配置 Profile。
- 具体模型及模型参数。
- Agent 定义、角色、提示词模板和能力配置。
- 系统提示词、Session 指令和下一轮临时指令。
- 工具目录版本、内容处理策略和上下文预算。

“切换”只替换下一轮的执行配置，不新建、不清空、不恢复其他 Session，也不要求用户重新开始对话。以下状态必须保留：

```text
sessionId
workspaceId
规范化消息历史
附件及内容引用
记忆引用
用户可见的对话位置
```

切换后允许重新创建以下内部执行对象：

```text
Provider Client
Model Client
AIAgent 执行实例
解析后的提示词
模型能力与参数
本轮工具上下文
```

内部执行对象被重建不等于对话或 Session 被重启。这个区别属于公开协议的强制行为。

### 7.5 Next-Turn Selection 模型

Runtime 为每个 Session 保存版本化的 `NextTurnSelection`：

```text
selectionVersion
mode
defaultSelection
  providerId
  providerProfileId
  modelId
modeSelection
  Expert: agent
  Meeting: participantSetVersion + participants[] + hostAgent? + meetingPolicy
  Work: generalManagerAgent + availableAgents[] + workflowPolicy
sessionInstructions
generationOptions
toolCatalogVersion
contentPolicyVersion
```

宿主通过 `session.selection.update` 随时提交新选择，包括当前流仍在输出期间。Runtime 验证并保存后立即返回 `selection.updated`，但不干扰正在执行的流。

```text
Turn A 正在流式输出，使用 Selection v12
Host -> session.selection.update(v13)
Runtime -> selection.updated(v13, effective=NextTurn)
Turn A 正常结束
Turn B 在同一 sessionId 中启动，使用 Selection v13
```

如果流式输出期间连续更新多次，以最后一个通过版本检查的 Selection 为下一轮配置。更新使用 `expectedSelectionVersion` 做乐观并发控制，避免多个界面或任务互相覆盖。

下一执行段必须在真正开始时读取最新 Selection，而不是在消息进入宿主队列时提前绑定。专家模式的执行段是下一次用户 Turn；会议模式是当前发言流结束后的下一位发言者/主持人调用；工作模式是当前模型流结束后尚未开始的下一步骤或 Agent 调用。已经运行的会议发言或后台子智能体继续使用原快照，不被中途替换。

一次性变化可以放在下一轮的 `TurnOverride` 中，仅覆盖一个 Turn；持续变化写入 `NextTurnSelection`，后续轮次一直生效，直到再次修改。两种方式都不创建新 Session。

### 7.6 Turn 执行快照与生效边界

每个 Turn 开始时生成不可变的 `TurnExecutionSnapshot`：

```text
selectionVersion
mode
modeConfigurationVersion
providerId
providerProfileId
modelId
agentId
parentAgentId
promptTemplateId
promptTemplateVersion
resolvedPromptHash
generationOptions
toolCatalogVersion
contentPolicyVersion
```

不可变快照只用于保证当前流不会在输出中途混入另一厂商、模型或提示词，不会冻结整个 Session。当前流结束后，下一轮立即解析最新 Selection。

- 已发送到 Provider 的当前流式请求保持原配置并正常完成。
- 配置切换默认不取消当前流，也不需要创建新 Attempt。
- 下一轮在原 `sessionId`、原对话历史上重建执行 Agent 并立即使用新配置。
- Provider、模型或 Agent 切换后重新计算能力、上下文窗口、提示词、工具格式和内容类型。
- 对于一次 Turn 内部由工具调用触发的模型续调，默认属于同一 Turn 快照；主程序看到该 Turn 完成后，新 Selection 才用于下一轮用户对话。
- 只有用户明确要求终止当前输出时才取消当前 Turn；取消不是切换配置的必要条件。

会议或工作模式更新 Agent 集合时也遵守相同原则：当前模型流和已启动后台作业保持旧快照；更新后的参与者集合、总经理或可用子智能体从下一个尚未开始的模式安全点生效，并产生 `selection.updated` 和新的 `modeConfigurationVersion`。

跨 Provider 延续由统一 AI 框架和规范化消息历史保证。厂商私有的 `responseId`、缓存句柄或 SDK 对象不能成为对话连续性的唯一来源；不能直接复用时，由对应 Provider Adapter 在同一 Session 上重新组装请求。若新模型上下文窗口更小，裁剪、摘要或内容降级必须产生明确事件，不能静默丢失历史。

### 7.7 现有项目行为基线

独立 Runtime 的体验不得弱于当前 `Netor.Cortana.AI`：

- [`ChatAgentResolver.ChangeProvider/ChangeModel/ChangeAgent`](../../../Src/Netor.Cortana.AI/Agents/ChatAgentResolver.cs#L52) 只更新下一轮默认选择，不清空当前 Turn 的 Agent/Session。
- [`ChatAgentResolver.BuildAgentForTurn`](../../../Src/Netor.Cortana.AI/Agents/ChatAgentResolver.cs#L99) 在下一轮检测选择或工具版本变化，必要时重建 AIAgent。
- [`AiChatHostedService.SendMessageAsync`](../../../Src/Netor.Cortana.AI/Chat/Runtime/AiChatHostedService.cs#L184) 在每次发送前读取当前选择并准备本轮执行。
- [`ChatSessionService.EnsureCurrentSessionAsync`](../../../Src/Netor.Cortana.AI/Chat/Runtime/ChatSessionService.cs#L219) 在 Agent/Provider/Model 变化后继续复用 `_currentSession`，只更新选择状态。
- [`AIAgentFactory.Build`](../../../Src/Netor.Cortana.AI/Agents/AIAgentFactory.cs#L143) 使用新的 Driver、Model、Agent ChatOptions 和工具上下文构建下一轮执行 Agent。

独立协议必须完整表达这条链路：`更新下一轮选择 -> 当前流正常结束 -> 重建执行 Agent -> 复用原 Session -> 下一轮立即生效`。

### 7.8 提示词分层

提示词使用结构化分层，不把所有内容无边界拼成一个字符串：

```text
Runtime/Core Policy        不可由模型覆盖的运行与安全约束
Host Policy                宿主业务规则和权限说明
Agent Prompt               Agent 角色、目标和工作方式
Session Instructions       当前会话持续指令
Run Instructions           仅本轮有效的临时指令
```

每一层具有来源、版本、优先级、是否可覆盖和内容哈希。Runtime 应能够返回解析后的提示词清单；敏感内容可以只返回元数据和哈希，不必回传正文。

### 7.9 专家、会议和工作模式

三种业务运行模式使用同一个 `run.start` 入口，通过 `mode` 和模式专属配置组成鉴别联合。宿主负责选择模式和调度任务，Runtime 负责验证配置、构建模式内部 Agent 并执行；不能把会议或工作模式重新拆回宿主进程执行。

```text
ExecutionStartRequest
├── taskId
├── sessionId
├── workspaceRoot
├── mode
├── defaultSelection
│   ├── providerId
│   ├── providerProfileId
│   └── modelId
├── initialInput
├── attachments
├── selectionVersion
└── modeOptions
    ├── ExpertOptions
    │   └── agent
    ├── MeetingOptions
    │   ├── participants[]
    │   ├── hostAgent（可选，缺省时使用 Runtime 内建主持人）
    │   ├── selectorPolicy
    │   └── meetingPolicy
    └── WorkOptions
        ├── generalManagerAgent
        ├── availableAgents[]
        ├── workflowPolicy
        └── toolPermissionPolicy
```

`AgentRef` 至少包含稳定 `agentId`、提示词模板版本和能力声明，并允许覆盖公共的 Provider、模型和生成参数；未覆盖时继承 `defaultSelection`。

模式语义如下：

| 运行模式 | 启动输入 | Runtime 内部职责 |
| --- | --- | --- |
| Expert | Provider、模型、单个 Agent、用户输入 | 构建单 Agent，按 Session 连续执行多个 Turn |
| Meeting | Provider、模型、`participants[]`、会议主题与策略 | 构建参会 Agent、主持人和内部发言选择器，维护会议轮次、总结、取消和 HITL |
| Work | Provider、模型、总经理 Agent、可用子智能体数组与工作策略 | 运行总经理、计划/步骤、项目负责人和子智能体，统一处理工具权限、后台作业和审计 |

V1 中一个 Session 的 `mode` 创建后不可隐式改变。Provider、模型、Agent 和提示词仍按 Next-Turn Selection 在同一 Session 的下一轮生效；从专家切换到会议或工作属于显式新建或模式移交，不能用普通 Selection 更新偷换历史语义。

当前源码已经证明这些边界可落地：会议模式的 [`MeetingAgentBuilder.BuildParticipantAgentsAsync`](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs#L211) 接收参与者集合并为每位参与者解析 Provider/模型；工作模式的 [`GeneralManagerAgentBuilder.BuildAsync`](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs#L73) 构建总经理专用工具集，并隔离 Agent 自带工具。独立 Runtime 应保留行为能力，但公共协议不复用这些内部 C# 类型。

## 8. 多任务与隔离要求

Runtime 对外必须支持多个并发 Run，并保证：

- 每个 Run 具有独立取消、超时、输出序号、错误和工具调用空间。
- 同一 Session 必须跨多个 Run 和不同执行 Agent 延续；只有显式 `session.new` 才创建新对话。
- 不同 Session 之间的消息历史、Selection、Agent 执行状态和工具调用不得互相复用。
- 一个 Run 的失败不得直接终止其他 Run。
- Provider 级限流和全局资源限额不得破坏任务级状态隔离。
- 主程序可以查询当前 Run、资源占用和队列情况。

Runtime 内部预留三种隔离级别。它们与 Expert/Meeting/Work 业务运行模式是两个正交维度：

| 隔离级别 | 说明 | 适用场景 |
| --- | --- | --- |
| Scoped | 同一进程内按 Run Scope 隔离 | 默认、高吞吐 |
| PerSession | 每个 Session 使用独立 Worker | 长会话、状态敏感 |
| PerRun | 每个 Run 使用独立 Worker | 高风险任务、强故障隔离 |

隔离级别是 Runtime 内部策略，不能改变宿主协议或业务运行模式语义。

## 9. Provider 兼容模型

### 9.1 兼容原则

“兼容所有 API”定义为可以持续增加 Provider Adapter，而不是宣称所有厂商行为完全一致。

统一模型只覆盖稳定的核心概念：

- 消息和多类型内容。
- 模型参数。
- 流式输出。
- 推理过程。
- 工具定义和工具调用。
- 结构化输出。
- 用量、完成原因和错误。

每个 Provider 必须声明能力，例如：

```text
Streaming
ToolCalling
Vision
Audio
StructuredOutput
Reasoning
PromptCaching
Files
ComputerUse
Embeddings
```

统一事件不能表达的新能力，通过命名空间化扩展字段和可选原始事件保留。宿主可以只依赖统一能力，也可以显式选择 Provider 扩展。

### 9.2 Provider 插件要求

- Provider Adapter 不得直接依赖主程序。
- Adapter 必须报告版本、能力、模型目录和配置 Schema。
- Adapter 负责厂商协议与统一协议之间的转换。
- Adapter 必须正确传播取消、超时、限流、用量和原始错误码。
- Adapter 的升级不得修改稳定的 Runtime 协议。

## 10. MCP、工具和审批

### 10.1 工具目录

宿主向 Runtime 发布版本化工具目录。目录包含工具标识、显示名称、描述、参数 Schema、结果 Schema、风险等级、超时和能力标签。

工具目录更新遵循以下规则：

- 新目录在下一次模型请求或 Workflow 安全点生效。
- 已发出的模型请求继续使用发起时的目录版本。
- 已开始的工具调用使用原 `callId` 完成或返回明确错误。
- Runtime 不缓存超出 Session/Run 生命周期的宿主授权。

### 10.2 反向工具调用

```text
Runtime -> tool.permission.request（无有效 Grant 时）
Host    -> tool.permission.response
Runtime -> tool.call.request
Host    -> 可选 approval.request
Host    -> 执行 MCP/插件/业务工具
Host    -> tool.call.response
Runtime -> 继续模型或 Workflow
```

有副作用的工具必须使用幂等键。断线后宿主应能按 `callId` 查询已执行结果，避免重复付款、下单、发消息或删除数据。

### 10.3 工作模式权限申请与子智能体

工作模式不把“模型发出了工具调用”视为已经获得权限。需要宿主授权的调用必须先完成独立权限握手，再进入真实工具调用：

```text
总经理或子智能体 -> Runtime Tool Gateway
Runtime -> tool.permission.request
Host    -> tool.permission.response（Granted / Denied / NeedsUserApproval）
Granted -> builtin.*：Runtime 在授权范围内执行
Granted -> 宿主/MCP 工具：Runtime -> tool.call.request -> Host -> tool.call.response
Runtime -> 恢复原 Agent / Workflow
```

权限请求至少包含：

```text
taskId
sessionId
runId
mode
agentId
parentAgentId
toolId
callId
argumentsHash
risk
requestedScope
reason
```

其中 `agentId` 是实际发起调用的智能体，`parentAgentId` 表示总经理或上级智能体，不能只记录最外层总经理。Runtime 在等待宿主期间进入 `WaitingForApproval`，并持续支持状态查询、取消、超时和断线恢复。

子智能体权限遵循以下规则：

- 默认不继承总经理、项目负责人或其他 Agent 的工具权限，每个需要授权的调用单独申请。
- Grant 只有同时声明 `allowDelegation=true`、允许的子智能体和可委派作用域时，才允许子智能体继承。
- 子智能体有效权限始终是宿主 Grant、父级可委派范围、Run 工具目录和 Runtime 安全策略的交集。
- 子智能体不得扩大根目录、网络目标、可执行程序、有效期或风险等级；继续委派必须再次显式允许。
- 所有继承调用记录 `grantId`、`rootGrantId`、委派链和参数哈希，宿主可以撤销后阻止尚未开始的调用。

## 11. 配置、基础工具和权限隔离

### 11.1 配置隔离

- Runtime 必须通过参数显式接收配置目录、数据目录和日志目录。宿主集成的 `serve` 模式还必须由 `run.start` 或启动参数显式提供工作区。
- 用户直接执行 `ai-runtime run` 且未传 `--workspace` 时，可以把启动时的当前目录解析为绝对 `workspaceRoot` 并固定到本次 Run；之后不能随进程目录变化重新解释。
- 默认不扫描当前用户的 Claude、Codex 或其他第三方 AI 配置目录。
- 默认不向上查找项目配置，也不隐式继承宿主工作目录。
- 敏感凭据使用独立密钥存储、受控环境变量或短期凭据注入。
- Named Pipe 使用当前用户或指定服务账户 ACL，并在初始化时验证一次性令牌。
- 工具调用必须携带来源、Run、Session、模型、参数摘要和审批记录。

### 11.2 CLI 内置基础工具

独立 Runtime 应提供一组不依赖宿主的基础工具。工具使用固定命名空间，避免与宿主和 MCP 工具重名：

| 工具组 | 基础能力 |
| --- | --- |
| `builtin.fs` | 列表、状态、读文本、读 Blob、写文本、写 Blob、建目录、复制、移动、删除、搜索、补丁 |
| `builtin.process` | 运行允许的可执行程序、参数数组、环境变量白名单、超时、退出码、stdout/stderr |
| `builtin.powershell` | 在显式授权的安全模式下运行 PowerShell |
| `builtin.http` | 在显式网络策略下执行受限 HTTP 请求和下载 Blob |
| `builtin.content` | 编码识别、分页读取、摘要前处理、哈希和内容类型识别 |

内置工具和宿主工具使用同一套工具事件、审批、超时、取消、结果大小和审计模型。模型只能看到当前 Run 已授权的工具。

### 11.3 默认工作区权限

每个 Run 必须具有显式 `workspaceRoot`。默认权限配置为：

```text
工作区内读取             允许
工作区内新建和写入       允许
覆盖、移动和删除         需要单独权限或审批
工作区外读取和写入       拒绝
任意进程执行             拒绝
PowerShell               拒绝
工具任意网络访问         拒绝
Provider 指定端点访问    按 Provider 配置允许
```

宿主可以按 Run、Session 或单次 Tool Call 下发权限 Grant。Grant 至少包含：

```text
grantId
runId
workspaceRoot
allowedReadRoots
allowedWriteRoots
allowOverwrite
allowMove
allowDelete
allowedExecutables
allowPowerShell
networkPolicy
expiresAt
allowDelegation
delegatedAgentIds
```

Runtime 只能收紧宿主 Grant，不能自行扩大权限。多个策略同时存在时采用最小权限交集。

### 11.4 路径边界校验

所有文件操作必须在实际打开文件前完成权限校验：

- 相对路径以 `workspaceRoot` 为唯一基准，不依赖 Runtime 当前目录。
- 绝对路径只有位于允许根目录内才可使用。
- 分别保留原始路径和用于校验的规范化绝对路径。
- 拒绝通过 `..`、UNC、设备路径、备用数据流或其他平台特殊路径绕过根目录。
- 解析符号链接、目录联接点和 Reparse Point 后再次校验实际目标，防止从工作区跳出。
- 路径比较遵循当前文件系统的大小写和分隔符规则，不能用普通字符串前缀判断父子目录。
- 复制、移动和写入同时校验源目录、目标目录及最终父目录。
- 校验失败返回结构化权限错误，不自动尝试其他目录。

文件内容和路径是不同的数据：读取路径中包含空格、中文或特殊字符的文件时，不经过 Shell，也不对路径进行引号拼接。

### 11.5 Process 与 PowerShell 边界

仅设置 `WorkingDirectory` 不能限制 PowerShell 或普通子进程访问其他目录。只要子进程继承了当前用户权限，它就可能绕过 Runtime 的文件工具直接读取工作区之外的数据。

因此定义以下执行级别：

| 模式 | 行为 |
| --- | --- |
| Restricted | 默认模式，只开放受限内置文件工具，不开放任意进程和 PowerShell |
| AllowlistedProcess | 只允许运行指定程序，参数数组、工作目录和环境变量受控 |
| SandboxedShell | 在低权限账户、AppContainer、Windows Sandbox 或容器中运行 Shell |
| UnrestrictedShell | 使用当前用户权限运行，必须显式授权并明确提示不具备工作区隔离 |

PowerShell 工具还必须满足：

- 默认关闭，不能因为模型请求而自动开启。
- 使用 `-NoProfile`、`-NonInteractive`，不加载用户 Profile 和启动脚本。
- 脚本内容通过标准输入或受控脚本文件传递，不拼接到命令行。
- 参数使用参数数组，环境变量采用白名单，不继承无关密钥。
- stdout 和 stderr 并发读取，具有字节上限、截断标记、超时和取消。
- 使用进程树回收机制，取消后不能遗留子进程。
- 只有 `SandboxedShell` 可以宣称操作系统级工作区隔离；`UnrestrictedShell` 不能作此声明。

### 11.6 权限审计

- 每次工具调用记录有效 Grant、规范化目标、审批、调用方、结果和诊断 ID。
- 读取敏感文件、覆盖、移动、删除、执行程序和网络访问属于可审计动作。
- 日志只记录必要元数据和内容哈希，默认不记录完整文件内容、脚本、Prompt 或密钥。
- 严格文件、网络、CPU、内存隔离最终由 Worker 进程、Job Object、低权限账户或容器能力提供。

## 12. 可靠性、持久化和恢复

### 12.1 运行可靠性

- Runtime 定期发送心跳和实例状态。
- 宿主与 Runtime 断开后可以使用实例 ID 和事件游标重新连接。
- Runtime 在可配置的缓冲限制内保留未确认事件。
- 缓冲达到上限时必须执行明确的背压、落盘或失败策略，不得静默丢失关键事件。
- Run 开始、工具调用前后和终态至少形成可恢复检查点。
- 宿主崩溃时，Runtime 可配置为继续并缓冲、等待租约或自动取消。
- Runtime 崩溃时，宿主根据任务幂等性选择恢复、重试或人工介入。
- 错误必须序列化为稳定错误码、类别、是否可重试、Provider 原始信息和诊断 ID。

### 12.2 `.madorin` 对话数据区

当未显式传入 `dataDirectory` 时，Runtime 使用 `workspaceRoot/.madorin`。用户直接执行 `ai-runtime run` 时，`workspaceRoot` 默认是 CLI 启动时的当前目录；由宿主连接 `serve` 时，必须由宿主明确传入，不能用 Runtime 进程碰巧启动时的当前目录推断业务工作区。

```text
workspaceRoot/
└── .madorin/
    ├── state.db          SQLite + WAL（元数据、状态、索引，不存消息正文）
    ├── messages/
    │   ├── {sessionId}.jsonl         CanonicalHistory 消息正文，append-only
    │   └── {sessionId}.compact.json  ContextProjection 缓存，可重新生成
    ├── blobs/            大型二进制内容，按 sha256 前缀分片
    ├── checkpoints/
    ├── staging/
    ├── locks/
    └── logs/
```

- `state.db` 使用 SQLite + WAL，保存 Session 元数据、Run / Invocation 状态、工具调用幂等记录、Selection 版本、会议/工作结构表、Blob 索引、GSN 游标。**消息正文不存入 `state.db`**，只保留轻量索引行（message_id、sequence、token_count、created_at）。
- `messages/{sessionId}.jsonl`：CanonicalHistory 消息正文，JSONL 格式，append-only。写入次序：先落盘文件，再更新 `state.db` 索引——保证数据库索引永远不指向尚未落盘的内容。
- `messages/{sessionId}.compact.json`：ContextProjection 缓存（每次 Invocation 生成的上下文投影），非权威数据，崩溃后可从 `.jsonl` 重新生成。
- 大型消息内容、附件和工具结果写入 `blobs/`，按哈希前缀分片，`state.db` 只保存 blobId、length、sha256。
- `staging/` 只保存未完成传输，校验完成后原子发布；`checkpoints/` 用于恢复快照；`logs/` 不是对话记录的权威来源。
- `.madorin` 是 Runtime 保留目录。AI 可用的文件工具默认拒绝直接读取、写入、移动或删除该目录，宿主和 UI 也不得绕过 Client SDK 修改其中的数据库或文件。
- 同一工作区默认只有一个 Runtime 写实例，通过锁文件和实例租约防止多进程同时写。
- `state.db` 损坏时可通过扫描 `messages/` 重建 Session 元数据（索引自愈工具）。
- Provider 凭据和宿主业务密钥不因对话持久化而自动写入 `.madorin`；敏感数据使用独立密钥存储或加密引用。

Runtime 是对话数据的权威方。宿主可以保存列表缓存或业务投影，但恢复时必须调用 Runtime API，并以返回的 `sessionVersion`、消息游标和状态为准。

### 12.3 日期分类决策

V1 不按日期拆分数据库，也不使用 `2026/07/20/{sessionId}` 之类的目录作为会话检索入口。原因是会话可能跨越多日，日期不是稳定主键；拆分后会增加跨日恢复、事务、排序、搜索、备份和迁移成本。

会话数量增加不会要求宿主扫描文件系统。Runtime 提供：

```text
session.list(cursor, pageSize, mode, status, updatedAfter, updatedBefore, search)
session.get(sessionId)
session.messages.list(sessionId, cursor, pageSize)
session.resume(sessionId)
```

`state.db` 至少为 `sessionId`、`(updatedAt, sessionId)`、`mode`、`status` 和消息的 `(sessionId, sequence)` 建索引。列表使用基于 `(updatedAt, sessionId)` 的游标分页，不在大数据量下依赖深 `OFFSET`。因此日期筛选只是索引查询条件，不会因为没有日期文件夹而降低恢复性能。

如果后续为了备份、导出或冷数据清理需要日期目录，可以在 `.madorin/archive/YYYY/MM/` 下保存不可变归档，但必须由 `state.db` 维护 `sessionId -> relativePath` 映射。宿主仍只调用查询/恢复 API，不能扫描日期目录；活动 Session 不在运行中跨目录搬移。

### 12.4 保留与恢复规则

- 关闭或重启 Runtime 后，可按 `sessionId` 恢复三种模式的完整规范化历史和最近安全检查点。
- 恢复结果必须包含模式、最新 Selection、参与 Agent/总经理快照、未完成 Run 判定和最后持久化事件序号。
- 删除、归档、压缩和保留期清理由 Runtime 命令/API 执行，并形成审计记录；不能由宿主直接删除文件。
- 数据库损坏、Schema 版本不兼容或 Blob 哈希不一致时返回明确诊断，不静默创建空对话替代原记录。

## 13. 热更新和版本管理

- 程序版本、协议版本和 Provider 版本分别管理。
- 协议初始化阶段必须协商兼容版本和可选能力。
- 新旧 Runtime 可以并行运行；旧实例完成已有 Run，新实例接收新 Run。
- 不直接覆盖正在运行的可执行文件和 Provider 程序集。
- Client SDK 必须允许宿主指定 Runtime 路径、版本和启动策略。
- 新版本健康检查失败时，宿主可以回退到上一个可用版本。

## 14. 可观测性

Runtime 应提供：

- 结构化日志和诊断 ID。
- Run、Session、Provider、模型和工具维度的追踪。
- 首 Token 延迟、总耗时、Token 用量、重试和限流指标。
- 当前并发、排队、事件缓冲和 Worker 状态。
- 不包含密钥和敏感内容的诊断包。
- 可配置的内容记录策略，默认避免在日志中保存完整 Prompt 和工具结果。

## 15. 公开交付物

独立项目最终至少交付：

- AI Runtime 可执行程序。
- Runtime Server 和 CLI。
- 公共协议说明及版本兼容策略。
- .NET Client SDK。
- Provider Adapter SDK。
- OpenAI、Anthropic、Gemini 和 OpenAI Compatible Adapter。
- 示例宿主和协议一致性测试工具。
- 安装、升级、诊断和安全配置文档。

## 16. 总体验收标准

满足以下条件后，才能认为独立 Runtime 可以替代进程内 AI 执行：

1. 多个 Run 的流式输出、工具调用、取消和终态不会串线。
2. 主程序重连后能够按游标恢复未确认事件，不丢失终态。
3. 工具调用在超时、重连和重试场景下不会因协议重发造成重复副作用。
4. MCP 工具新增、移除和版本变化可以在安全点生效。
5. Runtime 或单个 Worker 崩溃不会导致主程序崩溃。
6. Runtime 升级时可以执行排空、并行版本切换和失败回退。
7. 主程序不引用具体 Provider SDK，也不解析控制台自然语言输出。
8. Runtime 在未授权情况下不读取宿主或用户的第三方 AI 配置目录。
9. Provider 不支持某项能力时返回明确能力错误，不进行静默降级。
10. 协议一致性测试覆盖正常、取消、超时、断线、重放、崩溃和并发场景。
11. 路径和 URL 中的空格、中文、保留字符、编码值和首尾空白可以无损往返。
12. 大型文件不经过 JSON/Base64 整体装载，传输后长度和 SHA-256 完全一致。
13. 内置文件工具无法通过相对路径、符号链接、目录联接点或特殊路径逃逸工作区。
14. 默认模式不允许任意进程和 PowerShell；开启非沙箱 Shell 时明确标识其权限范围。
15. 当前流期间可以更新 Provider、模型、Agent 和提示词；当前流正常完成，下一轮在原 Session 中立即生效。
16. 配置切换不改变 `sessionId`，不清空消息历史，也不要求新建、恢复或重启对话。
17. 每个 Turn 都能查询实际生效的 Selection 版本、能力、工具目录和提示词哈希。
18. 跨 Provider 延续对话时不依赖厂商私有状态，任何历史裁剪或降级都有明确事件。
19. 专家、会议和工作模式均由宿主通过同一协议启动，会议 Agent 数组和工作模式总经理/子智能体不会退回宿主进程执行。
20. 工作模式每次受控工具调用都能追溯到实际 Agent、父 Agent、Grant 和委派链，子智能体默认不继承权限。
21. Runtime 重启后可以从 `workspaceRoot/.madorin` 按 `sessionId` 恢复对话；宿主无需也不得扫描日期目录。
22. 在大量会话下，按更新时间、模式和状态的游标分页不会随归档目录数量线性扫描。

## 17. 版本路线

| 版本 | 重点 |
| --- | --- |
| V1 | 独立 Runtime 闭环、专家/会议/工作三模式、Named Pipe、多 Run、OpenAI/Anthropic、工具授权、`.madorin` 会话恢复 |
| V2 | Provider 插件化、Worker 隔离、更复杂的自定义 Workflow、冷数据归档和版本并行调度 |
| V3 | 多模态完善、远程传输、分布式 Worker、资源配额和企业级安全策略 |

V1 的具体范围、项目结构、实施步骤和验收用例见下一份文档。
