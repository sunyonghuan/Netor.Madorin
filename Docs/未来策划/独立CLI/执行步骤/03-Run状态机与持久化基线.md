# 阶段 3：Run 状态机与持久化基线 : 0%

> 阶段状态：待执行 · 总进度：0%
>
> 前置阶段：[02-Runtime骨架与双通道](./02-Runtime骨架与双通道.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 0 | 61 | 0% |
| 测试要求 | 0 | 9 | 0% |
| 完成标准 | 0 | 5 | 0% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

## 1. 目标

建立多 Run 生命周期、Session 选择版本、SQLite Schema、GSN Event Outbox、事件确认/重放和 Runtime 崩溃判定。本阶段以模拟 Invocation 驱动状态机，不接真实 Provider，也不完成三模式专属持久化。

## 2. 前置门禁

- Runtime 可以安全启动、认证、建立双通道、重连并关闭。
- 控制消息和事件信封已经通过真实进程一致性测试。
- 工作区和数据目录在进程启动后保持单绑定。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Entities` | Session、Run、Invocation、Selection 和恢复实体 |
| `Madorin.AI.Runtime.Core` | 状态机、唯一终态、并发和幂等纯规则 |
| `Madorin.AI.Runtime.Persistence.Abstractions` | Repository、Conversation Store、Outbox、迁移和事务接口 |
| `Madorin.AI.Runtime.Persistence.Sqlite` | `state.db`、WAL、Schema、迁移、索引和 Outbox |
| `Madorin.AI.Runtime.Persistence.Files` | 数据目录、锁、Staging 和消息文件接口基线 |
| `Madorin.AI.Runtime.Services` | Run Registry、Session、Selection、事件和恢复服务 |
| `Madorin.AI.Runtime.Server` | 作用域、并发限制和服务生命周期装配 |
| `Madorin.AI.Runtime.Core.Tests` | 状态机和竞争测试 |
| `Madorin.AI.Runtime.Persistence.Tests` | 事务、迁移、Outbox、恢复和分页测试 |
| `Madorin.AI.Runtime.EndToEnd.Tests` | 多 Run、取消、重连和崩溃测试 |

## 4. 执行步骤 : 0%

### 4.1 完整实现 Run 状态机 : 0%

- [×] 以显式转换表实现所有允许跳转，不在业务服务中散落状态赋值。
- [×] 正常路径为 Accepted -> Preparing -> Running/等待态 -> Persisting -> 唯一终态。
- [×] `WaitingForTool`、`WaitingForApproval`、`WaitingForCredentials` 只能由对应子流程进入和退出。
- [×] `Interrupted` 只在 Runtime 启动恢复时赋给无终态 Run，正常运行不得主动设置。
- [×] 终态写入使用事务或比较交换条件，解决完成、取消、超时和崩溃竞争。
- [×] 重复取消返回当前状态；已终态 Run 不制造第二个终态事件。

### 4.2 建立 Run Registry 与独立作用域 : 0%

- [×] 每个 Run 创建独立 DI Scope、取消源、超时、Run 内序号、错误和工具调用空间。
- [×] Registry 只保存活动 Run 的轻量句柄；终态写入后及时释放大型对象。
- [×] 全局最大并发和每 Provider 并发采用独立限制器；本阶段用 Fake Provider key 验证。
- [×] 超出容量返回 Busy/Capacity，由宿主决定排队，Runtime 不复制宿主业务队列。
- [×] 同一 Session 默认串行，不同 Session 可以并行；并发策略可显式配置。
- [×] 一个 Run 失败、取消或超时不得取消其他 Run 的 Scope。

### 4.3 实现请求幂等 : 0%

- [×] 为 Session 与 Run 请求建立幂等记录及到期时间。
- [×] `sessionIdempotencyKey` 首次成功后返回固定 `sessionId`；重复请求返回原结果。
- [×] `runIdempotencyKey` 重复请求返回原 `run.accepted` 或当前终态，不重复执行。
- [×] 请求内容摘要与同一幂等键绑定；键相同但负载不同返回冲突错误。
- [×] 清理过期键时不得删除仍被活动 Run 或恢复流程引用的记录。

### 4.4 实现 Session 与 NextTurnSelection : 0%

- [×] Session 创建时固定 mode，续接时 mode 不允许改变。
- [×] `session.selection.update` 使用 `expectedSelectionVersion` 乐观并发。
- [×] 当前 Invocation 启动时生成不可变 Snapshot；流期间更新只影响下一 Invocation。
- [×] 单轮 `turnOverride` 只作用于本次 Run，结束后自动恢复 Session Selection。
- [×] Provider/模型/Agent/Prompt 变更不改变 `sessionId`、历史和附件引用。
- [×] 保存 `AgentSnapshot` 哈希并实现 `session.resume` 的 `neededDefinitions` 计算。
- [×] `session.rehydrate` 验证 Agent 定义哈希，未 Ready 的 Session 不允许启动续接 Run。

### 4.5 建立 `.madorin` 数据目录 : 0%

- [×] 建立 `state.db`、`messages/`、`blobs/`、`checkpoints/`、`staging/`、`locks/`、`logs/`，并为阶段 4B 保留可选的项目 `memory.md` 固定路径。
- [×] 验证数据目录实际位于配置位置；相对路径只在启动时解析一次。
- [×] 同一规范化工作区只允许一个写实例，锁包含实例、PID、启动时间和租约信息；不同 `instanceId`、Pipe 前缀或数据目录都不得绕过该锁。
- [×] 将 `.madorin` 标记为 Runtime 保留区，并向后续文件工具暴露统一拒绝策略。
- [×] 数据库、文件和日志权限不满足时启动失败，不自动转移到其他目录。
- [×] `memory.md` 不进入 SQLite、消息索引或 Checkpoint；它是用户维护的 Markdown 上下文文件。

### 4.6 建立 SQLite Schema 与迁移基线 : 0%

- [×] 启用 WAL、busy timeout、外键和明确的同步级别。
- [×] 建立 Schema 版本表和只前进迁移；每个迁移具备预检、事务、校验和回退说明。
- [×] 建立 Session、Run、Invocation、Selection、AgentSnapshot、请求幂等、消息轻量索引、Blob 元数据和事件 Outbox 表。
- [×] 为 `sessionId`、`(updatedAt, sessionId)`、mode、status、`(sessionId, sequence)` 和活动 Run 查询建立索引。
- [×] 消息索引只保存元数据，不保存正文或完整内容 JSON。
- [×] 数据库版本过新、迁移中断或校验失败时返回明确诊断，不创建空库替代。

### 4.7 实现 GSN Event Outbox : 0%

- [×] 在数据库事务内原子分配下一 GSN 并插入 Pending Outbox 行。
- [×] GSN、确认水位和 Outbox 只在当前 `runtimeInstanceId` 的数据存储内有效；不同 Runtime 可以出现相同数值的 GSN，宿主不得跨实例确认或去重。
- [×] 使用专用序列表或等价的串行写事务，禁止并发执行 `MAX(gsn)+1` 产生重复序号。
- [×] 事务提交后才允许发送事件；发送成功更新为 Sent。
- [×] 宿主确认 GSN 后，在事务内更新确认水位和 Acknowledged 状态。
- [×] 重启时按 GSN 升序加载 Pending/Sent 未确认事件并重放。
- [×] Outbox 行的 GSN 与 payload 永久一一对应，禁止复用旧 GSN 发送新内容。
- [×] 清理确认行时保留必要诊断水位，不破坏新连接的重放边界。

### 4.8 实现分级事件缓冲与背压 : 0%

- [×] 内存缓冲初始目标 32 MiB/实例，磁盘 replay 缓冲初始目标 512 MiB/实例。
- [×] 关键事件、工具事件、错误和终态始终进入 Outbox，不可丢弃。
- [×] Delta 可合并；达到磁盘上限后才允许丢弃未分配 GSN 的 Delta，并产生 `delta.dropped`。
- [×] 宿主确认后同步释放内存和磁盘缓冲。
- [×] `run.query` 返回终态文本快照，供宿主在 Delta 缺失时补全。
- [×] 所有容量、丢弃和背压动作记录指标，不记录敏感内容。

### 4.9 实现取消、超时与凭据等待骨架 : 0%

- [×] 取消命令先返回 `run.cancelAccepted`，完成清理后才发 `run.cancelled`。
- [×] Preparing、Running、三个等待态和 Persisting 均定义取消检查点。
- [×] 超时与用户取消使用不同错误/终态原因，但都只能形成一个终态。
- [×] Fake Provider 模拟 401，使 Run 进入 `WaitingForCredentials`。
- [×] 收到 `credentials.update` 后只重试原请求一次；再次 401 或等待超时进入 Failed。

### 4.10 实现恢复和查询基线 : 0%

- [×] 启动事务把无终态历史 Run 批量标记为 Interrupted，并产生诊断记录。
- [×] 恢复 Outbox 后才接受新 Run，避免新旧事件 GSN 交错错误。
- [×] `session.list` 使用 `(updatedAt, sessionId)` 游标分页，不使用深 OFFSET 和目录扫描。
- [×] 实现 `session.get`、消息索引分页、`session.resume` 的元数据部分。
- [×] JSONL 正文与三模式专属快照在阶段 6 完成前明确报告尚不可完整恢复。
- [×] 查询 API 只通过 Repository/Store，不允许 Server 直接打开数据库文件。

## 5. 测试要求 : 0%

- [×] 对状态机每个允许/禁止转换做表驱动测试。
- [×] 完成、取消、超时、断线并发竞争重复运行，始终只有一个终态。
- [×] 多 Session 并行、多 Run 交错事件下 GSN 无重复，Run 内序号有序。
- [×] 两个 Runtime 数据存储故意生成相同 `runId` 和 GSN 时，各自 Outbox、确认水位、重放与宿主去重状态仍完全隔离。
- [×] 在“Outbox 提交前、提交后发送前、发送后标记前、确认前后”注入崩溃并验证恢复。
- [×] Selection 并发更新产生确定冲突；当前 Invocation Snapshot 不被中途更新污染。
- [×] SQLite 迁移从每个历史 Schema 版本升级，失败时原数据可恢复。
- [×] 10 万 Session 数据集上的游标分页命中索引，性能不依赖消息目录数量。
- [×] Runtime 被强制结束后重启，无历史 Run 保持虚假 Running。

## 6. 完成标准 : 0%

- [×] 多个模拟 Run 可并发、独立取消、独立超时且不串线。
- [×] GSN Outbox 在故障注入后保持连续、一一对应并可重放。
- [×] Session Selection、rehydrate 和请求幂等行为通过协议与持久化测试。
- [×] `.madorin` 目录、SQLite 迁移、WAL、锁和查询基线可诊断。
- [×] 关键查询有执行计划或基准记录，Debug/Release 构建 0 警告。

## 7. 风险与禁止事项

- 不得把消息正文写入 `state.db`。
- 不得用内存计数器作为 GSN 的唯一权威来源。
- 不得在恢复未完成前接受新 Run。
- 不得让宿主通过扫描 `messages/` 或日期目录恢复 Session。
- 不得通过全局锁串行所有 Run；锁粒度限定在真正共享的数据库序列、Session 或资源限制器。
