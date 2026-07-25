# 阶段 7：CLI、Client SDK 与参考宿主执行计划 : 100%

> 状态：已完成 · 54 / 54
>
> 主进度口径：[07-CLI-ClientSDK与参考宿主](../执行步骤/07-CLI-ClientSDK与参考宿主.md) 的 74 项执行任务。历史 `53 / 74` 和 `72%` 只作为审计基线；没有实现与测试证据的项目不得勾选。
>
> 约束：保留阶段 6C 的全部未提交修改；阶段 7 不创建 Git 提交；每批 C# 修改后刷新 CodeMap overlay。

## 1. 基线审计与契约冻结 : 100%

- [√] 审计 07 文档、CLI 命令规范、命令树与当前占位实现。
- [√] 建立命令、参数、互斥、默认值、锁、输出和退出码矩阵。
- [√] 将 `db repair` 冻结为独立维护语义，并同步命令规范。
- [√] 校正 07 主清单与执行步骤总览中的阶段状态和进度。
- [√] 记录阶段 6C 未提交差异，确认阶段 7 不覆盖其文件和行为。

> 阶段 7 全程保留阶段 6C 的未提交文件；最终 `git status` 仍保留 06C 文档和实现差异，阶段 7 修改范围由各批次文档与本文件修改清单独立记录。

## 2. Contracts、Services 与 Server : 100%

- [√] 补齐 Session 查询、消息、Selection、resume/rehydrate 公共 DTO 与处理器。
- [√] 补齐维护、在线控制、工具/权限/审批与 callId 查询 DTO 与处理器。
- [√] 补齐宿主回调 DTO、错误映射和能力声明。
- [√] 将全部新增协议类型注册到 `RuntimeJsonContext`。
- [√] 增加协议快照、往返和处理器测试。

> 批次 1、2、3、8、19、21、28、29 已分别闭合 Client/回调、Session、在线控制、参考宿主、公共传输边界和生命周期异常映射；Protocol `49 / 49`、EndToEnd `261 / 261` 及全解决方案回归共同验证协议元数据、处理器与高层 API。

## 3. Client SDK : 100%

- [√] 实现 `AttachExisting`、`StartIfMissing`、`AlwaysStart` 启动策略。
- [√] 扩充 `RuntimeClientOptions`、实例绑定信息和稳定异常模型。
- [√] 使用 `ProcessStartInfo.ArgumentList` 与受控 secret 输入启动并完成认证、初始化、能力和版本检查。
- [√] 实现 Session、Run、Selection、工具目录、权限、审批、工具调用和 callId 查询高层 API。
- [√] 实现带实例归属的事件流、显式 GSN 确认、重连和 Runtime 切换隔离。
- [√] 实现有界宿主回调调度，统一超时、取消和异常映射。
- [√] 保证 Dispose 只清理当前句柄的循环、连接与所拥有进程。
- [√] 增加三种启动策略、多实例、重连、取消、回调与 Dispose 端到端测试。

## 4. CLI `run` 与 REPL : 100%

- [√] 完成单次 `run` 的输入、会话、三模式、选择、超时、流式和文件输出。
- [√] 完成无参数首次配置与单行 REPL。
- [√] 完成 Selection、记忆、Session、恢复、压缩和工具斜杠命令。
- [√] 完成两阶段 Ctrl+C、EOF、取消后继续同一 Session。
- [√] 增加首次启动、Selection、记忆、恢复、压缩、EOF 与 Ctrl+C 测试。

## 5. 配置、Agent、记忆与诊断 : 100%

- [√] 复核并补齐 `config`、`agent`、`memory`、`version`、`doctor`。
- [√] 保证 Key 无命令行明文、输入不回显、展示与诊断脱敏。
- [√] 保证 `doctor --fix` 先备份、展示计划并确认。
- [√] 增加配置、Agent、记忆、诊断和安全测试。

## 6. Session、维护与在线控制 : 100%

- [√] 完成 `session` 列表、查询、导出、删除、归档和压缩。
- [√] 完成 `db check/repair/rebuild/vacuum/backup/migrate`。
- [√] 完成 `storage check/gc`。
- [√] 完成 `ctl status/sessions/runs/cancel/credential update`。
- [√] 保证所有业务走服务或 Client API，CLI 不直接修改 Runtime 内部文件。
- [√] 增加锁冲突、备份失败、dry-run、拒绝确认、中途故障和恢复点测试。

> 批次 15 已完成 `db rebuild`，批次 16 已完成 `db vacuum`，批次 17 已完成 `db migrate`，批次 18 已完成 `storage check/gc`，批次 19 已完成 `ctl` 在线控制；批次 23 已完成 Session 维护审计，并复核 CLI 只通过 LocalRuntime、维护服务或 Client API 操作 Runtime 数据。
- [√] 断言 `db check` 零写入。

## 7. 输出、Schema 与退出码 : 100%

- [√] 统一人类输出、JSON/JSONL 信封、stdout/stderr 和 ANSI 行为。
- [√] 实现输出文件原子替换，失败不覆盖旧结果。
- [√] 固定退出码并覆盖完整命令树帮助、参数、示例和 JSON 快照。
- [√] 断言没有成功返回的 `NotImplemented` 命令。

