# 阶段 7 批次 7：配置、Agent、记忆与版本审计 : 100%

> 状态：已完成

## 目标

- 复核 `version` 的人类与 JSON 输出。
- 复核 `config init/edit/show/validate` 对协议、Provider、BaseURL、Key、模型与默认项的管理。
- 复核 `agent list/create/edit/delete` 的交互式 CRUD 和默认 Agent 替换约束。
- 复核 `memory init/show/add/edit/clear`、`show effective`、REPL `/memory` 与 `/remember`。
- 证明 Key 不通过命令行明文参数输入，展示、诊断与错误输出不泄漏凭据。

## 冻结契约

- V1 不提供独立 `providers` 命令组，Provider 配置统一由 `config` 管理。
- 配置和 Agent 的交互输入不得在 `--json` 模式下伪装成机器输出。
- API Key 只经无回显终端输入写入；`config show` 的人类与 JSON 输出均必须脱敏。
- 记忆只操作全局和项目两个 `memory.md`，并继续使用跨进程锁与原子替换。
- 本批不勾选 `doctor`、数据库维护、Session 管理或完整命令树输出总项。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ConfigCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/AgentCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/MemoryCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/DiagnosticCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/` 下本批相关测试
- 本批次文档与阶段 7 进度文档

## 实施检查

- [√] 审计冻结命令矩阵，移除独立 `providers` 成功占位入口。
- [√] 验证 `version` 人类与 JSON 输出。
- [√] 验证配置、Agent 和记忆完整行为。
- [√] 验证 Key 输入、持久化、展示和错误脱敏。
- [√] CodeMap overlay、严格构建与定向/全量回归。
- [√] 仅按测试证据同步阶段 7 进度。

## 验证记录

- `Stage7CommandMatrixTests`：5 / 5 通过，覆盖根命令、`config` 子命令、Key 参数禁用、版本输出和配置脱敏。
- `Stage7CommandMatrixTests`、`StandaloneCliTests`、`StandaloneConfigTests`、`StandaloneConfigurationSafetyTests`：32 / 32 通过。
- `ReadEventsAsync_WithoutExplicitAcknowledgement_ReplaysAfterReconnect`：修正旧 Pipe 缓冲竞态假设后连续 3 / 3 通过。
- `Madorin.AI.Runtime.EndToEnd.Tests` Debug 全量：142 / 142 通过。
- `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1`：0 警告、0 错误。
- CodeMap workspace `session` 已刷新至 overlay revision `40`。
- `git diff --check`：通过，仅有既有 LF/CRLF 提示。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次7-配置Agent记忆与版本审计.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7CommandMatrixTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientHighLevelStage7Tests.cs`
