# 独立 AI CLI / Runtime 命令规范

> 文档状态：规范设计
>
> 前置文档：[总体方案](../README.md) · [实施方案 V1](../方案设计/01-实施方案-V1.md) · [架构修订](../方案设计/02-架构修订-V1.md)
>
> 本文定义所有 CLI 命令，覆盖三种使用场景：
> - **独立运行**：用户直接执行 `madorin` 命令，不依赖宿主程序
> - **宿主集成**：宿主通过 IPC 协议调用 Runtime，CLI 提供等价的控制入口
> - **交互式会话**：在 `madorin run` 的交互模式下，用斜杠命令控制当前会话

---

## 1. 命令总览

```text
madorin <command> [subcommand] [options]

── 服务与执行 ──────────────────────────────────────────────────────
  serve          启动常驻 Runtime Server，等待宿主 IPC 连接
  run            执行一次对话任务（独立运行或交互模式）

── 诊断与信息 ──────────────────────────────────────────────────────
  version        显示程序版本、协议版本和构建信息
  doctor         检查配置、Provider 连通性和运行环境
  providers      查看和管理 Provider 配置

── 会话管理 ────────────────────────────────────────────────────────
  session        会话列表、查看、导出、删除、压缩

── 数据库维护 ──────────────────────────────────────────────────────
  db             数据库完整性检查、重建索引、清理、备份

── 存储维护 ────────────────────────────────────────────────────────
  storage        Blob 垃圾回收、消息文件检查

── 在线控制（连接到运行中的 Runtime） ──────────────────────────────
  ctl            查询活动 Run、取消任务、推送凭据
```

---

## 2. 服务与执行命令

### `madorin serve`

启动常驻 Runtime Server，等待宿主通过 Named Pipe 连接。

```text
madorin serve
  --workspace <path>       必须；绑定工作区，所有会话数据存入该目录下 .madorin/
  --data-dir <path>        可选；覆盖默认数据目录（默认 {workspace}/.madorin）
  --config-dir <path>      可选；配置文件目录
  --log-dir <path>         可选；日志目录
  --instance <id>          可选；Runtime 实例名（默认自动生成 UUID）
  --pipe-prefix <prefix>   可选；Named Pipe 前缀（默认 \\.\pipe\netor.ai.runtime）
  --max-runs <n>           可选；最大并发 Run 数（默认 10）
  --log-level <level>      可选；Debug | Information | Warning | Error
```

退出码：0 = 正常关闭，1 = 启动失败，2 = 认证失败。

---

### `madorin run`

执行一次对话任务。不传 `--input` 时进入交互模式（REPL）。

```text
madorin run
  --workspace <path>       可选；缺省使用当前目录
  --provider <id>          可选；Provider ID（缺省读配置文件默认值）
  --model <modelId>        可选；模型 ID
  --agent <agentId>        可选；Agent 定义文件或内置 Agent ID
  --mode expert|meeting|work  可选；运行模式（默认 expert）
  --session <sessionId>    可选；继续已有会话
  --input <text>           可选；单次输入，执行后退出（非交互）
  --input-file <path>      可选；从文件读取输入
  --output-file <path>     可选；输出写入文件（JSON 或纯文本）
  --output-format text|json|jsonl  可选；输出格式（默认 text）
  --no-stream              可选；等待完整结果后输出（非流式）
  --timeout <seconds>      可选；执行超时
  --data-dir <path>        可选；覆盖数据目录
```

**交互模式斜杠命令**见第 8 节。

---

## 3. 诊断与信息命令

### `madorin version`

```text
madorin version
  --json                   以 JSON 输出（适合脚本解析）

输出示例：
  madorin 1.0.0 (protocol 1.2, built 2026-07-20)
  Runtime: Netor.AI.Runtime
  Platform: .NET 10.0 / Windows x64
```

---

### `madorin doctor`

检查当前环境是否满足运行条件。