## 8. SampleHost : 100%

- [√] 只引用 Client 与必要 Contracts，禁止 Provider、MAF、MEAI、SQLite 和 Server 实现依赖。
- [√] 同时启动三个工作区、三个 Runtime 和三个独立 Client。
- [√] 演示三模式并发、事件分流与确认、取消、工具/审批回调和 callId 查询。
- [√] 演示 Selection、resume/rehydrate 与 Runtime 重启恢复。
- [√] 增加参考宿主依赖边界和端到端测试。

## 9. 文档 : 100%

- [√] 补齐安装、PATH、双击、首次配置和 CLI 使用文档。
- [√] 补齐宿主集成、生命周期、回调、恢复和升级文档。
- [√] 补齐安全、数据库和 Blob 运维文档。
- [√] 更新 SampleHost README、07 主清单和执行步骤总览。

## 10. 最终验证 : 100%

- [√] 全量 MSTest 通过；真实 Provider 只在缺少外部凭据时明确跳过。
- [√] Debug `--no-restore -warnaserror -m:1` 为 0 警告、0 错误。
- [√] Release `--no-restore -warnaserror -m:1` 为 0 警告、0 错误。
- [√] Windows `win-x64` Release Native AOT 发布无 trimming/AOT 警告。
- [√] 实测 `madorin --help`、无参数 REPL、PATH 启动和 Windows 双击启动。
- [√] 记录协议版本、Schema、测试数、AOT 警告和残余风险。
- [√] 07 主清单达到 `74 / 74`，测试要求和完成标准全部有证据。

## 11. 验证记录

