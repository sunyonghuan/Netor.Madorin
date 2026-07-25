# 阶段 7 批次 6：单行 REPL 与两阶段中断 : 100%

> 状态：已完成

## 目标

- 补齐 `/model`、`/provider`、`/agent`、`/mode`、`/session`、`/new`、`/resume`、`/tools`、`/help`、`/exit` 和 `/quit` 核心命令。
- Selection 修改只作用于后续 Run，不改变已经执行中的 Run；Session 模式在 REPL 内只读。
- Run 执行中第一次 Ctrl+C 只取消当前 Run，等待唯一 `Cancelled` 终态后恢复同一 Session 的提示符。
- 取消收敛期间第二次 Ctrl+C 或空闲 Ctrl+C 返回退出码 130；EOF 正常退出。

## 冻结契约

- REPL 只在等待输入时读取一行；Run 执行期间不读取后续输入、不建立输入队列。
- `/model`、`/provider`、`/agent` 无参数时显示当前值，有参数时验证本地配置并从下一轮开始生效。
- 已建立 Session 后，Selection 更新使用递增版本和乐观并发写入 Runtime；更新失败时保留原选择。
- `/mode` 只显示当前模式，携带参数时拒绝修改。
- `/resume` 无参数列出最近活动 Session；携带 ID 时先验证 Session 存在、模式匹配和本地 Agent/Provider/模型可解析。
- `/tools` 显示 Runtime 当前工具目录，并明确 `builtin.memory.read` 为只读、`builtin.memory.append` 需要审批。
- EOF 和 `/exit`/`/quit` 返回 0；空闲中断和第二次中断返回 130。

## 非目标

- 本批不实现 `/compact`、`/context`、`/export`、`/history`、`/clear` 和完整运行状态统计。
- 本批不勾选“全部 REPL 斜杠命令”或完整 REPL 测试总项。
- 本批不修改数据库维护、在线控制和 SampleHost。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/IReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ConsoleReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneReplStage7Tests.cs`
- 本批次文档与阶段 7 进度文档

## 实施检查

- [√] 核心斜杠命令、Selection 版本和恢复行为。
- [√] Runtime Session/工具目录查询与 Selection 乐观并发更新。
- [√] 首次 Ctrl+C 取消 Run 并恢复同一 Session。
- [√] 第二次/空闲 Ctrl+C、EOF 和退出码。
- [√] Run 唯一终态与取消后继续测试。
- [√] CodeMap overlay、严格构建与回归验证。

## 验证记录

- CodeMap baseline `369c49099fd03afd05db23e540096b3bf18f3924`，workspace `session`，最终 overlay revision `36`。
- `dotnet build .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj -c Debug --no-restore -warnaserror -m:1`：0 警告、0 错误。
- `StandaloneReplStage7Tests`：5 / 5 通过，覆盖核心命令、Selection 持久化、Session 新建/恢复、EOF 和两阶段中断。
- CLI、三模式、LocalRuntime 与 REPL 定向回归：29 / 29 通过。
- `Madorin.AI.Runtime.EndToEnd.Tests` Debug 全量：137 / 137 通过。
- `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1`：0 警告、0 错误。
- `git diff --check`：通过；仅有既有 LF/CRLF 提示。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次6-单行REPL与两阶段中断.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/IReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ConsoleReplInterruptSource.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneReplStage7Tests.cs`
