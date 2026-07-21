# 独立 AI CLI / Runtime 命令规范

> 文档状态：规范设计
>
> 前置文档：[总体方案](./README.md) · [实施方案 V1](./01-实施方案-V1.md) · [架构修订](./02-架构修订-V1.md)
>
> 本文定义所有 CLI 命令，覆盖三种使用场景：
> - **独立运行**：用户直接执行 `ai-runtime` 命令，不依赖宿主程序
> - **宿主集成**：宿主通过 IPC 协议调用 Runtime，CLI 提供等价的控制入口
> - **交互式会话**：在 `ai-runtime run` 的交互模式下，用斜杠命令控制当前会话

---

## 1. 命令总览

```text
ai-runtime <command> [subcommand] [options]

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

### `ai-runtime serve`

启动常驻 Runtime Server，等待宿主通过 Named Pipe 连接。

```text
ai-runtime serve
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

### `ai-runtime run`

执行一次对话任务。不传 `--input` 时进入交互模式（REPL）。

```text
ai-runtime run
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

### `ai-runtime version`

```text
ai-runtime version
  --json                   以 JSON 输出（适合脚本解析）

输出示例：
  ai-runtime 1.0.0 (protocol 1.2, built 2026-07-20)
  Runtime: Netor.AI.Runtime
  Platform: .NET 10.0 / Windows x64
```

---

### `ai-runtime doctor`

检查当前环境是否满足运行条件。

```text
ai-runtime doctor
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

### `ai-runtime providers`

```text
ai-runtime providers list
  --json                   JSON 输出

ai-runtime providers show <providerId>
  显示 Provider 详细能力（模型列表、支持的特性、速率限制）

ai-runtime providers test <providerId>
  --model <modelId>        可选；测试指定模型
  发送一次最小化请求，验证连通性、认证和流式输出是否正常

ai-runtime providers add <providerId>
  --type openai|anthropic|openai-compatible|custom
  --endpoint <url>         OpenAI Compatible 时必须
  --api-key-env <envVar>   从环境变量读取 API Key（推荐；环境变量不进入进程列表或 Shell 历史）
  --api-key-file <path>    从文件读取 API Key（文件权限应设为 600/仅所有者可读）
  --config-file <path>     从文件读取完整配置（含所有字段）

注意：不提供 --api-key 直接传值入参——API Key 会出现在进程列表（ps/tasklist）和 Shell 历史中。
      如需交互式输入，运行不带 API Key 参数的命令，系统会提示安全输入（不回显）。

ai-runtime providers remove <providerId>
  --confirm                必须显式确认
```

---

## 4. 会话管理命令

### `ai-runtime session list`

```text
ai-runtime session list
  --workspace <path>       可选；缺省使用当前目录
  --mode expert|meeting|work  可选；按模式过滤
  --status active|archived|all  可选；默认 active
  --limit <n>              可选；最多显示 N 条（默认 20）
  --since <date>           可选；只显示该日期后的会话（ISO 8601）
  --search <keyword>       可选；按标题/摘要关键字过滤
  --json                   JSON 输出
```

---

### `ai-runtime session show <sessionId>`

显示会话详细信息：元数据、最新 Selection、消息数、token 统计、会议/工作结构摘要。

```text
ai-runtime session show <sessionId>
  --workspace <path>
  --messages               同时显示消息摘要（每条一行）
  --json
```

---

### `ai-runtime session export <sessionId>`

将会话内容导出为可读文件。

```text
ai-runtime session export <sessionId>
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

### `ai-runtime session delete <sessionId>`

```text
ai-runtime session delete <sessionId>
  --workspace <path>
  --confirm                必须显式确认
  --include-blobs          同时删除该会话引用的 Blob 文件
                           注意：Blob 是内容寻址存储，可能被其他会话共享；
                           默认不删除 Blob，使用 storage gc 命令安全清理孤立 Blob
```

默认只删除 `state.db` 记录和 `.jsonl` 消息文件，不删除 `blobs/` 内容（避免误删其他会话共享的 Blob）。

---

### `ai-runtime session compact <sessionId>`

手动为指定会话触发上下文压缩，生成摘要并更新 `.compact.json`。

```text
ai-runtime session compact <sessionId>
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

### `ai-runtime session archive <sessionId>`

将会话标记为归档状态（不删除数据，只从活跃列表隐藏）。

```text
ai-runtime session archive <sessionId>
  --workspace <path>

ai-runtime session unarchive <sessionId>
  --workspace <path>
```

---

## 5. 数据库维护命令

> **安全说明**：所有数据库维护命令（db check 除外）在执行前检查是否有运行中的 Runtime 实例持有同一工作区的排他锁。若有，命令报错退出，建议先停止 Runtime 或使用 `ctl` 命令在线操作。`--force-lock` 参数可跳过锁检查（危险，可能损坏数据库，仅用于 Runtime 进程已死但锁未释放的场景）。

### `ai-runtime db check`

