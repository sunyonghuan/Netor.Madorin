# Madorin CLI 命令矩阵

> 状态：阶段 7 冻结基线
>
> 适用版本：程序 `0.1.0`、协议 `1.1`、SQLite Schema `14`
>
> 机器真值：`tests/Madorin.AI.Runtime.EndToEnd.Tests/Snapshots/stage7-cli-command-matrix.v1.json`

本文冻结当前 46 条命令路径、局部参数、默认值、数据影响、锁要求、主要退出码和可解析示例。精确帮助文本及参数集合由机器快照的 SHA-256 和 `Stage7CommandMatrixTests` 约束；本文用于人工评审和运维判断。

## 1. 通用契约

所有命令继承以下全局参数：

| 参数 | 值 | 默认值 / 语义 |
| --- | --- | --- |
| `--workspace` | 路径 | 独立命令默认当前目录；`serve` 必须显式传入 |
| `--data-dir` | 路径 | 独立命令默认用户数据区中的工作区隔离目录；`serve` 默认 `<workspace>/.madorin` |
| `--log-level` | 枚举 | 使用当前配置的日志级别 |
| `--no-color` | 开关 | 非 TTY 自动禁用 ANSI；显式传入时始终禁用 |
| `--json` | 开关 | 使用统一 JSON 信封；`run` 的 JSONL 使用 `--output-format jsonl` |

固定退出码：`0` 成功、`1` 通用错误、`2` 参数或配置错误、`3` 认证失败、`4` 工作区或数据错误、`5` Runtime 连接失败、`130` 用户中断。

锁名称采用以下缩写：

| 缩写 | 含义 |
| --- | --- |
| 无 | 不获取排他写锁；只读命令不得修改数据 |
| 配置写锁 | 当前用户配置和 Agent 目录的跨进程写锁，配合重新读取和原子替换 |
| 记忆写锁 | 全局或项目 `memory.md` 的跨进程写锁，配合原子替换 |
| 工作区写锁 | 规范化工作区唯一写实例锁；实例名、Pipe 或数据目录不能绕过 |
| Runtime 绑定 | 连接目标 Runtime 的实例绑定；服务端按实例和权限约束操作 |

## 2. 根命令、服务与诊断

| 命令 | 局部参数与默认值 | 数据影响 | 锁 | 主要失败码 | 示例 |
| --- | --- | --- | --- | --- | --- |
| `madorin` | 无参数进入单行 REPL | 按交互操作写 Session/Run | 工作区写锁 | `2/4/130` | `madorin` |
| `madorin run` | `--provider --model --agent --mode --session --input --input-file --output-file --output-format --no-stream --timeout`；模式默认 `expert`，输出默认 `text`，默认流式 | 写 Session、Run、消息、事件及工具审计 | 工作区写锁 | `2/4/130` | `madorin run --input "hello" --no-stream` |
| `madorin serve` | `--config-dir --log-dir --instance --pipe-prefix --max-runs`；Pipe 默认 `madorin.ai.runtime`，并发默认 `4` | 持续写 Runtime 状态、Session、Run、消息和事件 | 工作区写锁，进程全生命周期持有 | `2/3/4` | `madorin serve --workspace D:\work` |
| `madorin version` | 无 | 只读 | 无 | `1` | `madorin version --json` |
| `madorin doctor` | `--provider --fix --yes`；`--yes` 仅与 `--fix` 合用 | 默认只读；`--fix` 可修复配置权限、数据和日志问题，修复前保留备份或恢复点 | 修复时按目标获取配置写锁或工作区写锁 | `1/2/4` | `madorin doctor --fix --yes` |

## 3. 配置与 Agent

| 命令 | 局部参数与默认值 | 数据影响 | 锁 | 主要失败码 | 示例 |
| --- | --- | --- | --- | --- | --- |
| `madorin config` | 子命令组 | 无直接行为 | 无 | `2` | `madorin config show` |
| `madorin config init` | 无；交互输入 Secret 时不回显 | 创建或更新个人配置 | 配置写锁 | `2/4` | `madorin config init` |
| `madorin config edit` | 无；交互编辑 | 更新个人配置 | 配置写锁 | `2/4` | `madorin config edit` |
| `madorin config show` | 无 | 只读并脱敏 Secret | 无 | `2/4` | `madorin config show --json` |
| `madorin config validate` | 无 | 只读校验 | 无 | `2/4` | `madorin config validate` |
| `madorin agent` | 子命令组 | 无直接行为 | 无 | `2` | `madorin agent list` |
| `madorin agent list` | 无 | 只读 | 无 | `2/4` | `madorin agent list` |
| `madorin agent create [agentId]` | `--name --description --system-prompt --provider --model --temperature`；`agentId` 可省略并交互输入 | 创建 Agent 定义 | 配置写锁 | `2/4` | `madorin agent create demo --name Demo` |
| `madorin agent edit <agentId>` | `--name --description --system-prompt --provider --model --temperature` | 更新 Agent 定义 | 配置写锁 | `2/4` | `madorin agent edit demo --model gpt-5` |
| `madorin agent delete <agentId>` | `--replacement --yes` | 删除 Agent 并可迁移默认引用 | 配置写锁 | `2/4` | `madorin agent delete demo --yes` |

