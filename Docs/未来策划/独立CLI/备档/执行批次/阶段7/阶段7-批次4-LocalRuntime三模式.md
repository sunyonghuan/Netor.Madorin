# 阶段 7 批次 4：LocalRuntime 三模式 : 100%

> 状态：已完成

## 目标

- 移除 LocalRuntime 仅允许 Expert 的历史限制，复用 `RuntimeExecutionCore` 完成 Expert、Meeting、Work 三模式。
- 为 Meeting 参与者和 Work 子智能体提供按 Provider ID 的本地 Provider 解析，不引入 IPC。
- CLI `run --mode` 为三种模式构造匹配的 `ModeOptions`，未知模式仍在 Runtime 启动前返回参数错误。
- 通过真实 LocalRuntime、SQLite、JSONL 和确定性 Provider 验证三模式均形成唯一 Completed 终态。

## 实施约束

- 独立 CLI 继续直接使用 LocalRuntime，不改为启动 RuntimeServer 子进程。
- Expert 使用当前选中 Agent；Meeting 使用全部有效 Agent 作为参与者；Work 使用当前选中 Agent 作为总经理、其余有效 Agent 作为执行者。
- Work 至少需要两个有效 Agent；不满足时返回参数错误，不创建数据目录。
- Provider 解析只按配置中的 Provider ID 选择 Adapter，不把路径、数据库或内部 DI 暴露给 CLI。
- 不处理输入/输出文件、JSON/JSONL、`--no-stream` 或 Ctrl+C；这些属于后续子批次。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunModesStage7Tests.cs`
- 本批次文档

## 实施检查

- [√] LocalRuntime 通用模式/ModeOptions 校验。
- [√] LocalRuntime 按 Provider ID 分派 Meeting/Work 调用。
- [√] CLI 三模式 Selection 构造与 Work Agent 数量校验。
- [√] 未知模式回归测试。
- [√] Expert、Meeting、Work 真实 LocalRuntime 端到端测试。
- [√] CodeMap overlay、严格构建与定向回归。

## 验证记录

- CodeMap workspace `session` 已刷新至 overlay revision `21`，102 个变更文件、1526 个符号已更新。
- `StandaloneRunModesStage7Tests`：2 / 2 通过；三个模式均通过真实 `CliApplication.RunForTests`、LocalRuntime、SQLite 和消息 JSONL 完成，数据库分别只有一个 `Completed` Run。
- `StandaloneCliTests`：9 / 9 通过；未知模式保持在数据目录创建前返回退出码 2。
- `StandaloneLocalRuntimeTests`：7 / 7 通过。
- 三类测试在同一 VSTest 进程中合跑：18 / 18 通过。
- `dotnet build tests/Madorin.AI.Runtime.EndToEnd.Tests/Madorin.AI.Runtime.EndToEnd.Tests.csproj --no-restore -c Debug -warnaserror -m:1`：0 警告、0 错误。
- Work 单 Agent 用例返回退出码 2，Provider 未收到请求且数据目录未创建。

## 进度说明

- 本批次 6 / 6 完成。
- 阶段执行计划仍为 8 / 54（14.8%），07 主清单仍为 17 / 96（17.7%）。完整 `run` 条目还包含 input-file、输出格式、`--no-stream`、REPL 与 Ctrl+C，不在本批次提前勾选。

## 修改文件

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunModesStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次4-LocalRuntime三模式.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
