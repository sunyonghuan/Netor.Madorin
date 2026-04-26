# AI 工具暴露规划

## 1. 规划背景

当前记忆插件已经具备事实摄取、长期记忆片段生成、抽象记忆生成和基础召回能力，但还不适合立即把全部能力暴露给 AI。

本次规划同时记录两个必须优先补齐的底层架构问题：

1. 缺少长期运行的记忆整理后台服务。记忆系统不能只依赖手动 runner 触发处理，应由插件内的 HostedService 持续扫描新入库观察记录，并定期执行整理、归并和抽象。
2. 缺少数据库底层操作服务。当前需要访问数据库的地方仍存在直接创建 SQLite 连接、直接执行 SQL 的问题，说明数据库访问层还没有完成，必须先统一封装数据库路径、连接、命令执行和事务边界。

因此，AI 工具暴露必须排在底层架构收口之后。第一阶段只暴露低风险读取类工具，不暴露重建、清库、删除、同步等高风险能力。

## 2. 暴露原则

1. AI 工具面向业务语义，不面向数据库表。
2. 不向 AI 暴露 SQL、SQLite 文件路径、内部表结构和迁移细节。
3. 默认只暴露读取类和上下文供应类工具。
4. 写入类工具必须要求用户明确授权，并写入审计记录。
5. 整理、重建、同步、删除类工具默认不暴露给普通 AI。
6. 工具命名统一使用 `memory_` 前缀。
7. 工具返回结构化结果，不直接拼接最终提示词。
8. 工具必须复用服务层，不允许绕过服务层直接访问数据库。

## 3. 前置架构要求

在实现 AI 工具前，必须先完成以下基础能力：

| 架构项 | 必要性 | 说明 |
| --- | --- | --- |
| 数据库执行层 | 必须 | 统一管理 SQLite 连接、路径、命令执行和事务，禁止上层散落 SQL 连接逻辑。 |
| 长期处理 HostedService | 必须 | 插件启动后持续处理新 observation，维护 fragment、abstraction 和处理状态。 |
| 记忆供应服务 | 必须 | AI 不应直接组合召回结果，应通过供应服务获得结构化上下文包。 |
| 工具日志降噪 | 必须 | 当前 tool/PowerShell 日志会污染长期记忆，需要过滤或降权。 |
| 审计链路 | 必须 | 召回、写入、整理、合并和抽象都应可追踪。 |

## 4. 第一批建议暴露工具

### 4.1 `memory_recall`

用途：根据查询文本召回相关长期记忆。

优先级：P0。

输入建议：

```text
queryText: string
queryIntent: string?
workspaceId: string?
maxMemoryCount: int?
```

输出建议：

```text
summary: string
confidence: double
windows: MemoryRecallWindow[]
items: MemoryRecallItem[]
```

实现要求：

1. 只调用 `IMemoryRecallService`。
2. 不直接访问数据库。
3. 自动写入 `recall_logs`。
4. 对返回数量执行上限保护。

适用场景：

1. 用户要求查询历史记忆。
2. AI 开始复杂任务前查询项目约束。
3. 当前上下文不足，需要补充事实、偏好、任务或约束。

### 4.2 `memory_supply_context`

用途：根据当前任务和最近消息生成可供上层注入的记忆包。

优先级：P0。

输入建议：

```text
scenario: string?
currentTask: string?
recentMessages: string[]?
workspaceId: string?
maxMemoryCount: int?
maxTokenBudget: int?
triggerSource: string?
```

输出建议：

```text
enabled: bool
summary: string
confidence: double
groups: MemorySupplyGroup[]
items: MemorySupplyItem[]
budget: MemorySupplyBudget
appliedPolicy: MemorySupplyPolicy
```

实现要求：

1. 通过 `IMemorySupplyService` 实现。
2. 供应服务内部复用 `IMemoryRecallService`。
3. 输出结构化供应包，不负责最终 prompt 拼接。
4. 按最大数量和预算截断。
5. 按 abstraction、constraint、preference、task、fact 分组。

适用场景：

