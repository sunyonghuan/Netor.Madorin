# 阶段 3：Run 状态机与持久化基线 : 100%

> 阶段状态：已完成 · 总进度：100%
>
> 归档日期：2026-07-22 · 归档复验：Debug/Release 构建及 Native AOT 均为 0 警告、0 错误；371 项测试通过，3 项真实 Provider 凭据测试跳过
>
> 前置阶段：[02-Runtime骨架与双通道](../../执行步骤/02-Runtime骨架与双通道.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 61 | 61 | 100% |
| 测试要求 | 9 | 9 | 100% |
| 完成标准 | 5 | 5 | 100% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

> 进度说明：执行任务、测试要求和完成标准均已逐项回填。本轮补齐 Session 重启后 rehydrate、取消接收结果和消息索引分页的端到端验收，并将持久化 Schema 记录更新到 v7。

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

## 4. 执行步骤 : 100%

### 4.1 完整实现 Run 状态机 : 100%

- [√] 以显式转换表实现所有允许跳转，不在业务服务中散落状态赋值。
- [√] 正常路径为 Accepted -> Preparing -> Running/等待态 -> Persisting -> 唯一终态。
- [√] `WaitingForTool`、`WaitingForApproval`、`WaitingForCredentials` 只能由对应子流程进入和退出。
- [√] `Interrupted` 只在 Runtime 启动恢复时赋给无终态 Run，正常运行不得主动设置。
- [√] 终态写入使用事务或比较交换条件，解决完成、取消、超时和崩溃竞争。
- [√] 重复取消返回当前状态；已终态 Run 不制造第二个终态事件。

### 4.2 建立 Run Registry 与独立作用域 : 100%

- [√] 每个 Run 创建独立 DI Scope、取消源、超时、Run 内序号、错误和工具调用空间。
- [√] Registry 只保存活动 Run 的轻量句柄；终态写入后及时释放大型对象。
- [√] 全局最大并发和每 Provider 并发采用独立限制器；本阶段用 Fake Provider key 验证。
- [√] 超出容量返回 Busy/Capacity，由宿主决定排队，Runtime 不复制宿主业务队列。
- [√] 同一 Session 默认串行，不同 Session 可以并行；并发策略可显式配置。
- [√] 一个 Run 失败、取消或超时不得取消其他 Run 的 Scope。

### 4.3 实现请求幂等 : 100%

- [√] 为 Session 与 Run 请求建立幂等记录及到期时间。
- [√] `sessionIdempotencyKey` 首次成功后返回固定 `sessionId`；重复请求返回原结果。
- [√] `runIdempotencyKey` 重复请求返回原 `run.accepted` 或当前终态，不重复执行。
- [√] 请求内容摘要与同一幂等键绑定；键相同但负载不同返回冲突错误。
- [√] 清理过期键时不得删除仍被活动 Run 或恢复流程引用的记录。

### 4.4 实现 Session 与 NextTurnSelection : 100%

- [√] Session 创建时固定 mode，续接时 mode 不允许改变。
- [√] `session.selection.update` 使用 `expectedSelectionVersion` 乐观并发。
- [√] 当前 Invocation 启动时生成不可变 Snapshot；流期间更新只影响下一 Invocation。
- [√] 单轮 `turnOverride` 只作用于本次 Run，结束后自动恢复 Session Selection。
- [√] Provider/模型/Agent/Prompt 变更不改变 `sessionId`、历史和附件引用。
- [√] 保存 `AgentSnapshot` 哈希并实现 `session.resume` 的 `neededDefinitions` 计算。
- [√] `session.rehydrate` 验证 Agent 定义哈希，未 Ready 的 Session 不允许启动续接 Run。

### 4.5 建立 `.madorin` 数据目录 : 100%

- [√] 建立 `state.db`、`messages/`、`blobs/`、`checkpoints/`、`staging/`、`locks/`、`logs/`，并为阶段 4B 保留可选的项目 `memory.md` 固定路径。
- [√] 验证数据目录实际位于配置位置；相对路径只在启动时解析一次。
- [√] 同一规范化工作区只允许一个写实例，锁包含实例、PID、启动时间和租约信息；不同 `instanceId`、Pipe 前缀或数据目录都不得绕过该锁。
- [√] 将 `.madorin` 标记为 Runtime 保留区，并向后续文件工具暴露统一拒绝策略。
- [√] 数据库、文件和日志权限不满足时启动失败，不自动转移到其他目录。
- [√] `memory.md` 不进入 SQLite、消息索引或 Checkpoint；它是用户维护的 Markdown 上下文文件。

### 4.6 建立 SQLite Schema 与迁移基线 : 100%

- [√] 启用 WAL、busy timeout、外键和明确的同步级别。
- [√] 建立 Schema 版本表和只前进迁移；每个迁移具备预检、事务、校验和回退说明。
- [√] 建立 Session、Run、Invocation、Selection、AgentSnapshot、请求幂等、消息轻量索引、Blob 元数据和事件 Outbox 表。
- [√] 为 `sessionId`、`(updatedAt, sessionId)`、mode、status、`(sessionId, sequence)` 和活动 Run 查询建立索引。
- [√] 消息索引只保存元数据，不保存正文或完整内容 JSON。
- [√] 数据库版本过新、迁移中断或校验失败时返回明确诊断，不创建空库替代。

### 4.7 实现 GSN Event Outbox : 100%

- [√] 在数据库事务内原子分配下一 GSN 并插入 Pending Outbox 行。
- [√] GSN、确认水位和 Outbox 只在当前 `runtimeInstanceId` 的数据存储内有效；不同 Runtime 可以出现相同数值的 GSN，宿主不得跨实例确认或去重。
- [√] 使用专用序列表或等价的串行写事务，禁止并发执行 `MAX(gsn)+1` 产生重复序号。
- [√] 事务提交后才允许发送事件；发送成功更新为 Sent。
- [√] 宿主确认 GSN 后，在事务内更新确认水位和 Acknowledged 状态。
- [√] 重启时按 GSN 升序加载 Pending/Sent 未确认事件并重放。
- [√] Outbox 行的 GSN 与 payload 永久一一对应，禁止复用旧 GSN 发送新内容。
- [√] 清理确认行时保留必要诊断水位，不破坏新连接的重放边界。

### 4.8 实现分级事件缓冲与背压 : 100%

- [√] 内存 replay 缓冲默认上限 32 MiB/实例；超出后按最旧 GSN 溢出，磁盘 replay 缓冲默认上限 512 MiB/实例。
- [√] 关键事件、工具事件、错误和终态始终进入 SQLite Outbox，不可丢弃；磁盘镜像满时仍保留数据库持久化。
- [√] Delta 达到磁盘上限后在分配 GSN 前丢弃，并产生 `delta.dropped`。
- [√] 宿主确认后同步释放已确认的内存 replay 与磁盘 replay 镜像。
- [√] `run.query` 返回终态文本快照，供宿主在 Delta 缺失时补全。
- [√] 所有容量、丢弃和背压动作记录指标，不记录 Run ID、Prompt 或 payload。

### 4.9 实现取消、超时与凭据等待骨架 : 100%

- [√] 取消命令先返回 `run.cancelAccepted`，完成清理后才发 `run.cancelled`。
- [√] Preparing、Running、三个等待态和 Persisting 均定义取消检查点。
- [√] 超时与用户取消使用不同错误/终态原因，但都只能形成一个终态。
- [√] Fake Provider 模拟 401，使 Run 进入 `WaitingForCredentials`。
- [√] 收到 `credentials.update` 后只重试原请求一次；再次 401 或等待超时进入 Failed。

### 4.10 实现恢复和查询基线 : 100%

- [√] 启动事务把无终态历史 Run 批量标记为 Interrupted，并产生诊断记录。
- [√] 恢复 Outbox 后才接受新 Run，避免新旧事件 GSN 交错错误。
- [√] `session.list` 使用 `(updatedAt, sessionId)` 游标分页，不使用深 OFFSET 和目录扫描；10 万 Session、200 页基线本机实测 94 ms，执行计划命中 `idx_sessions_updated`。
- [√] 实现 `session.get`、消息索引分页、`session.resume` 的元数据部分。
- [√] JSONL 正文与三模式专属快照在阶段 6 完成前明确报告尚不可完整恢复。
- [√] 查询 API 只通过 Repository/Store，不允许 Server 直接打开数据库文件。

## 5. 测试要求 : 100%

- [√] 对 11 个状态的 121 个允许/禁止组合做表驱动测试。
- [√] 完成、取消、超时、断线终态竞争连续 10 轮、每轮 6 个独立 SQLite 连接，始终只有一个终态。
- [√] 多 Session 并行、多 Run 交错事件下 GSN 无重复，Run 内序号有序。
- [√] 两个 Runtime 数据存储故意生成相同 `runId` 和 GSN 时，各自 Outbox、确认水位、重放与宿主去重状态仍完全隔离。
- [√] 在“Outbox 提交前、提交后发送前、发送后标记前、确认前后”注入故障并验证恢复；GSN 回滚、同 payload 重放和 ACK 水位均保持正确。
- [√] Selection 并发更新产生确定冲突；当前 Invocation Snapshot 不被中途更新污染，更新后的 Selection 仅用于下一 Invocation。
- [√] SQLite v1-v6 分别升级到 v7 并保留既有 Run；迁移事务失败时版本号与原数据不变。
- [√] 10 万 Session 数据集上的游标分页命中索引，性能不依赖消息目录数量。
- [√] Runtime 被强制结束后重启，无历史 Run 保持虚假 Running，既有终态和终态文本保持不变。

## 6. 完成标准 : 100%

- [√] 多个模拟 Run 可并发、独立取消、独立超时且不串线。
- [√] GSN Outbox 在故障注入后保持连续、一一对应并可重放。
- [√] Session Selection、rehydrate 和请求幂等行为通过协议与持久化测试。
- [√] `.madorin` 目录、SQLite 迁移、WAL、锁和查询基线可诊断。
- [√] 关键查询有执行计划或基准记录，Debug/Release 构建 0 警告。

### 6.1 本轮验收记录

- Debug 阶段 3 相关测试：249/249 通过（Core 132、Protocol 28、Modes 10、Persistence 38、End-to-End 41）；Provider 测试独立于阶段 4 验收。
- Release 全解决方案构建：0 警告、0 错误；Windows `win-x64` Native AOT 发布：0 trimming/AOT 警告。
- 状态机：11 个状态、121 个组合全部逐例验证；终态 CAS 竞争连续 10 轮，每轮 6 个独立连接只允许一个写入成功。
- Outbox：默认 32 MiB 内存 replay 与 512 MiB 磁盘 replay 上限；内存满后按最旧 GSN 溢出；Delta 配额耗尽时不分配 GSN，关键事件即使镜像无空间仍写入 SQLite；ACK 同步释放内存和磁盘镜像。
- Outbox 故障恢复：提交前失败回滚 GSN；提交后、发送后未标记、已标记未 ACK 均按原 GSN/payload 重放；ACK 后释放镜像并保留确认水位。
- `run.query`：请求/结果协议快照与 JSON Source Generation 往返通过；真实 Named Pipe 下完成 Run 后可查询精确终态文本，未知 Run 返回协议错误；Schema v5 -> v7 升级保留既有 Run。
- Schema 与启动恢复：v1-v6 全部升级到 v7，失败迁移保持原版本和数据；RuntimeServer 启动将遗留 Running 标记为 Interrupted，并保留既有 Completed 状态及终态文本。
- Selection：乐观并发冲突可确定复现；Invocation Snapshot 创建后更新 Session Selection 不污染当前事件，下一次续接 Run 使用新版本。
- 指标：记录内存/磁盘容量与保留量、内存溢出、Delta 丢弃和背压；标签仅包含 `tier`、`reason`，不含敏感内容。
- Session 分页：100,000 条 Session，200 页游标分页，命中 `idx_sessions_updated`，本机分页阶段实测 94 ms。
- 阶段 3 已闭合：Session 元数据/消息索引分页、重启后 rehydrate、取消接收结果、超时终态、凭据单次重试和并发隔离均已通过针对性端到端与持久化验证；JSONL 正文和模式专属快照的完整恢复按阶段 6 规划保留明确诊断。

## 7. 风险与禁止事项

- 不得把消息正文写入 `state.db`。
- 不得用内存计数器作为 GSN 的唯一权威来源。
- 不得在恢复未完成前接受新 Run。
- 不得让宿主通过扫描 `messages/` 或日期目录恢复 Session。
- 不得通过全局锁串行所有 Run；锁粒度限定在真正共享的数据库序列、Session 或资源限制器。