| 日期 | 范围 | 命令/证据 | 结果 |
| --- | --- | --- | --- |
| 2026-07-24 | SDK 基线 | `.NET SDK 10.0.301` | 已确认 |
| 2026-07-24 | CodeMap | baseline `369c49099fd03afd05db23e540096b3bf18f3924`，overlay `session-stage7` | 已建立；默认 `session` 被其他进程锁定 |
| 2026-07-24 | Client 生命周期 | `RuntimeClientLifecycleStage7Tests` | 7 / 7 通过 |
| 2026-07-24 | 多实例异常模型 | `MultiInstanceProcessTests` | 1 / 1 通过；认证失败使用稳定 SDK 异常，初始化未就绪仅按可重试连接错误处理 |
| 2026-07-24 | EndToEnd 回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 118 / 118 通过 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | Client 高层 API 与宿主回调 | `RuntimeClientHighLevelStage7Tests` | 5 / 5 通过 |
| 2026-07-24 | EndToEnd 回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 123 / 123 通过 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `1` | 66 个变更文件、1388 个符号已刷新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | Session 列表协议 | `SessionListContractStage7Tests` | 5 / 5 通过 |
| 2026-07-24 | Session 列表持久化与 Schema | `SessionListStage7Tests`、`SessionListSchemaStage7Tests` | 11 / 11 通过 |
| 2026-07-24 | Session 列表 EndToEnd | `SessionListStage7Tests` | 1 / 1 通过；真实 RuntimeServer、Named Pipe 与 RuntimeClient |
| 2026-07-24 | Session 列表性能回归 | `ListSessionsAsync_100kSessions_UsesKeysetIndexWithinBaseline` | 1 / 1 通过，测试体耗时 644 ms |
| 2026-07-24 | EndToEnd.Tests 严格构建 | `dotnet build ... --no-restore -c Debug -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `15` | 新增 EndToEnd 测试已刷新 |
| 2026-07-24 | LocalRuntime 三模式 | `StandaloneRunModesStage7Tests` | 2 / 2 通过；Expert、Meeting、Work 均真实落盘 SQLite/JSONL，且各只有一个 Completed Run |
| 2026-07-24 | CLI 与 LocalRuntime 回归 | `StandaloneRunModesStage7Tests`、`StandaloneCliTests`、`StandaloneLocalRuntimeTests` | 分项 2 / 2、9 / 9、7 / 7；同进程合跑 18 / 18 通过 |
| 2026-07-24 | EndToEnd.Tests 严格构建 | `dotnet build ... --no-restore -c Debug -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `21` | 102 个变更文件、1526 个符号已刷新 |
| 2026-07-24 | 进度复核 | 批次 4 为 6 / 6 | 完整 `run` 条目仍含后续子批次能力；执行计划保持 8 / 54，07 主清单保持 17 / 96 |
| 2026-07-24 | `run` 输入输出 | `StandaloneRunInputOutputStage7Tests` | 6 / 6 通过；覆盖严格 UTF-8、三种格式、流/非流、stdout/stderr 与原子输出文件 |
| 2026-07-24 | CLI、三模式与 LocalRuntime 回归 | 四个相关测试类同进程合跑 | 24 / 24 通过 |
| 2026-07-24 | EndToEnd 全量回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 最终 132 / 132 通过；首次运行的既有 Meeting 重启初始化瞬时失败已单独重跑并在全量重跑中通过 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `30` | 109 个变更文件、1538 个符号已刷新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 5 为 6 / 6 | 单次 `run` 条目闭合；执行计划更新为 9 / 54，07 主清单更新为 18 / 96 |
| 2026-07-24 | REPL 与两阶段中断 | `StandaloneReplStage7Tests` | 5 / 5 通过；核心命令、Selection、Session 恢复、EOF、首次/二次/空闲 Ctrl+C 和取消后继续均闭合 |
| 2026-07-24 | CLI、三模式、LocalRuntime 与 REPL 回归 | 五个相关测试类同进程合跑 | 29 / 29 通过 |
| 2026-07-24 | EndToEnd 全量回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 137 / 137 通过 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `36` | 批次 6 最终 C# 修改已刷新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 6 为 6 / 6 | 执行计划更新为 11 / 54；07 执行任务更新为 23 / 74，总清单更新为 23 / 96 |
| 2026-07-24 | 配置、Agent、记忆与版本命令矩阵 | `Stage7CommandMatrixTests` | 5 / 5 通过；根命令不再公开旧 `providers`，Key 参数禁用，版本输出与配置展示脱敏 |
| 2026-07-24 | 管理命令定向回归 | `Stage7CommandMatrixTests`、`StandaloneCliTests`、`StandaloneConfigTests`、`StandaloneConfigurationSafetyTests` | 32 / 32 通过 |
| 2026-07-24 | 事件重连竞态回归 | `ReadEventsAsync_WithoutExplicitAcknowledgement_ReplaysAfterReconnect` | 旧 Pipe 缓冲允许先排空，未确认 GSN 仍会在重连后重放；连续 3 / 3 通过 |
| 2026-07-24 | EndToEnd 全量回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 142 / 142 通过 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `40` | 批次 7 最终 C# 修改已刷新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 7 为 6 / 6 | 执行计划更新为 12 / 54；07 执行任务更新为 28 / 74，总清单更新为 28 / 96 |
| 2026-07-24 | Session 列表与详情 | `StandaloneSessionCommandsStage7Tests` | 7 / 7 通过；游标分页、筛选、空结果、详情、消息摘要脱敏、不存在和非法参数均闭合 |
| 2026-07-24 | Client 初始化就绪竞态返工 | 两个曾失败进程测试 | 2 / 2 通过；初始化仅对可重试的 `-32601` 未就绪错误做有界重试 |
| 2026-07-24 | 事件重连稳定性复核 | `ReadEventsAsync_WithoutExplicitAcknowledgement_ReplaysAfterReconnect` | 连续 20 / 20 通过 |
| 2026-07-24 | EndToEnd 全量回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 149 / 149 通过 |
| 2026-07-24 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build --no-restore -m:1` | 716 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-24 | 严格构建 | `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1` | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `48` | 119 个文件重建索引、1577 个符号更新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 8 为 6 / 6 | 执行计划保持 12 / 54；07 执行任务更新为 30 / 74，总清单更新为 30 / 96 |
| 2026-07-24 | Session 导出 | `StandaloneSessionExportStage7Tests` | 5 / 5 通过；默认脱敏、显式包含、TXT、逐行可解析 JSONL、原子替换和稳定错误映射均闭合 |
| 2026-07-24 | Session 与命令矩阵回归 | 4 个相关 EndToEnd 测试类 | 18 / 18 通过 |
| 2026-07-24 | 事件通道就绪竞态返工 | 两个断线重连用例 | 修复前全量稳定超时；挂载与 Outbox 重放纳入同一派发锁后定向 2 / 2、EndToEnd 154 / 154 通过 |
| 2026-07-24 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx --no-restore -m:1` | 721 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-24 | 严格构建 | Debug/Release `--no-restore -warnaserror -m:1` | 两种配置均为 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `56` | 123 个文件重建索引、1580 个符号更新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 9 与事件通道返工均完成 | 执行计划保持 12 / 54；07 执行任务更新为 31 / 74，总清单更新为 31 / 96 |
| 2026-07-24 | Session 归档红灯 | `SessionArchive*` | 0 / 3；全部命中原 `NotImplemented` 成功占位 |
| 2026-07-24 | Session 归档与恢复 | `SessionArchive*` | 3 / 3 通过；状态切换、幂等、列表/详情联动和缺失 Session 错误均闭合 |
| 2026-07-24 | Session 与命令矩阵回归 | 4 个相关 EndToEnd 测试类 | 29 / 29 通过 |
| 2026-07-24 | EndToEnd 全量回归 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 157 / 157 通过 |
| 2026-07-24 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build --no-restore -m:1` | 724 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-24 | 严格构建 | Debug/Release `--no-restore -warnaserror -m:1` | 两种配置均为 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `57` | 本批 5 个 C# 文件、135 个符号更新 |
| 2026-07-24 | 差异检查 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |
| 2026-07-24 | 进度复核 | 批次 10 为 6 / 6 | delete/compact/审计尚未闭合；执行计划保持 12 / 54，07 执行任务保持 31 / 74，总清单保持 31 / 96 |
| 2026-07-25 | Session 删除红灯 | 4 个新增删除场景 | 0 / 4；命中旧的成功 `NotImplemented` 占位或缺失的 `--include-blobs` 参数 |
| 2026-07-25 | Session 删除定向回归 | `StandaloneSessionCommandsStage7Tests`、删除底层故障与恢复用例 | 14 / 14、2 / 2 通过 |
| 2026-07-25 | Persistence 回归 | `Madorin.AI.Runtime.Persistence.Tests` | 169 / 169 通过 |
| 2026-07-25 | EndToEnd 回归 | `Madorin.AI.Runtime.EndToEnd.Tests` | 161 / 161 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx --no-restore --logger "console;verbosity=minimal" -m:1` | 730 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建 | Debug/Release `--no-restore -warnaserror -m:1` | 两种配置均为 0 警告、0 错误 |
| 2026-07-25 | CodeMap | workspace `session`，overlay revision `63` | 130 个变更文件、1641 个符号已刷新 |
| 2026-07-25 | 差异检查 | `git diff --check` | 通过；仅有工作树既有的 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 11 全部检查项闭合 | 执行计划保持 12 / 54；07 执行任务更新为 32 / 74，总清单更新为 32 / 96 |
| 2026-07-25 | Session 压缩红灯 | `StandaloneSessionCommandsStage7Tests` 新增 6 个场景 | 既有 14 项通过，新增 6 项失败；命中缺少参数、校验和旧成功占位 |
| 2026-07-25 | Session 压缩定向回归 | `StandaloneSessionCommandsStage7Tests`、`ConversationStoreTests` | 21 / 21、20 / 20 通过；覆盖 dry-run、三策略、选择覆盖、缓存、force、历史失效、Provider 失败、工具轮次和稳定错误 |
| 2026-07-25 | Persistence 与 EndToEnd 回归 | 两个测试项目 | 171 / 171、168 / 168 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx --no-restore -v minimal` | 739 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告 |
| 2026-07-25 | CodeMap 与差异检查 | workspace `session` overlay revision `66`；`git diff --check` | 已刷新；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 12 全部检查项闭合 | 执行计划更新为 13 / 54；07 执行任务更新为 33 / 74，总清单更新为 33 / 96 |
| 2026-07-25 | 数据库检查与修复红灯 | `StandaloneDatabaseCommandsStage7Tests` 基础 9 项 | 8 项命中旧成功占位，1 项因旧命令解析失败；实现后基础 9 / 9 通过 |
| 2026-07-25 | 数据库检查与修复定向回归 | `StandaloneDatabaseCommandsStage7Tests` | 14 / 14 通过；覆盖严格零写入、活动 WAL、无 SHM WAL、确认、dry-run、锁、备份及中途故障恢复点 |
| 2026-07-25 | Persistence 与 EndToEnd 回归 | 两个测试项目 | 171 / 171、182 / 182 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build --no-restore -m:1` | 753 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告 |
| 2026-07-25 | CodeMap 与差异检查 | workspace `session` overlay revision `83`；`git diff --check` | 143 个文件重建索引、1685 个符号更新；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 13 全部检查项闭合 | 执行计划更新为 16 / 54；07 执行任务更新为 35 / 74，总清单更新为 35 / 96 |
| 2026-07-25 | 数据库在线备份红灯 | `StandaloneDatabaseCommandsStage7Tests` 新增 4 项 | 原有 14 项通过，新增 4 项命中旧成功占位或缺失参数 |
| 2026-07-25 | 数据库在线备份定向回归 | `StandaloneDatabaseCommandsStage7Tests` | 21 / 21 通过；覆盖活动 WAL、默认/显式输出、消息目录相对路径、SQLite-only 无锁、锁冲突、拒绝覆盖和归档失败清理 |
| 2026-07-25 | Persistence 与 EndToEnd 回归 | 两个测试项目 | 171 / 171、189 / 189 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-build --no-restore -m:1` | 760 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告；发布制品帮助实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与差异检查 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `88`；`git diff --check` | 本批未变更协议/Schema；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 14 全部检查项闭合 | 执行计划保持 16 / 54；07 执行任务更新为 36 / 74，总清单更新为 36 / 96 |
| 2026-07-25 | 数据库索引重建红灯 | `StandaloneDatabaseCommandsStage7Tests` 新增 8 项 | 原有 21 项通过，新增 8 项失败；命中缺失选项、确认、锁、全量替换、partial 和恢复点错误模型 |
| 2026-07-25 | 数据库索引重建定向回归 | `StandaloneDatabaseCommandsStage7Tests` | 29 / 29 通过；覆盖 dry-run 零写入/无锁、确认、拒绝、锁冲突、全量替换、`Recovered`、中间损坏 partial 和恢复点故障 |
| 2026-07-25 | Persistence 与 EndToEnd 回归 | 两个测试项目 | 171 / 171、197 / 197 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx --no-restore -m:1` | 768 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告；发布制品总帮助与 `db rebuild` 帮助实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与差异检查 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `95`；`git diff --check` | 本批 7 个 C# 文件、142 个符号已刷新；本批未变更协议/Schema；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 15 全部检查项闭合 | 执行计划保持 16 / 54；07 执行任务更新为 37 / 74，总清单更新为 37 / 96 |
| 2026-07-25 | 数据库压缩红灯 | `StandaloneDatabaseCommandsStage7Tests` 新增 3 项 | 全部命中旧成功 `NotImplemented` 占位 |
| 2026-07-25 | 数据库压缩定向与 EndToEnd 回归 | `DbVacuum_*`、`StandaloneDatabaseCommandsStage7Tests`、EndToEnd 全量 | 3 / 3、32 / 32、200 / 200 通过 |
| 2026-07-25 | 解决方案全量回归 | `dotnet test .\Madorin.AI.Runtime.slnx --no-restore -m:1` | 771 个通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告；发布制品总帮助与 `db vacuum` 帮助实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与差异检查 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `98`；`git diff --check` | 本批 4 个 C# 文件、21 个符号已刷新；本批未变更协议/Schema；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 16 全部检查项闭合 | 执行计划保持 16 / 54；07 执行任务保持 37 / 74，总清单保持 37 / 96 |
| 2026-07-25 | 数据库迁移红灯 | `StandaloneDatabaseCommandsStage7Tests` 新增 9 项 | 9 / 9 失败；命中缺失 `--to`/`--dry-run` 参数或旧成功 `NotImplemented` 占位 |
| 2026-07-25 | 数据库迁移定向与数据库命令回归 | `DbMigrate_*` 与命令表面、`StandaloneDatabaseCommandsStage7Tests` | 12 / 12、43 / 43 通过；覆盖 dry-run、13→14、0→1、锁、恢复点、无操作和非法目标 |
| 2026-07-25 | Persistence、EndToEnd 与解决方案全量回归 | 三层测试 | 171 / 171、211 / 211、782 项通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告；发布制品总帮助与 `db migrate` 帮助实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与差异检查 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `100`；`git diff --check` | 本批最终 5 个 C# 文件、60 个符号已刷新；本批未变更协议/Schema；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 17 全部检查项闭合 | 执行计划更新为 17 / 54；07 执行任务更新为 38 / 74，总清单更新为 38 / 96 |
| 2026-07-25 | Blob 存储维护红灯与定向回归 | `StandaloneStorageCommandsStage7Tests`；Storage 与 Database 联合回归 | 红灯 0 / 11；实现后 11 / 11、联合回归 54 / 54 通过 |
| 2026-07-25 | Persistence、EndToEnd 与全解决方案回归 | `dotnet test` Debug | Persistence 171 / 171、EndToEnd 222 / 222、全解决方案 793 项通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 为 0 条 trimming/AOT 警告；发布制品总帮助与两个 storage 子命令帮助实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与差异检查 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `104`；`git diff --check` | 本批最终 4 个 C# 文件已刷新；本批未变更协议/Schema；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | 进度复核 | 批次 18 全部检查项闭合 | 执行计划更新为 18 / 54；07 执行任务更新为 40 / 74，总清单更新为 40 / 96 |
| 2026-07-25 | 在线控制协议与 Client/Server | `Stage7OnlineControlContractTests`、`RuntimeOnlineControlStage7Tests` | 4 / 4、2 / 2 通过；`runtime.status`、`run.list`、取消原因及跨认证连接凭据更新闭合 |
| 2026-07-25 | `ctl` 红灯与真实 Runtime 回归 | `StandaloneControlCommandsStage7Tests`、在线控制相关 EndToEnd | 8 / 8、合计 10 / 10 通过；覆盖命令树、退出码、认证、实例绑定、状态、Session、Run 筛选、取消和凭据续期 |
| 2026-07-25 | EndToEnd、Protocol 与全解决方案回归 | 三层 `dotnet test` | EndToEnd 232 / 232、Protocol 49 / 49、全解决方案 807 项通过；3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 无 trimming/AOT 警告；发布制品总帮助与 `ctl` 帮助实测通过 |
| 2026-07-25 | CodeMap 与进度复核 | workspace `session` overlay revision `113`；批次 19 全部检查项闭合 | 执行计划更新为 19 / 54；07 执行任务更新为 46 / 74，总清单更新为 46 / 96 |
| 2026-07-25 | 统一输出定向回归 | 版本、配置、Session、数据库、存储、在线控制与统一信封测试 | `89 / 89` 通过；成功业务字段进入 `data`，失败上下文保留在 `data`，根级信封稳定 |
| 2026-07-25 | JSONL、流分离与原子输出 | `Stage7UnifiedOutputTests`、Run 输入输出、Session 导出 | `16 / 16` 通过；覆盖逐行解析、独立终态、stdout/stderr、ANSI 与失败保留旧文件 |
| 2026-07-25 | EndToEnd 与全解决方案回归 | EndToEnd；`dotnet test Madorin.AI.Runtime.slnx --no-restore -m:1` | `237 / 237`；全解决方案共 815 项，812 项通过，3 个真实 Provider 用例按凭据条件跳过 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release build；Windows `win-x64` Release publish | 构建均为 0 警告、0 错误；AOT 无 trimming/AOT 警告；发布制品帮助与版本 JSON 实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `120` | 批次 20 闭合；执行计划更新为 22 / 54；07 执行任务 52 / 74，总清单 54 / 96 |
| 2026-07-25 | 参考宿主与 Client 回归 | `ReferenceHostProcessTests`、`MultiInstanceProcessTests`、`RuntimeClientLifecycleStage7Tests` | 2 / 2、1 / 1、7 / 7 通过；完整参考宿主流程稳定性复跑 10 / 10 通过；三 Runtime/三模式、取消、回调、显式确认、初始连接重试、重连和恢复闭合 |
| 2026-07-25 | 全解决方案回归 | `dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-restore -m:1` | 814 项通过；3 个真实 Provider 用例按凭据条件跳过，0 失败 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release `--no-restore -warnaserror -m:1`；CLI 与 SampleHost Windows `win-x64` Release publish | 两次构建均为 0 警告、0 错误；两个 AOT 制品均无 trimming/AOT 警告；发布版 CLI 帮助和 SampleHost 协议输出实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `134` | 批次 21 闭合；执行计划更新为 28 / 54；07 执行任务 59 / 74，总清单 63 / 96 |
| 2026-07-25 | 完整 REPL 与工作区互斥 | `StandaloneReplStage7Tests`、相关 CLI/Session 回归、EndToEnd 全量 | 7 / 7、54 / 54、241 / 241 通过；补齐压缩、上下文、导出、历史、清屏、状态和详细帮助，锁冲突返回 holder 信息 |
| 2026-07-25 | 全解决方案与发布门禁 | 全量测试、Debug/Release 严格构建、CLI `win-x64` Native AOT | 816 项通过，3 项真实 Provider 按凭据跳过；构建 0 警告、0 错误；AOT 无 trimming/AOT 警告，发布版帮助与版本 JSON 通过 |
| 2026-07-25 | CodeMap 与进度复核 | workspace `session` overlay revision `138` | 批次 22 闭合；执行计划更新为 30 / 54；07 执行任务 61 / 74，总清单 66 / 96 |
| 2026-07-25 | 命令矩阵、Doctor 与 Session 审计 | `Stage7CommandMatrixTests`、`StandaloneDoctorStage7Tests`、`StandaloneSessionCommandsStage7Tests` | 32 / 32 通过；46 条命令路径及帮助稳定，Doctor 顺序/脱敏/修复和四类 Session 维护审计闭合 |
| 2026-07-25 | EndToEnd 与全解决方案回归 | EndToEnd；`dotnet test .\Madorin.AI.Runtime.slnx -c Debug --no-restore -m:1` | 247 / 247；全解决方案 825 项中 822 项通过，3 个真实 Provider 用例按凭据条件跳过，0 失败 |
| 2026-07-25 | 严格构建与 Native AOT | Debug/Release `--no-restore -warnaserror -m:1`；CLI Windows `win-x64` Release publish | 两次构建均为 0 警告、0 错误；AOT 无 trimming/AOT 警告；发布版帮助和版本 JSON 实测通过 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `147` | 批次 23 闭合；执行计划更新为 36 / 54；07 执行任务 65 / 74，总清单 70 / 96 |
| 2026-07-25 | 安装、集成、安全与运维文档 | 三份独立指南；当前 AOT 发布版 Doctor、数据库和存储命令帮助 | 9 个维护命令帮助均返回 0；正式命名、协议 `1.1`、Schema `14`、安全边界及故障处置已复核 |
| 2026-07-25 | 文档进度复核 | 批次 24 全部文档项闭合；13 个阶段行重新求和 | 执行计划更新为 39 / 54；07 执行任务 71 / 74，总清单 77 / 96；总览修正为 750 / 912；本批无 C# 修改 |
| 2026-07-25 | 完整 CLI 命令矩阵与 JSON Schema | `Stage7CommandMatrixTests`；46 条命令路径、14 项默认值、9 组参数约束、7 个退出码及两份机器快照 | 定向 11 / 11、EndToEnd 252 / 252 通过；不存在成功返回的占位命令 |
| 2026-07-25 | 全解决方案与发布门禁 | 全量 MSTest；Debug/Release `--no-restore -warnaserror -m:1`；CLI Windows `win-x64` Release Native AOT | 827 项通过，3 个真实 Provider 用例因缺少凭据跳过，0 失败；两次构建 0 警告、0 错误；AOT 无 trimming/AOT 警告 |
| 2026-07-25 | 发布制品与版本记录 | AOT `madorin --help`、`run --help`、`serve --help`、`version --json`；协议 `1.1`、SQLite Schema `14` | 四项均返回 0；run/serve 默认值可见；PATH、双击和无参数 REPL 发布实测仍保留为残余风险 |
| 2026-07-25 | CodeMap 与进度复核 | workspace `session` overlay revision `156`；199 个变更文件、1876 个符号已刷新；批次 25 全部检查项闭合 | 执行计划更新为 46 / 54；07 执行任务 74 / 74，总清单 82 / 96；总览更新为 755 / 912 |
| 2026-07-25 | 既有证据审计 | CLI/宿主/维护 EndToEnd；记忆持久化；记忆工具 | 96 / 96、16 / 16、4 / 4 通过；关闭记忆命令、宿主个人配置隔离、维护故障矩阵及两个用户闭环标准 |
| 2026-07-25 | 进度复核 | 批次 26 五项主清单证据闭合 | 执行计划保持 46 / 54；07 总清单更新为 87 / 96；总览更新为 760 / 912 |
| 2026-07-25 | 记忆续调与特殊字符串往返 | `ExpertToolLoopTests`；`RuntimeClientHighLevelStage7Tests` | 两项定向测试 2 / 2、Modes 92 / 92、EndToEnd 253 / 253 通过；真实记忆 ToolResult 续调和 SDK 特殊字符串全链路闭合 |
| 2026-07-25 | 全解决方案与严格构建 | 全量 MSTest；Debug/Release `--no-restore -warnaserror -m:1` | 829 项通过，3 个真实 Provider 用例因缺少凭据跳过，0 失败；两次构建均为 0 警告、0 错误 |
| 2026-07-25 | CodeMap 与进度复核 | workspace `session` overlay revision `158`；批次 27 两项主清单证据闭合 | 执行计划保持 46 / 54；07 总清单更新为 89 / 96；总览更新为 762 / 912 |
| 2026-07-25 | Client 公共边界与 Schema 并发 | `ClientAssembly_PublicSurface_DoesNotExposeTransportTypes`；`SchemaValidator_ConcurrentInstances_UseIsolatedSchemaRegistries` | 两项定向测试 2 / 2 通过；Client 导出签名不暴露 Transport 类型，Schema registry 不再跨实例共享非线程安全状态 |
| 2026-07-25 | 全解决方案与严格构建 | 全量 MSTest；Debug/Release `--no-restore -warnaserror -m:1` | 831 项通过，3 个真实 Provider 用例因缺少凭据跳过，0 失败；EndToEnd 254 / 254、Provider 131 项通过；两次构建均为 0 警告、0 错误 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `164`；`git diff --check` | 4 个 C# 文件、121 个符号已刷新；执行计划保持 46 / 54；07 总清单更新为 90 / 96；总览更新为 763 / 912；差异检查通过，仅有既有 LF/CRLF 提示 |
| 2026-07-25 | Client 生命周期联合矩阵 | `RuntimeClientLifecycleStage7Tests`；重连、取消、宿主回调与高层 Client API 相关回归 | 12 / 12、26 / 26 通过；真实进程 attach、`StartIfMissing` 优先附着、初始化协议错误、能力健康错误、重连、取消、回调异常、Dispose 和版本切换均闭合 |
| 2026-07-25 | EndToEnd、全解决方案与严格构建 | EndToEnd；全量 MSTest；Debug/Release `--no-restore -warnaserror -m:1` | EndToEnd 258 / 258；全解决方案 835 项通过，3 个真实 Provider 用例因缺少凭据跳过，0 失败；两次构建均为 0 警告、0 错误 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `165` | 1 个 C# 文件、26 个符号已刷新；执行计划保持 46 / 54；07 总清单更新为 91 / 96；总览更新为 764 / 912 |
| 2026-07-25 | 完整多进程拓扑 | `TwoHostsWithTwoRuntimes_IsolatedAndCrossConnectionsFail` | 2 个 SampleHost、4 个 Runtime 与第五工作区独立 CLI 同时运行；CLI 记忆落盘后四个 Runtime 均可重连，回收一个 Runtime 后其余三个不受影响 |
| 2026-07-25 | 同标识多实例隔离 | `BoundClients_SameRunIdAndGsn_KeepAllOperationsInstanceScoped` | 两套真实 Named Pipe 双向认证连接固定复用相同 `runId`、GSN 和 `callId`；事件、查询、确认、取消、回调和 Dispose 均限定于绑定实例；批次定向 2 / 2 通过 |
| 2026-07-25 | EndToEnd、全解决方案与严格构建 | EndToEnd；全量 MSTest；Debug/Release `--no-restore -warnaserror -m:1` | EndToEnd 259 / 259；全解决方案 836 项通过，3 个真实 Provider 用例因缺少凭据跳过，0 失败；两次构建均为 0 警告、0 错误 |
| 2026-07-25 | 协议、Schema、CodeMap 与进度复核 | 协议 `1.1`、Schema `14`、workspace `session` overlay revision `167` | 208 个文件、1916 个符号已刷新；执行计划保持 46 / 54；07 总清单更新为 93 / 96；总览更新为 766 / 912 |
| 2026-07-25 | CLI 进程与 Windows 发布实测 | 批次 31 定向测试；PATH 新 `cmd.exe`；无参数 REPL；Explorer Shell 默认 `open` 动词 | 定向 2 / 2、联合矩阵 31 / 31 通过；协议 `1.1`；REPL 正常退出；Shell 启动进程存活超过 1 秒且已回收 |
| 2026-07-25 | 最终测试与严格构建 | EndToEnd；全解决方案 MSTest；Debug/Release `--no-restore -warnaserror -m:1`；Windows `win-x64` Release Native AOT | EndToEnd 261 / 261；全解决方案 838 项通过、3 项真实 Provider 凭据测试跳过、0 失败；两次构建 0 警告、0 错误；AOT 0 trimming/AOT 警告 |
| 2026-07-25 | 最终 CodeMap、差异与进度复核 | workspace `session` overlay revision `170`；`git diff --check`；文本完整性与残留进程检查 | 211 个文件、1916 个符号已刷新；差异与文本检查通过；无测试 Runtime/CLI 残留；执行计划 54 / 54、07 总清单 96 / 96、总览 769 / 912 |