```text
madorin doctor
  --workspace <path>       可选；检查指定工作区的 .madorin 数据
  --provider <id>          可选；只检查指定 Provider
  --fix                    可选；尝试自动修复可修复的问题
                           注意：--fix 会修改数据库，执行前自动备份到 {data-dir}/backups/；
                           若无法写入备份目录，拒绝执行 --fix；
                           显示将执行的修复操作并要求用户确认（可用 --yes 跳过确认）

检查项（按顺序）：
  ✓ .NET 运行时版本
  ✓ Named Pipe 支持
  ✓ 工作区目录权限（读/写/创建）
  ✓ .madorin 数据完整性（state.db schema + messages/ 文件）
  ✓ Provider 配置和 API Key 存在性
  ✓ Provider API 连通性（发送 ping 请求）
  ✓ 日志目录写权限
  ✓ Blob 存储目录权限
  ! 警告：messages/ 中存在未被 state.db 引用的孤立文件
```

---

### `madorin providers`

```text
madorin providers list
  --json                   JSON 输出

madorin providers show <providerId>
  显示 Provider 详细能力（模型列表、支持的特性、速率限制）

madorin providers test <providerId>
  --model <modelId>        可选；测试指定模型
  发送一次最小化请求，验证连通性、认证和流式输出是否正常

madorin providers add <providerId>
  --type openai|anthropic|openai-compatible|custom
  --endpoint <url>         OpenAI Compatible 时必须
  --api-key-env <envVar>   从环境变量读取 API Key（推荐；环境变量不进入进程列表或 Shell 历史）
  --api-key-file <path>    从文件读取 API Key（文件权限应设为 600/仅所有者可读）
  --config-file <path>     从文件读取完整配置（含所有字段）

注意：不提供 --api-key 直接传值入参——API Key 会出现在进程列表（ps/tasklist）和 Shell 历史中。
      如需交互式输入，运行不带 API Key 参数的命令，系统会提示安全输入（不回显）。

madorin providers remove <providerId>
  --confirm                必须显式确认
```

---

## 4. 会话管理命令

### `madorin session list`

```text
madorin session list
  --workspace <path>       可选；缺省使用当前目录
  --mode expert|meeting|work  可选；按模式过滤
  --status active|archived|all  可选；默认 active
  --limit <n>              可选；最多显示 N 条（默认 20）
  --since <date>           可选；只显示该日期后的会话（ISO 8601）
  --search <keyword>       可选；按标题/摘要关键字过滤
  --json                   JSON 输出
```

---

### `madorin session show <sessionId>`

显示会话详细信息：元数据、最新 Selection、消息数、token 统计、会议/工作结构摘要。

```text
madorin session show <sessionId>
  --workspace <path>
  --messages               同时显示消息摘要（每条一行）
  --json
```

---

### `madorin session export <sessionId>`

将会话内容导出为可读文件。

```text
madorin session export <sessionId>
  --workspace <path>
  --format jsonl|markdown|txt  默认 markdown
  --output <path>          输出路径（缺省打印到 stdout）
  --include-reasoning      是否包含推理过程（默认不包含）
  --include-tool-calls     是否包含工具调用详情（默认不包含）
```

Markdown 格式输出示例：
```markdown
# 会话：优化数据库查询性能
模式：专家 | 创建：2026-07-20 | 消息数：24

---
**用户**：帮我分析一下这个 SQL 查询为什么慢...
**助手（claude-opus-4-7）**：...
```

---

### `madorin session delete <sessionId>`

```text
madorin session delete <sessionId>
  --workspace <path>
  --confirm                必须显式确认
  --include-blobs          同时删除该会话引用的 Blob 文件
                           注意：Blob 是内容寻址存储，可能被其他会话共享；
                           默认不删除 Blob，使用 storage gc 命令安全清理孤立 Blob
```

默认只删除 `state.db` 记录和 `.jsonl` 消息文件，不删除 `blobs/` 内容（避免误删其他会话共享的 Blob）。

---

### `madorin session compact <sessionId>`

手动为指定会话触发上下文压缩，生成摘要并更新 `.compact.json`。

```text
madorin session compact <sessionId>
  --workspace <path>
  --provider <id>          用于生成摘要的 Provider（缺省使用会话的默认 Provider）
  --model <modelId>        用于生成摘要的模型（缺省使用 Provider 的默认模型）
  --strategy full|sliding-window|summary   压缩策略（默认 summary）
  --dry-run                只显示压缩计划，不实际执行
  --force                  即使当前 .compact.json 仍然有效也强制重新生成

输出：
  压缩前 token 估算：8,420
  生成摘要消息：1 条（约 320 tokens）
  压缩后投影 token 估算：2,180
  已写入 .madorin/messages/{sessionId}.compact.json
```

