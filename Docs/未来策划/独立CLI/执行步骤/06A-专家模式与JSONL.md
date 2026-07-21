# 阶段 6A：专家模式与 JSONL : 0%

> 阶段状态：待执行 · 总进度：0%
>
> 前置阶段：[05-工具权限与反向RPC](./05-工具权限与反向RPC.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 0 | 56 | 0% |
| 测试要求 | 0 | 11 | 0% |
| 完成标准 | 0 | 7 | 0% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

## 1. 目标

以专家模式打通第一个真实业务闭环，并完成所有模式共用的 CanonicalHistory JSONL、消息索引、自愈、ContextProjection 和 Session 恢复基础。

## 2. 前置门禁

- 真实 Provider、Run/Invocation 状态机和工具网关已通过一致性测试。
- `.madorin`、SQLite、Blob、Outbox 和 rehydrate 握手可用。
- `ExpertOptions`、AgentRef、AgentSnapshot 和 InvocationSnapshot 已冻结。
- `~/.madorin/config.json`、`agents/*.json` 和独立 CLI 的选择规则已经通过验证。
- 全局/项目 `memory.md` 的路径、命令、Memory File Service 和自动注入规则已经通过验证。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Orchestration.Abstractions` | Invocation 执行、模式编排和投影接口 |
| `Madorin.AI.Runtime.Modes.Expert` | 专家模式单 Agent 编排 |
| `Madorin.AI.Runtime.Persistence.Files` | JSONL、compact cache、末行恢复和索引重建 |
| `Madorin.AI.Runtime.Persistence.Sqlite` | 消息轻量索引和恢复事务 |
| `Madorin.AI.Runtime.Services` | Invocation Manager、Memory Context Provider、Content Pipeline 和历史服务 |
| `Madorin.AI.Runtime.Server` | MAF Agent 工厂与模式注册 |
| `Madorin.AI.Runtime.Cli` | 无宿主专家模式的本地应用服务入口 |
| `Madorin.AI.Runtime.Persistence.Tests` | JSONL 幂等、自愈、并发和索引测试 |
| `Madorin.AI.Runtime.Modes.Tests` | 专家模式多轮、切换和工具测试 |
| `Madorin.AI.Runtime.EndToEnd.Tests` | 真实进程专家模式恢复测试 |

## 4. 执行步骤 : 0%

### 4.1 实现 AgentInvocation 执行器 : 0%

- [×] 从 Run、Session Selection、单轮 Override、AgentRef、全局/项目记忆和工具目录解析一次不可变 Invocation 配置。
- [×] 在调用 Provider 前持久化 Invocation 记录和 AgentSnapshot，并发送 `invocation.started`。
- [×] 每个 Invocation 创建新的 MAF `IAgent`；底层 Adapter/HttpClient 按既定生命周期复用。
- [×] 将 ContextProjection、工具目录和取消令牌传入 Agent 执行循环。
- [×] 将输出、reasoning、工具调用、用量和错误转为统一事件及 CanonicalHistory 记录。
- [×] Invocation 完成/失败先持久化，再发送对应事件；Run 根据模式结果进入 Persisting 和唯一终态。
- [×] 创建 Agent 前按全局、项目顺序注入记忆，并把两个文件哈希写入 InvocationSnapshot。

### 4.2 实现专家模式编排 : 0%

- [×] 一个专家 Run 对应一个用户输入和一个主 AgentInvocation；工具续调仍属于同一 Invocation。
- [×] 验证 Session mode 为 Expert 且只有一个可执行 Agent 定义。
- [×] 用户内容先进入 CanonicalHistory，再生成本轮 ContextProjection。
- [×] 助手最终内容、reasoning 和工具记录按实际发生顺序追加。
- [×] 取消、Provider 失败、工具拒绝和凭据失败均保留已完成历史并形成唯一 Run 终态。

### 4.3 固化 `ConversationRecordV1` : 0%

- [×] 每个 `messages/{sessionId}.jsonl` 首行是带 schema、sessionId、mode 和 createdAt 的 header。
- [×] 每条消息包含 messageId、seq、invocationId、agentId、role、`content[]` 和时间戳。
- [×] `content[]` 使用协议定义的联合类型，不降级为单一字符串。
- [×] reasoning、tool call/result、usage 引用和摘要元数据使用稳定字段。
- [×] 每行是独立 UTF-8 JSON，不使用跨行格式化；单行过大内容转 Blob。
- [×] 文件名只由已验证的 sessionId 生成，禁止拼入标题、日期或用户路径。

### 4.4 实现单 Session 顺序写队列 : 0%

- [×] Session 加载时从 JSONL 最后一条完整记录获得 `lastKnownSeq`。
- [×] 写入前在内存生成 messageId 和 `seq = lastKnownSeq + 1`。
- [×] 先 append JSONL 并按持久性策略 flush，再更新内存 seq。
- [×] 文件成功后执行 SQLite `INSERT OR IGNORE` 轻量索引。
- [×] 同一 Session 所有写入经过单写队列；不同 Session 可以并行。
- [×] 写队列容量有上限并支持取消，但已经分配并开始落盘的记录必须完成一致性收尾。

### 4.5 实现崩溃自愈与索引重建 : 0%

- [×] 启动/打开 Session 时检测末行是否完整 UTF-8 和有效 JSON。
- [×] 只允许截断损坏的最后一行；中间损坏视为数据损坏并停止自动修复。
- [×] 文件已写、SQLite 未更新时按 messageId/sequence 补索引，不重复追加。
- [×] SQLite 索引与文件冲突时以完整 JSONL 权威内容为准，并记录诊断。
- [×] 重建工具扫描 header 和完整记录，恢复 Session/消息索引；Run、Grant 和工具意图不可恢复部分明确报告。
- [×] 修复前保留备份或恢复点，不能静默覆盖原文件。

### 4.6 实现 CanonicalHistory 读取 : 0%

- [×] UI/导出读取始终返回完整权威历史，不因模型窗口裁剪而删除记录。
- [×] 消息列表先通过 SQLite 游标定位，再按记录位置/序号读取 JSONL。
- [×] 验证 SQLite 索引不指向未落盘内容；发现不一致时触发诊断而非返回空消息。
- [×] Blob 内容按权限和大小选择元数据、流或明确省略，不无界加载。
- [×] CanonicalHistory 只能显式归档，不允许 ContextProjection 流程改写或删除。

### 4.7 实现 Full 与 TailWindow 投影 : 0%

- [×] 每次 Invocation 从 CanonicalHistory 新建 ContextProjection。
- [×] 使用当前 Adapter tokenizer/估算器计算 token，并为估算值标记来源。
- [×] 未超过模型窗口 90% 时使用 Full。
- [×] TailWindow 从最近完整消息单元选择，工具调用和结果不得拆开。
- [×] 投影发生裁剪时发送 `context.projection.adjusted`，包含数量、估算、策略和 Invocation。
- [×] 投影只存在内存；可选 `.compact.json` 只是带源历史哈希的缓存，可随时重建。

### 4.8 实现专家模式多轮与实时选择 : 0%

- [×] 当前流期间接受 Selection 更新，但当前 Invocation Snapshot 不变。
- [×] 下一 Run 在同一 sessionId 上使用最新 Provider、模型、Agent 和 Prompt。
- [×] 单轮 Override 结束后不写回 NextTurnSelection。
- [×] 跨 Provider 时从 CanonicalHistory 重新构建投影，不依赖旧 Provider 会话状态。
- [×] 历史内容无法被新 Provider 表达时产生明确投影调整或能力错误。

### 4.9 完成专家 Session 恢复 : 0%

- [×] `session.resume` 返回完整规范化历史元数据、最新 Selection、AgentSnapshot、附件/Blob、检查点和 lastGsn。
- [×] Prompt 未加密存储时返回一个 SingleAgent `neededDefinitions`。
- [×] `session.rehydrate` Ready 后才允许 ExistingSessionRunRequest。
- [×] 恢复后从原 JSONL seq 和数据库 GSN 水位继续，不重置编号。
- [×] 恢复的第一轮必须验证 Prompt 哈希、模式和 Selection 版本。

### 4.10 接通独立 CLI 的基本专家对话 : 0%

- [×] `madorin` 从用户目录加载默认 Provider、模型、Agent 和全局 `memory.md`，并从当前工作区加载项目 `memory.md`，在当前进程启动专家模式。
- [×] 独立 CLI 支持基本多轮输入、流式输出、新建会话和退出，不要求宿主或 IPC 存在。
- [×] Agent 来自 `~/.madorin/agents/*.json`，其 Provider/模型缺省值由 `config.json` 补齐。
- [×] 独立模式不发布宿主业务工具或 MCP 工具，但必须把 `builtin.memory.read/append` 作为 CLI 自带工具提供给 LLM；完整宿主交互仍由 Client SDK/协议承担。
- [×] 配置修改不影响已启动进程，退出并重新启动 `madorin` 后生效。

## 5. 测试要求 : 0%

- [×] 在 JSONL 写入前、写入中、flush 后和索引前后逐点注入崩溃。
- [×] 重启后不重复 messageId/seq，SQLite 索引不指向不存在记录。
- [×] 损坏末行可修复，中间损坏被拒绝且原文件不被进一步修改。
- [×] 同一 Session 高并发追加保持顺序；不同 Session 并行无全局文件锁瓶颈。
- [×] Full/TailWindow 不修改 CanonicalHistory，工具调用/结果不会被拆开。
- [×] 当前流中切换 Provider/模型/Agent/Prompt，下一轮同 Session 生效且当前流不污染。
- [×] 专家模式覆盖正常、工具、审批、取消、401 续期、重连和 Runtime 重启。
- [×] 从干净用户目录完成首次配置后，无宿主运行 `madorin` 并完成基本多轮专家对话。
- [×] 修改任一 `memory.md` 后，当前 Invocation 保持原内容，下一轮使用新内容并产生新哈希。
- [×] 真实 Provider 可以让 LLM 直接调用 `builtin.memory.read` 并继续回答；`append` 经审批后写入且下一轮自动注入。
- [×] 真实路径、URL、中文、多行 Prompt 和大 Blob 恢复后逐字节一致。

## 6. 完成标准 : 0%

- [×] 专家模式从 New Session 到多轮续接、工具调用、终态和恢复全链路通过。
- [×] `madorin` 可以使用个人配置完成无宿主基本专家对话，同时不改变宿主协议路径。
- [×] 全局和项目记忆在 Agent 启动时自动注入，不进入 CanonicalHistory，也不触发自动记忆写入。
- [×] JSONL 是消息正文唯一权威存储，SQLite 只保留轻量索引。
- [×] 文件/索引崩溃窗口均有自动或明确人工恢复路径。
- [×] CanonicalHistory 与 ContextProjection 在代码、存储和事件语义上严格分离。
- [×] 性能基线证明追加和分页不会随历史文件总数线性扫描。

## 7. 风险与禁止事项

- 不得从 SQLite 的最后 sequence 派生下一 JSONL seq。
- 不得先写 SQLite 索引再写消息文件。
- 不得修改或插入历史 JSONL 行来实现压缩。
- 不得跨 Invocation 复用 MAF `IAgent` 实例。
- 不得在 Prompt 缺失或哈希不匹配时自动用空 Prompt 恢复。