只读检查命令，不修改任何文件或数据库，不截断文件。发现问题时只报告，不自动修复。

```text
ai-runtime db check
  --workspace <path>
  --verbose                显示每个检查项详情

检查项（全部只读，不修改数据）：
  ✓ state.db schema 版本与当前 Runtime 兼容
  ✓ state.db WAL 状态正常
  ✓ messages/ 中的每个 .jsonl 文件在 state.db 中有对应 Session 记录
  ✓ state.db 中的每个 Session 在 messages/ 中有对应 .jsonl 文件
  ✓ .jsonl 文件末行可解析为有效 JSON（不修改末行）
  ✓ blobs/ 中的文件 SHA-256 与 state.db 记录一致
  ! 警告：发现 3 个孤立 blob 文件（未被任何消息引用）
  ! 警告：发现 1 个 messages/ 文件末行未完整写入（可通过 db repair 自愈）

如需修复，使用 db repair 命令（独立命令，有确认和备份提示）。
```

---

### `ai-runtime db rebuild`

从 `messages/` 文件扫描重建 `state.db` 的 Session 和消息索引。用于 `state.db` 损坏或丢失的恢复场景。

```text
ai-runtime db rebuild
  --workspace <path>
  --dry-run                只扫描并报告，不写入数据库
  --confirm                实际执行时必须显式确认（会覆盖现有数据）

注意：
  - 只能恢复消息内容；Run 状态、工具调用幂等记录和权限记录无法从文件恢复
  - 恢复后的 Session 状态为 Recovered，宿主需重新判断是否存在未完成 Run
```

---

### `ai-runtime db vacuum`

```text
ai-runtime db vacuum
  --workspace <path>
  执行 SQLite VACUUM，压缩数据库文件大小（需要短暂排他锁）
```

---

### `ai-runtime db backup`

```text
ai-runtime db backup
  --workspace <path>
  --output <path>          备份目标路径（缺省为 {data-dir}/backups/{timestamp}.db）
  --include-messages       是否同时压缩备份 messages/ 目录（默认不包含）
  使用 SQLite 在线备份 API，不需要停止 Runtime
```

---

### `ai-runtime db migrate`

```text
ai-runtime db migrate
  --workspace <path>
  --to <version>           目标 schema 版本（缺省为当前 Runtime 支持的最新版本）
  --dry-run                只检查迁移路径，不执行
  启动时 Runtime 自动执行此命令；手动调用用于预检或强制迁移
```

---

## 6. 存储维护命令

### `ai-runtime storage gc`

清理 `blobs/` 目录中未被任何消息或 Run 引用的孤立文件。

```text
ai-runtime storage gc
  --workspace <path>
  --dry-run                只列出将被删除的文件，不实际删除
  --older-than <days>      只清理超过指定天数的孤立 blob（默认 7 天）
  --confirm
```

---

### `ai-runtime storage check`

```text
ai-runtime storage check
  --workspace <path>
  --verify-hashes          重新计算所有 blob 的 SHA-256 并与 state.db 比对（耗时）
```

---

## 7. 在线控制命令（`ctl`）

`ctl` 子命令连接到**正在运行的** Runtime 实例，通过控制通道发送命令。

### `ai-runtime ctl status`

```text
ai-runtime ctl status
  --instance <id>          可选；Runtime 实例名（缺省自动发现当前用户下的实例）
  --json

输出示例：
  实例：netor.ai.runtime.a1b2c3d4
  版本：1.0.0 | 协议：1.2
  工作区：E:\Projects\my-project
  活动 Run：3 | 队列 Run：0 | 完成 Run：47
  事件缓冲：12 MiB / 32 MiB
  运行时长：2h 14m
```

---

### `ai-runtime ctl sessions`

```text
ai-runtime ctl sessions
  --instance <id>
  列出当前 Runtime 实例管理的活动会话和最近完成的 Run
```

---

### `ai-runtime ctl runs`

```text
ai-runtime ctl runs
  --instance <id>
  --session <sessionId>    可选；只显示指定会话的 Run
  列出活动 Run 及其状态、已用时和当前阶段
```

---

### `ai-runtime ctl cancel <runId>`

```text
ai-runtime ctl cancel <runId>
  --instance <id>
  --reason <text>          可选；取消原因（记入审计日志）
  向 Runtime 发送 run.cancel 命令
```

---

### `ai-runtime ctl credential update`

向运行中的 Runtime 推送新凭据，用于 Provider API Key 续期。

```text
ai-runtime ctl credential update
  --instance <id>
  --provider <providerId>
  --profile <profileId>    可选
  --api-key-env <envVar>   从环境变量读取新 API Key（推荐，不出现在命令行）
  --expires-at <datetime>  可选；新凭据的过期时间
```

---

## 8. 交互模式斜杠命令

在 `ai-runtime run`（无 `--input` 参数）的交互模式下，输入 `/` 开头的命令控制当前会话。

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
  "diagnosticId": "diag_xxx"
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
  }
}
```

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
