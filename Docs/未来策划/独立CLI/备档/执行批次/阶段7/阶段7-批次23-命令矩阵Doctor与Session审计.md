# 阶段 7 批次 23：命令矩阵、Doctor 与 Session 审计 : 100%

## 目标与冻结边界

- [√] 冻结当前 46 条 CLI 命令路径并逐条验证 `--help`，确认 V1 使用 `config` 而非旧 `providers` 命令组。
- [√] 将 CLI 命令规范中的 53 处用户命令统一为正式 `madorin`，不保留 `ai-runtime` 别名。
- [√] 完成 Doctor 九项固定顺序检查、Provider 探测、凭据脱敏及可恢复数据库损坏修复。
- [√] 为 Session archive/unarchive/delete/compact 写入独立数据区的 `prepared/completed` 维护审计；compact dry-run 保持零审计写入。
- [√] 删除成功返回的 `NotImplemented` 兼容路径和未注册的旧 Provider 命令占位。

## 实现与审查

- [√] `doctor` 固定按 runtime、personal-config、memory、workspace、data、provider、transport、logs、blob 顺序输出。
- [√] `doctor --fix` 只处理检查结果标记为可修复的损坏；JSON 实际修复要求 `--yes`，交互拒绝返回 130。
- [√] Doctor 修复复用 `DatabaseMaintenanceService`，在工作区排他锁内创建恢复点并再次检查，不自行修改 SQLite 或消息文件。
- [√] Provider 探测异常和配置错误统一脱敏，API Key 不进入人类或 JSON 输出。
- [√] CLI Session、数据库、存储与在线控制继续通过 LocalRuntime、维护服务或 Client API；没有直接构造 SQLite/ConversationStore。
- [√] Doctor 文件保持单命令垂直切片：检查、确认和输出辅助均为私有且无跨命令复用需求，本批不做仅搬移代码的结构拆分。
- [√] 修正旧 Doctor smoke 测试对当前用户配置的隐式依赖，收窄为确定性的 `doctor --help` 命令注册冒烟。

## 测试与验证

- [√] `StandaloneDoctorStage7Tests`、`StandaloneSessionCommandsStage7Tests`、`Stage7CommandMatrixTests`：32 / 32 通过。
- [√] EndToEnd 全量：247 / 247 通过。
- [√] 解决方案全量：825 项中 822 项通过，3 项真实 Provider 冒烟按缺少凭据明确跳过，0 失败。
- [√] Debug/Release `--no-restore -warnaserror -m:1`：均为 0 警告、0 错误。
- [√] Windows `win-x64` Release Native AOT 发布成功，无 trimming/AOT 警告。
- [√] 发布版 `madorin --help` 与 `version --json` 实测通过；产品为 `Madorin.AI.Runtime`，协议为 `1.1`。
- [√] SQLite Schema 保持 `14`；CodeMap workspace `session` 刷新至 overlay revision `147`。
- [√] `git diff --check` 通过，仅存在工作区既有 LF/CRLF 提示。

## 进度与剩余边界

- [√] 07 执行任务由 61 / 74 更新为 65 / 74，总清单由 66 / 96 更新为 70 / 96。
- [√] 阶段 7 执行计划由 30 / 54 更新为 36 / 54。
- [×] 当前命令矩阵只冻结命令路径、现有参数在帮助中的可见性和旧品牌/占位清理；每条命令的完整参数集合、默认值、示例、互斥和 JSON Schema 快照仍待后续批次。
- [×] 安装、PATH、双击、宿主集成、安全、数据库和 Blob 运维文档仍未闭合。

## 修改文件

- `Docs/未来策划/独立CLI/命令规范/04-CLI命令规范.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次23-命令矩阵Doctor与Session审计.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/CliOutput.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DiagnosticCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DoctorCommand.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/ConversationJsonContext.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Files/SessionMaintenanceAuditLog.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/CliCommandSmokeTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7CommandMatrixTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneDoctorStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
