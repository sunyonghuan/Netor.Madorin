# 阶段 7 批次 31：CLI 进程与 Windows 发布实测 : 100%

> 状态：已完成
>
> 日期：2026-07-25
>
> 本批只补阶段 7 剩余 CLI 进程矩阵和 Windows 发布制品实测，不修改 Runtime、Client、协议或持久化业务实现。

## 1. 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliProcessStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次31-CLI进程与Windows发布实测.md`
- 验证通过后更新：
  - `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
  - `Docs/未来策划/独立CLI/执行步骤/README.md`
  - `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`

## 2. 既有联合证据

- [√] `StandaloneCliTests.RunForTests_FirstUseWizardAndRepl_PersistsPromptAndContinuesSession` 覆盖无配置首次向导、无参数 REPL、Prompt 首尾空白、两轮同 Session 和凭据不回显。
- [√] `StandaloneConfigurationSafetyTests` 的三项 `RunEditAsync` 用例和 `StandaloneCliTests.RunForTests_AgentCommands_CompleteInteractiveCrudFlow` 重新运行通过。
- [√] `StandaloneReplStage7Tests` 的首次/再次 Ctrl+C、空闲 Ctrl+C、取消后同 Session 继续输入及进程内锁释放用例重新运行通过。

> 首次配置、配置修改、Agent CRUD 与 REPL Ctrl+C 联合矩阵 `31 / 31` 通过。

## 3. 新增真实进程门禁

- [√] 两个真实 `madorin serve` CLI 进程绑定不同工作区并行存活。
- [√] 相同工作区的竞争 CLI 返回工作区错误和持锁实例信息。
- [√] 终止一个 CLI 进程不影响另一工作区，终止后原工作区可由新 CLI 获取写锁。
- [√] 新 `cmd.exe` 进程只通过临时 PATH 以名称执行 `madorin version --json`，输出可解析且协议为 `1.1`。

> 新增 `ServeProcesses_WorkspaceIsolationTerminationAndLockRelease_RemainScoped` 与 `PathDiscovery_NewCommandShell_ExecutesMadorinByName`，定向 `2 / 2` 通过。

## 4. Windows 发布制品实测

- [√] Windows `win-x64` Release Native AOT 发布保持 0 trimming/AOT 警告。
- [√] 新终端 PATH 命令发现实测通过。
- [√] 无参数 `madorin.exe` 进入轻量单行 REPL。
- [√] 通过 Windows Shell 双击 `madorin.exe` 进入同一 REPL，并在退出后确认无残留进程。

> 发布版由新 `cmd.exe` 进程按名称启动并返回协议 `1.1`；无参数 REPL 返回 0；Explorer `Shell.Application` 默认 `open` 动词启动后在 REPL 存活超过 1 秒，随后已回收。

## 5. 回归与进度

- [√] 批次 31 定向测试通过。
- [√] EndToEnd 全量测试通过。
- [√] 全解决方案测试通过，真实 Provider 用例仅在缺少凭据时跳过。
- [√] Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- [√] 刷新 CodeMap `session` overlay，并通过 `git diff --check` 与文本完整性检查。
- [√] 仅在全部证据闭合后更新 07 主清单、执行步骤总览和阶段 7 执行计划。

验证记录：

- EndToEnd `261 / 261` 通过；全解决方案 `838` 项通过，3 项真实 Provider 用例因缺少凭据跳过，0 失败。
- Debug/Release 严格构建均为 0 警告、0 错误；Windows `win-x64` Release Native AOT 为 0 trimming/AOT 警告。
- 协议 `1.1`、SQLite Schema `14`；CodeMap workspace `session` overlay revision `170`，211 个文件、1916 个符号完成刷新。
- `git diff --check` 通过，仅有既有 LF/CRLF 提示；两个新增文件无尾随空格、替换字符或缺失结尾换行。
- 阶段 7 测试结束后没有残留 Runtime/CLI 测试进程；PID `47940` 的 `D:\Contrna\Madorin.exe` 是任务外用户进程，未终止或修改。

## 6. 修改文件

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneCliProcessStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次31-CLI进程与Windows发布实测.md`