## 4. 记忆与 Session

`memory` 的 `<scope>` 只接受 `global` 或 `project`。全局目标为 `~/.madorin/memory.md`，项目目标为 `<workspace>/.madorin/memory.md`。

| 命令 | 局部参数与默认值 | 数据影响 | 锁 | 主要失败码 | 示例 |
| --- | --- | --- | --- | --- | --- |
| `madorin memory` | 子命令组 | 无直接行为 | 无 | `2` | `madorin memory show project` |
| `madorin memory show <scope>` | 无 | 只读 | 无 | `2/4` | `madorin memory show global` |
| `madorin memory init <scope>` | 无 | 缺失时创建记忆文件 | 记忆写锁 | `2/4` | `madorin memory init project` |
| `madorin memory add <scope>` | 从标准输入读取单行内容 | 追加记忆内容 | 记忆写锁 | `2/4` | `"Prefer UTC" \| madorin memory add project` |
| `madorin memory edit <scope>` | 无；调用受控编辑流程 | 替换记忆内容 | 记忆写锁 | `2/4` | `madorin memory edit project` |
| `madorin memory clear <scope>` | `--yes` | 清空记忆内容 | 记忆写锁 | `2/4` | `madorin memory clear project --yes` |
| `madorin session` | 子命令组 | 无直接行为 | 无 | `2` | `madorin session list` |
| `madorin session list` | `--status --mode --since --search --limit --cursor`；状态默认 `active`，数量默认 `20` | 只读 | 无 | `2/4` | `madorin session list --status active --limit 20` |
| `madorin session show <sessionId>` | `--messages` | 只读 | 无 | `2/4` | `madorin session show s1 --messages` |
| `madorin session export <sessionId>` | `--format --output --include-reasoning --include-tool-calls`；格式默认 `markdown`，文件写入使用原子替换 | 读取 Session；可创建导出文件 | 无工作区写锁；输出文件自身原子提交 | `2/4` | `madorin session export s1 --format markdown --output s1.md` |
| `madorin session archive <sessionId>` | 无 | 将 Session 标记为归档 | 工作区写锁 | `2/4` | `madorin session archive s1` |
| `madorin session unarchive <sessionId>` | 无 | 恢复 Session 为活动状态 | 工作区写锁 | `2/4` | `madorin session unarchive s1` |
| `madorin session compact <sessionId>` | `--strategy --provider --model --keep-last-tokens --dry-run --force`；策略默认 `summary` | dry-run 只展示计划；实际执行生成或刷新上下文缓存 | 实际执行使用工作区写锁 | `2/4` | `madorin session compact s1 --dry-run` |
| `madorin session delete <sessionId>` | `--confirm --include-blobs`；必须确认 | 删除权威索引并创建可恢复审计/恢复点 | 工作区写锁 | `2/4` | `madorin session delete s1 --confirm` |

## 5. 数据库与 Blob 维护

维护命令不直接暴露数据库物理路径给 SDK 宿主。写操作先获取工作区排他锁；备份或恢复点创建失败时不得进入修改阶段。