1. 新会话开始。
2. 用户切换项目或工作区。
3. AI 进入编码、验收、规划、调试等复杂任务前。
4. 宿主准备构建上下文前。

### 4.3 `memory_get_status`

用途：查看记忆系统状态。

优先级：P0。

输入建议：

```text
workspaceId: string?
```

输出建议：

```text
observationCount: int
fragmentCount: int
abstractionCount: int
recallLogCount: int
processingState: string
lastProcessedAt: string?
knownIssues: string[]
```

实现要求：

1. 通过服务层获取状态。
2. 不暴露数据库路径。
3. 不返回大段原文。
4. 用于验收、诊断和 AI 自检。

## 5. 第二批建议暴露工具

### 5.1 `memory_add_note`

用途：用户明确要求时，写入一条人工记忆。

优先级：P1。

输入建议：

```text
content: string
memoryType: string
topic: string?
reason: string
workspaceId: string?
```

限制：

1. 必须用户明确要求“记住”“写入记忆”“加入长期记忆”。
2. 必须写入来源、原因和审计记录。
3. 不允许 AI 静默调用。
4. 默认写入 candidate/pending，后续再确认。

### 5.2 `memory_list_recent`

用途：查看最近生成或访问的记忆。

优先级：P1。

输入建议：

```text
limit: int?
kind: string?
workspaceId: string?
```

用途：

1. 验收。
2. 透明化展示。
3. 让用户知道系统最近记住了什么。

## 6. 不建议默认暴露的工具

以下能力不得第一版暴露给普通 AI：

| 工具能力 | 原因 |
| --- | --- |
| 直接 SQL 查询 | 泄露底层结构，破坏服务边界。 |
| 直接表增删改 | 绕过审计和业务规则。 |
| 重置数据库 | 破坏性强。 |
| 重新同步历史 | 可能清空验证库或重复导入。 |
| 批量删除记忆 | 需要人工确认和撤销策略。 |
| 修改 memory_settings | 会影响全局行为。 |
| 修改 processing state | 可能破坏处理游标。 |
| 手动运行大批量处理 | 可能造成大量噪声和锁竞争。 |

## 7. 管理员工具规划

这些工具可以后续作为管理员或开发者工具实现，但默认不进入普通 AI 工具列表：

1. `memory_process_pending`：触发处理待处理 observation。
2. `memory_run_abstraction`：触发抽象生成。
3. `memory_resync_history`：重新同步历史数据。
4. `memory_mark_forgotten`：软遗忘指定记忆。
5. `memory_rebuild_index`：重建索引或向量。

所有管理员工具都必须满足：

1. 明确权限边界。
2. 明确确认流程。
3. 完整审计。
4. 可回滚或至少可追踪。

## 8. 推荐实施顺序

1. 补齐数据库执行层。
2. 补齐长期处理 HostedService。
3. 治理 tool/PowerShell 日志噪声。
4. 实现 `IMemorySupplyService`。
5. 暴露 `memory_recall`。
6. 暴露 `memory_supply_context`。
7. 暴露 `memory_get_status`。
8. 编写 AI 工具验收文档。
9. 再评估是否加入 `memory_add_note` 和 `memory_list_recent`。

## 9. 第一版验收标准

第一版 AI 工具完成后应满足：

1. AI 可以按任务召回相关长期记忆。
2. AI 可以获取结构化上下文供应包。
3. AI 可以查看记忆系统基础状态。
4. 所有工具都走服务层。
5. 没有工具直接操作 SQLite。
6. 没有工具暴露数据库路径和 SQL。
7. 召回和供应结果有数量和预算限制。
8. 项目构建通过。
9. 有验收文档记录工具输入、输出、边界和风险。

## 10. 当前决策

当前阶段先执行架构收口：

1. 新增数据库执行层，收敛 SQLite 连接和命令执行。
2. 新增长期运行的记忆处理 HostedService。
3. 暂不实现 AI 工具代码。
4. 暂不暴露写入、删除、同步、重建能力。
5. 后续 AI 工具必须复用服务层和审计链路。