---

### `madorin session archive <sessionId>`

将会话标记为归档状态（不删除数据，只从活跃列表隐藏）。

```text
madorin session archive <sessionId>
  --workspace <path>

madorin session unarchive <sessionId>
  --workspace <path>
```

---

## 5. 数据库维护命令

> **安全说明**：`db check` 严格只读且不获取写锁。`db repair` 等写命令必须在实际写入前获取同一工作区的排他锁；锁冲突时直接报错，不提供绕过写锁的参数。

### `madorin db check`

只读检查命令，不修改任何文件或数据库，不截断文件。发现问题时只报告，不自动修复。

```text
madorin db check
  --workspace <path>
  --data-dir <path>        覆盖 Runtime 数据目录
  --verbose                显示每个检查项详情

检查项（全部只读，不修改数据）：
  ✓ SQLite quick_check、integrity_check 和 foreign_key_check
  ✓ state.db schema 版本、必需表和索引与当前 Runtime 兼容
  ✓ state.db WAL 模式正常；活动 WAL 中的已提交数据必须可见
  ✓ messages/ 中的每个 .jsonl 文件在 state.db 中有对应 Session 记录
  ✓ state.db 中的每个 Session 在 messages/ 中有对应 .jsonl 文件
  ✓ .jsonl Header、消息序号和每行 JSON 完整性（不修改损坏尾行）
  ✓ .jsonl 权威消息记录与 state.db 消息索引一致
  ! 警告：发现 1 个 messages/ 文件末行未完整写入（可通过 db repair 自愈）

如需修复，使用 db repair 命令（独立命令，有确认和备份提示）。
```

---

### `madorin db repair`

只修复 `db check` 已识别的可恢复损坏，不是 `db rebuild` 的别名。V1 可修复范围仅为 JSONL 损坏尾行和已有 Session 的消息索引漂移；中间行损坏、缺失 Session/JSONL、SQLite 损坏和 Schema 不兼容只报告，不自动重建或迁移。

```text
madorin db repair
  --workspace <path>
  --data-dir <path>        覆盖 Runtime 数据目录
  --dry-run                只显示修复计划，不获取写锁、不备份、不写入
  --yes                    跳过交互确认；JSON 模式实际修复时必填

执行顺序：
  1. 使用 db check 的只读规则生成修复计划
  2. 展示计划并要求 --yes 或交互确认
  3. 获取工作区排他锁，锁冲突时不修改数据
  4. 备份 state.db、现存 WAL/SHM 和待修复 JSONL
  5. 截断损坏尾行并从权威 JSONL 同步消息索引
  6. 再次只读检查；中途失败时保留恢复点和原始诊断

退出码：
  0   无问题、dry-run 成功或修复成功
  1   存在不可修复问题、备份/修复失败或修复后仍不健康
  2   JSON 模式实际修复未提供 --yes
  4   工作区排他锁冲突
  130 用户拒绝交互确认
```

---

### `madorin db rebuild`

从 `messages/` 文件扫描重建 `state.db` 的 Session 和消息索引。用于 `state.db` 损坏或丢失的恢复场景。

```text
madorin db rebuild
  --workspace <path>
  --data-dir <path>        覆盖 Runtime 数据目录
  --dry-run                只扫描并报告，不获取写锁、不备份、不写入
  --confirm                实际执行时必须显式确认（会覆盖现有数据）

注意：
  - 人类模式未提供 --confirm 时先展示计划并交互确认；拒绝返回 130
  - JSON 模式实际执行未提供 --confirm 时返回 2；dry-run 不要求确认
  - 实际执行先获取工作区排他锁，并在覆盖前保存 state.db 及现存 WAL/SHM 恢复点
  - Canonical JSONL 始终只读；尾行损坏只恢复有效前缀，中间损坏只报告
  - 只能恢复 Session 和消息索引；Run、Grant、工具意图和 Invocation 快照无法从文件恢复
  - 恢复后的 Session 状态为 Recovered，宿主需重新判断是否存在未完成 Run
  - 存在不可恢复 Session 时保留已恢复结果，返回 partial 和退出码 1

退出码：
  0   全部可恢复 Session 重建成功，或 dry-run 成功
  1   部分恢复、恢复点失败或重建失败
  2   JSON 模式实际执行未提供 --confirm
  4   工作区排他锁冲突
  130 用户拒绝交互确认
```

