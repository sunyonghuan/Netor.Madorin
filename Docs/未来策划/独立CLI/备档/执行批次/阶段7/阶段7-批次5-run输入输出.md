# 阶段 7 批次 5：run 输入输出 : 100%

> 状态：已完成

## 目标

- 完成单次 `run` 的 `--input` / `--input-file` 输入解析，保证中文、引号、换行和首尾空白无损传递。
- 完成 `text`、`json`、`jsonl` 三种输出格式及 `--no-stream` 行为。
- 将机器输出限定为可独立解析的 stdout 数据，诊断和人类错误写入 stderr。
- 使用同目录临时文件和原子发布实现 `--output-file`，失败时保留已有有效文件。

## 冻结契约

- `--input` 与 `--input-file` 互斥；两者同时出现时返回退出码 2，且不创建 Runtime 数据目录。
- `--input-file` 使用 UTF-8（允许 BOM）读取完整正文，不 Trim、不改写换行；文件不存在、不可读或编码无效时返回退出码 2。
- 单次模式未指定 `--output-format` 时默认 `text`；`--json` 等价于 `--output-format json`，与显式非 JSON 格式同时使用时返回退出码 2。
- `text` 默认逐 Delta 输出，`text --no-stream` 在终态前不输出正文；两者成功正文一致。
- `json` 只输出一个成功或失败信封；成功 `data` 至少包含 `sessionId`、`runId` 和完整 `text`。
- `jsonl` 默认每个 Runtime 事件输出一行完整对象，终态行包含 `success` 并可独立判断成功、失败或取消；`--no-stream` 只输出最终信封。
- JSON/JSONL 的 stdout 不包含提示符、进度、自然语言错误或 ANSI；人类模式错误写 stderr。
- 指定 `--output-file` 时，业务输出只写目标文件，stdout 保持为空；文件在 Run 成功并完整格式化后才原子发布。
- 输出文件的目录必须已存在；任何读取、格式化、临时写入或发布失败都返回非零退出码，并保留原目标文件内容。

## 非目标

- 不实现 REPL 斜杠命令、Selection 切换、Session 恢复或两阶段 Ctrl+C。
- 不统一其他命令组的全部输出；本批只为 `run` 建立可复用行为和测试证据。
- 不勾选 REPL/Ctrl+C 条目，也不提前勾选阶段 4.9 的跨命令输出总项。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/AtomicOutputFile.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputFormat.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputWriter.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunInputOutputStage7Tests.cs`
- 本批次文档与阶段 7 执行计划

## 实施检查

- [√] 输入互斥、格式互斥、默认值和错误前置校验。
- [√] UTF-8 input-file 无损读取与错误映射。
- [√] text/json/jsonl 和 stream/no-stream 输出。
- [√] stdout/stderr 分离与机器输出可解析性。
- [√] output-file 同目录临时写入、原子发布和失败保留。
- [√] CodeMap overlay、严格构建与定向回归。

## 验证记录

- `StandaloneRunInputOutputStage7Tests`：6 / 6 通过；覆盖输入/格式互斥、严格 UTF-8 BOM、中文路径、引号、混合换行、首尾空白、三种格式、流/非流、stdout/stderr、原子替换、锁冲突和旧文件保留。
- CLI、三模式与 LocalRuntime 同进程定向回归：24 / 24 通过。
- `Madorin.AI.Runtime.EndToEnd.Tests`：首次全量运行 131 / 132，通过单独重跑确认既有 Meeting 重启初始化竞态为瞬时失败；随后全量重跑 132 / 132 通过。
- `dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1`：0 警告、0 错误。
- CodeMap workspace `session` 已刷新至 overlay revision `30`，109 个变更文件、1538 个符号已更新。
- `git diff --check` 通过，仅有仓库既有 LF/CRLF 提示。

## 进度说明

- 本批次 6 / 6 完成。
- 阶段执行计划更新为 9 / 54（16.7%），07 主清单更新为 18 / 96（18.8%）；REPL/Ctrl+C 与跨命令统一输出仍保持未完成。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次5-run输入输出.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/AtomicOutputFile.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputFormat.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/RunOutputWriter.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneRunInputOutputStage7Tests.cs`