## 12. 修改文件

- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次1-ClientSDK生命周期.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次1-返工1-Client公共类型.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次1-返工2-初始化连接异常分类.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-Client高层API与宿主回调.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工1-Qwen提示识别.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工2-Qwen工具预算.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工3-Qwen未遵守范围.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次4-LocalRuntime三模式.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次5-run输入输出.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次6-单行REPL与两阶段中断.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次7-配置Agent记忆与版本审计.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次8-Session列表与详情.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次9-Session导出.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次10-Session归档.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次11-Session删除.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次12-Session压缩.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次13-数据库检查与修复.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次14-数据库在线备份.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次15-数据库索引重建.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次16-数据库压缩.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次17-数据库迁移.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次18-Blob存储检查与清理.md`
- `Docs/未来策划/独立CLI/命令规范/04-CLI命令规范.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-返工-Client初始化就绪竞态.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-返工-事件通道就绪竞态.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/OwnedProcessClosePolicy.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/Properties/AssemblyInfo.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClientException.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClientOptions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbackDispatcher.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbacks.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeInstanceBinding.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeStartPolicy.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/MultiInstanceHost.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/MultiInstanceProcessTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientLifecycleStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientHighLevelStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Messages/MessageTypes.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/ProtocolVersions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListParameters.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListItem.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Serialization/RuntimeJsonContext.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Entities/SessionStatus.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionBlobReferences.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionListQuery.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationDeleteRecoveryPoint.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationCompactCacheV1.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationJsonContext.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStore.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationStoreFailurePoint.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationIntegrityInspector.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionCompactionOptions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionCompactionResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionDeletionResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/DatabaseMaintenanceModels.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/DatabaseMaintenanceService.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/StorageMaintenanceModels.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/StorageMaintenanceService.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Modes.Expert/Madorin.AI.Runtime.Modes.Expert.csproj`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/AtomicOutputFile.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputFormat.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputWriter.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DatabaseCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/StorageCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionExportWriter.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/IReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ConsoleReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/ConversationStoreTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SqliteSessionRepositoryTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunModesStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunInputOutputStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneReplStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7CommandMatrixTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionExportStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneDatabaseCommandsStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneStorageCommandsStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次19-在线控制.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/RuntimeStatusParameters.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/RuntimeStatusResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/RunListParameters.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/RunListItem.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/RunListResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Transport/JsonRpcMessages.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ControlCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ControlCommandOutput.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ControlRuntimeConnection.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/Stage7OnlineControlContractTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeOnlineControlStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneControlCommandsStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次20-统一输出与退出码.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次21-参考宿主.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/CliOutput.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DiagnosticCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/CliVersionTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7UnifiedOutputTests.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/Program.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/ReferenceHost.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/ReferenceHostConfiguration.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/ReferenceHostCallbacks.cs`
- `Src/Madorin.Ai.Runtime/samples/Madorin.AI.Runtime.SampleHost/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Modes.Expert/ExpertModeOrchestrator.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/ReferenceHostProcessTests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次22-完整REPL与工作区互斥.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/CliTerminal.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/WorkspaceWriteLock.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntimeStatus.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionContextStatus.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次23-命令矩阵Doctor与Session审计.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DoctorCommand.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/SessionMaintenanceAuditLog.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/CliCommandSmokeTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneDoctorStage7Tests.cs`
- `Docs/未来策划/独立CLI/使用指南/06-安装与CLI使用.md`
- `Docs/未来策划/独立CLI/使用指南/07-宿主集成指南.md`
- `Docs/未来策划/独立CLI/使用指南/08-安全与数据运维.md`
- `Docs/未来策划/独立CLI/README.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次24-安装集成安全与运维文档.md`
- `Docs/未来策划/独立CLI/命令规范/09-CLI命令矩阵.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次25-完整命令矩阵与JSONSchema快照.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次26-既有测试证据审计.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次27-记忆续调与特殊字符串往返.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次28-Client公共边界与Schema并发.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次29-Client生命周期联合矩阵.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次30-多进程与同标识隔离.md`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientMultiInstanceIsolationStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Madorin.AI.Runtime.EndToEnd.Tests.csproj`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7CommandMatrixTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Snapshots/stage7-cli-command-matrix.v1.json`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Snapshots/stage7-cli-json-envelope.v1.json`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Modes.Tests/Madorin.AI.Runtime.Modes.Tests.csproj`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Modes.Tests/ExpertToolLoopTests.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Services/Tools/JsonSchemaToolValidator.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Provider.Tests/ToolCatalogStoreTests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次31-CLI进程与Windows发布实测.md`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliProcessStage7Tests.cs`