---

### `madorin db vacuum`

```text
madorin db vacuum
  --workspace <path>
  --data-dir <path>        覆盖 Runtime 数据目录
```

执行规则：

- 获取工作区排他锁后，使用独立、非连接池 SQLite 连接执行 `VACUUM`。
- 数据目录或 `state.db` 不存在时返回错误，不创建目录、空数据库或其他运行数据。
- 成功结果报告 `databasePath`、`beforeBytes`、`afterBytes` 和 `reclaimedBytes`。
- 命令不增加确认或 dry-run 参数，也不建立恢复点；SQLite `VACUUM` 在新文件完成后原子替换原数据库。

退出码：

```text
0   压缩成功
1   数据目录/数据库缺失或 SQLite 压缩失败
4   工作区排他锁冲突
```

---

### `madorin db backup`

```text
madorin db backup
  --workspace <path>
  --data-dir <path>        覆盖 Runtime 数据目录
  --output <path>          备份目标路径（缺省为 {data-dir}/backups/{timestamp}.db）
  --include-messages       是否同时压缩备份 messages/ 目录（默认不包含）
  使用 SQLite 在线备份 API，不需要停止 Runtime

执行规则：
  - 仅备份 SQLite 时不获取工作区写锁，在线备份包含活动 WAL 中已提交的数据
  - 包含 messages/ 时先获取工作区排他锁，再生成 SQLite 与消息目录的一致备份集
  - 消息归档与数据库同名，后缀为 .messages.zip，并保留 messages/ 下相对路径
  - 数据库和消息归档均先写同目录临时文件、flush-to-disk 后原子发布
  - 目标已存在时拒绝覆盖；任一部分失败时清理本次不完整备份集

JSON 成功输出：
  command、status、dataDirectory、databasePath、messagesArchivePath、messageFileCount

退出码：
  0   备份成功
  1   数据/目标/在线备份/消息归档失败
  4   --include-messages 时工作区排他锁冲突
```

---

### `madorin db migrate`

```text
madorin db migrate
  --workspace <path>
  --data-dir <path>        可选；覆盖工作区默认 .madorin 数据目录
  --to <version>           目标 schema 版本（缺省为当前 Runtime 支持的最新版本）
  --dry-run                只检查迁移路径，不执行
  启动时 Runtime 自动执行此命令；手动调用用于预检或强制迁移
```

- 迁移只允许从当前版本向前执行；拒绝负数、降级和高于当前 Runtime 支持版本的目标。
- `--dry-run` 严格只读：不获取工作区写锁、不创建恢复点，也不创建缺失的数据目录、数据库、WAL 或 SHM。
- 实际迁移先获取工作区排他锁，并在锁内重新读取当前 Schema，防止计划和执行之间发生版本竞争。
- 当前版本等于目标时返回 `up-to-date`，不建立恢复点；存在迁移步骤时先备份 `state.db` 及现存 WAL/SHM，再复用 Runtime 启动时使用的事务化 `SqliteSchema` 迁移链逐版本执行。
- 迁移失败时保留恢复点和原始诊断；数据目录或数据库不存在时返回错误，不静默创建空数据。

JSON 成功输出：

```text
command、status、dataDirectory、databasePath、fromVersion、targetVersion、appliedVersions、recoveryPointPath
```

退出码：

```text
0   dry-run、迁移成功或已是目标版本
1   数据目录/数据库缺失、恢复点失败或迁移失败
2   目标版本非法、降级或高于 Runtime 支持版本
4   工作区排他锁冲突
```

---

## 6. 存储维护命令

### `madorin storage gc`

安全清理 `blobs/` 目录中未被 Canonical JSONL 或 SQLite `tool_intents` 结果引用的孤立文件。

```text
madorin storage gc
  --workspace <path>
  --data-dir <path>
  --dry-run                显式只读展示计划；与 --confirm 互斥
  --older-than <days>      只处理至少指定天数的孤立 Blob（默认 7 天）
  --confirm                建立恢复点并执行清理
```

