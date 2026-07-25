# 阶段 7 批次 22：完整 REPL 与工作区互斥 : 100%

## 目标与冻结边界

- [√] 补齐 `/compact`、`/context`、`/export`、`/history`、`/clear`、`/status` 和 `/help [command]`，保留 V1 单行 REPL 边界。
- [√] 所有 Session 数据通过 `LocalRuntime` 服务读取或修改，导出复用现有原子输出与脱敏规则。
- [√] `/clear` 只调用交互控制台清屏，不向重定向输出写 ANSI 控制序列。
- [√] 同工作区第二个写实例返回占用错误及 holder 元数据，不自动附着；不同工作区互不阻塞。
- [√] 首个 CLI 退出后释放其工作区锁，后续实例可以正常进入。

## 测试与验证

- [√] `StandaloneReplStage7Tests` 7 / 7 通过，覆盖完整命令、Selection、恢复、EOF、两阶段 Ctrl+C、原子导出与锁释放。
- [√] 相关 CLI、Session、导出、LocalRuntime 和统一输出回归 54 / 54 通过。
- [√] EndToEnd 全量 241 / 241 通过。
- [√] 全解决方案 816 项通过；3 项真实 Provider 测试按凭据条件跳过，0 失败。
- [√] Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- [√] CLI Windows `win-x64` Release Native AOT 发布成功，无 trimming/AOT 警告；发布版帮助和版本 JSON 实测通过。
- [√] CodeMap workspace `session` 刷新至 overlay revision `138`。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次22-完整REPL与工作区互斥.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Config/CliTerminal.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntimeStatus.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/SessionContextStatus.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/WorkspaceWriteLock.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneReplStage7Tests.cs`