| 命令 | 局部参数与默认值 | 数据影响 | 锁 | 主要失败码 | 示例 |
| --- | --- | --- | --- | --- | --- |
| `madorin db` | 子命令组 | 无直接行为 | 无 | `2` | `madorin db check` |
| `madorin db check` | `--verbose` | 严格只读完整性检查，断言零写入 | 无 | `1/2/4` | `madorin db check --verbose` |
| `madorin db repair` | `--dry-run --yes` | 仅修复已识别的可恢复损坏；实际执行前备份 | 实际执行使用工作区写锁 | `1/2/4` | `madorin db repair --dry-run` |
| `madorin db rebuild` | `--dry-run --confirm` | 从权威消息与恢复数据重建索引；实际执行前备份 | 实际执行使用工作区写锁 | `1/2/4` | `madorin db rebuild --dry-run` |
| `madorin db vacuum` | 无 | 在线压缩数据库；执行前备份 | 工作区写锁 | `1/4` | `madorin db vacuum` |
| `madorin db backup` | `--output --include-messages` | 使用 SQLite 在线备份 API 创建备份 | 工作区写锁 | `1/2/4` | `madorin db backup --output backup.zip` |
| `madorin db migrate` | `--to --dry-run` | 预检并迁移 Schema；实际执行前备份 | 实际执行使用工作区写锁 | `1/2/4` | `madorin db migrate --dry-run` |
| `madorin storage` | 子命令组 | 无直接行为 | 无 | `2` | `madorin storage check` |
| `madorin storage check` | `--verify-hashes` | 只读检查消息和 Blob 引用；可选完整哈希校验 | 无 | `1/2/4` | `madorin storage check --verify-hashes` |
| `madorin storage gc` | `--dry-run --older-than --confirm`；龄期默认 `7` 天，未确认时默认 dry-run | 实际执行把可回收 Blob 移入恢复点，不立即永久删除 | 实际执行使用工作区写锁 | `1/2/4` | `madorin storage gc --dry-run --older-than 7` |

## 6. 在线控制

`ctl` 只通过 Client SDK 和 Runtime 协议操作目标实例，不直接读取或修改 Runtime 内部文件。`--instance` 绑定实例；未显式指定时只允许解析当前工作区中唯一可用实例。

| 命令 | 局部参数与默认值 | 数据影响 | 锁 | 主要失败码 | 示例 |
| --- | --- | --- | --- | --- | --- |
| `madorin ctl` | 子命令组 | 无直接行为 | 无 | `2` | `madorin ctl status` |
| `madorin ctl status` | `--instance` | 只读 Runtime 状态 | Runtime 绑定 | `2/3/5` | `madorin ctl status --instance runtime-1` |
| `madorin ctl sessions` | `--instance` | 只读 Session 列表 | Runtime 绑定 | `2/3/5` | `madorin ctl sessions` |
| `madorin ctl runs` | `--instance --session` | 只读 Run 列表 | Runtime 绑定 | `2/3/5` | `madorin ctl runs --session s1` |
| `madorin ctl cancel <runId>` | `--instance --reason` | 请求取消目标 Run | Runtime 绑定；按 `(runtimeInstanceId, runId)` 路由 | `2/3/5` | `madorin ctl cancel run-1 --reason "operator request"` |
| `madorin ctl credential` | 子命令组 | 无直接行为 | 无 | `2` | `madorin ctl credential update --help` |
| `madorin ctl credential update` | `--instance --run --provider --profile --api-key-env --expires-at`；`--run --provider --api-key-env` 必填 | 从环境变量读取 Secret 并提交给等待凭据的 Run，不回显明文 | Runtime 绑定 | `2/3/5` | `madorin ctl credential update --run run-1 --provider openai --api-key-env OPENAI_API_KEY` |

## 7. 参数约束

以下组合固定返回退出码 `2`：

- `run --input` 与 `--input-file` 同时出现。
- `run --output-file` 未配合 `--input` 或 `--input-file`。
- `run` 在交互模式下使用 JSON、JSONL 或 `--no-stream`。
- `run --json` 与非 JSON 的 `--output-format` 同时出现。
- `serve` 缺少 `--workspace`。
- `doctor --yes` 未配合 `--fix`。
- `storage gc --dry-run` 与 `--confirm` 同时出现。
- `session delete` 缺少 `--confirm`。
- `ctl credential update` 缺少 `--run`、`--provider` 或 `--api-key-env`。

所有必填位置参数、上述条件必填与互斥组合均由 `Stage7CommandMatrixTests` 自动验证。危险维护命令的确认、备份失败、锁冲突、中途故障和恢复点保留由对应 EndToEnd/Persistence 测试验证。

## 8. 快照与变更规则

1. 新增、删除或重命名命令时，必须同时更新本文、命令矩阵快照和命令树测试。
2. 参数、默认值、usage 或帮助文本变化会改变对应 SHA-256，必须作为公开 CLI 契约变更审查。
3. JSON 输出必须符合 `stage7-cli-json-envelope.v1.json`；业务字段进入 `data`，日志和进度不得污染 stdout。
4. `db check`、`storage check`、查询类 `session/ctl` 命令保持只读；不得为简化实现改为直接修改内部文件。
5. `db repair` 是独立的受限修复命令，不得改为 `db rebuild` 的别名。