- 未提供 `--confirm` 时默认返回 dry-run 计划，不获取工作区写锁、不创建目录或恢复点。
- `--older-than` 不允许负数；`--dry-run` 与 `--confirm` 同时出现时返回参数错误。
- 执行模式先获取工作区排他锁，再在锁内重新扫描全部引用、Blob 文件和龄期，禁止直接执行锁外计划。
- 只有名称为小写 64 位 SHA-256 的孤立 `.blob` 文件可以成为候选；活动引用、过新文件、非法文件名和引用扫描不完整状态一律保护。
- 移动候选前建立 `backups/storage-gc-*` 恢复点并持久化 `manifest.txt`；候选移动到恢复点而不是不可恢复删除。中途失败保留已移动文件、剩余源文件、恢复点路径和原始诊断。

JSON 计划/结果至少包含：

```text
command、status、dataDirectory、blobDirectory、olderThanDays、cutoffUtc、
referenceScanComplete、candidateCount/candidateBlobIds、protectedByAgeCount、
collectedCount/collectedBlobIds、recoveryPointPath、blockingIssues
```

退出码：

```text
0   dry-run 计划可执行或清理成功
1   引用扫描不完整、恢复点失败或移动失败
2   龄期为负数或互斥参数冲突
4   工作区排他锁冲突
```

---

### `madorin storage check`

```text
madorin storage check
  --workspace <path>
  --data-dir <path>
  --verify-hashes          流式计算有效 Blob 的 SHA-256（耗时）
```

- 命令严格只读，不获取工作区写锁，不创建数据目录、数据库、WAL/SHM、恢复点或锁文件。
- 汇总 Canonical JSONL 中的 `blob_ref`、SQLite `tool_intents.result_blob_*` 和 `blobs/*.blob`，默认检查引用缺失、文件名、元数据冲突和已知长度。
- 只有指定 `--verify-hashes` 时才读取完整 Blob 内容并校验 SHA-256。
- 任一 JSONL 或 SQLite 引用扫描不完整时明确报告 `referenceScanComplete=false`；该状态不得被 GC 用来推断孤立 Blob。

JSON 输出至少包含：

```text
command、status、healthy、dataDirectory、blobDirectory、verifyHashes、
referenceScanComplete、referenceCount、blobFileCount、orphanCount、issueCount、issues
```

退出码：`0` 表示无问题，`1` 表示发现完整性问题或扫描失败，`2` 表示路径参数非法。

---

## 7. 在线控制命令（`ctl`）

`ctl` 子命令连接到**正在运行的** Runtime 实例，通过已认证的 Client 控制通道发送命令。实例只从全局 `--data-dir` 或 `{workspace}/.madorin/runtime.pid` 发现，不扫描其他用户目录或工作区；控制 Pipe 必须使用 `madorin.ai.runtime` 正式品牌。握手 secret 只从 `MADORIN_AI_RUNTIME_SECRET` 读取，不作为命令参数，也不写入 stdout、stderr、JSON 或日志。

### `madorin ctl status`

```text
madorin ctl status
  --instance <id>          可选；要求目标数据目录发现的实例 ID 与其一致
  --json

输出示例：
  实例：a1b2c3d4e5f6475c89ab0123456789cd
  版本：0.1.0 | 协议：1.0
  工作区：E:\Projects\my-project
  PID：12345
  活动 Run：3 | 已连接宿主：2
  运行时长：2h 14m
```

---

### `madorin ctl sessions`

```text
madorin ctl sessions
  --instance <id>
  列出当前 Runtime 实例管理的活动 Session，返回 Session ID、模式、状态、更新时间和标题
```

---

### `madorin ctl runs`

```text
madorin ctl runs
  --instance <id>
  --session <sessionId>    可选；只显示指定会话的 Run
  列出活动 Run 的 Run ID、Session ID、状态和启动时间
```

---

### `madorin ctl cancel <runId>`

```text
madorin ctl cancel <runId>
  --instance <id>
  --reason <text>          可选；取消原因（记入审计日志）
  向 Runtime 发送 run.cancel 命令；成功只表示 cancelAccepted，最终 cancelled 需另行查询
```

---

### `madorin ctl credential update`

向运行中的 Runtime 推送新凭据，用于 Provider API Key 续期。

```text
madorin ctl credential update
  --instance <id>
  --run <runId>            必填；等待凭据的 Run
  --provider <providerId>
  --profile <profileId>    可选
  --api-key-env <envVar>   从环境变量读取新 API Key（推荐，不出现在命令行）
  --expires-at <datetime>  可选；带显式时区的 ISO 8601 过期时间
```

缺少 Runtime secret 或握手失败返回 3；实例文件缺失/损坏、PID 失效、Pipe 品牌或实例不匹配、连接失败返回 5；参数错误返回 2；Runtime 拒绝取消或凭据更新返回 1。

---

## 8. 交互模式斜杠命令

在 `madorin run`（无 `--input` 参数）的交互模式下，输入 `/` 开头的命令控制当前会话。

```text
/model [modelId]               查看或切换下一轮使用的模型
  示例：/model claude-opus-4-7
  无参数时显示当前模型

/provider [providerId]         查看或切换下一轮使用的 Provider
  示例：/provider anthropic

/agent [agentId]               查看或切换下一轮使用的 Agent
  示例：/agent code-expert

/mode [expert|meeting|work]    显示当前运行模式（交互模式下不允许在会话内切换）

/compact                       立即为当前会话触发上下文压缩
  压缩完成后显示：压缩前/后 token 估算对比

/context                       显示当前上下文投影状态
  当前 CanonicalHistory：42 条消息 | 约 18,400 tokens
  当前 ContextProjection：28 条消息 | 约 7,200 tokens（SlidingWindow）

/session                       显示当前会话信息（sessionId、模式、消息数、Provider）

/new                           开始新会话（当前会话保存并关闭）

/resume [sessionId]            切换到指定会话（当前会话保存）
  无参数时显示最近会话列表

/export [path]                 将当前会话导出为 Markdown
  无参数时输出到 stdout

/tools                         显示当前 Run 可用的工具列表和权限状态

/history [n]                   显示最近 n 条消息（默认 5）
  /history all 显示全部

/clear                         清除终端显示（不清除历史记录）

/status                        显示 Runtime 当前状态（内存、缓冲、活动 Run）

/help [command]                显示命令帮助
  /help compact 显示 compact 详细说明

/exit                          保存会话并退出
/quit                          同 /exit
```

---

## 9. 输出规范

### 9.1 人类可读输出（默认）

- 成功操作：显示简洁结果
- 警告：以 `! 警告：` 前缀
- 错误：以 `✗ 错误：` 前缀，包含错误码和建议操作
- 进度：长时操作显示进度条或旋转指示符

### 9.2 机器可读输出（`--json` 或 `--output-format json`）

所有 `--json` 输出格式：

```json
{
  "success": true,
  "data": { ... },
  "warnings": [],
  "diagnosticId": null
}
```

错误时：
```json
{
  "success": false,
  "error": {
    "code": "WorkspaceNotFound",
    "message": "...",
    "isRetryable": false,
    "diagnosticId": "diag_xxx"
  },
  "warnings": [],
  "diagnosticId": "diag_xxx"
}
```

业务字段只出现在 `data` 中；失败时，恢复点等可操作上下文也可以保留在 `data`。JSON/JSONL 模式的 stdout 只包含机器数据，诊断日志和进度只写 stderr；`--no-color` 和非 TTY 输出不得包含 ANSI 控制码。流式 JSONL 每行都是完整 JSON 对象，终态行必须能仅凭该行的 `success` 与 `data/error` 判断成功或失败。输出文件先写同目录临时文件并原子发布，写入失败时保留已有有效结果。

### 9.3 退出码约定

| 退出码 | 含义 |
|---|---|
| 0 | 成功 |
| 1 | 通用错误 |
| 2 | 配置或参数错误 |
| 3 | 认证失败 |
| 4 | 工作区或数据错误 |
| 5 | 连接失败（ctl 命令无法连接到 Runtime） |
| 130 | 用户中断（Ctrl+C） |

---

## 10. 全局选项

以下选项适用于所有命令：

```text
--workspace <path>      工作区路径（多个命令需要时的全局覆盖）
--data-dir <path>       数据目录（覆盖 {workspace}/.madorin）
--log-level <level>     Debug | Information | Warning | Error（默认 Warning）
--no-color              禁用彩色输出
--json                  等价于 --output-format json
--version               显示版本后退出
--help                  显示帮助后退出
```

环境变量（优先级低于命令行参数）：

```text
NETOR_AI_WORKSPACE        默认工作区路径
NETOR_AI_DATA_DIR         默认数据目录
NETOR_AI_LOG_LEVEL        默认日志级别
NETOR_AI_NO_COLOR         =1 时禁用彩色输出
```
