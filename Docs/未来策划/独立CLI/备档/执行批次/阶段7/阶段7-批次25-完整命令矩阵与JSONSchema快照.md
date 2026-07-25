# 阶段 7 批次 25：完整命令矩阵与 JSON Schema 快照 : 100%

## 目标与交付

- [√] 冻结当前 46 条命令路径及其精确 usage、帮助 SHA-256、局部参数和 Argument arity。
- [√] 为每条命令提供可解析示例，并固定 14 项默认值帮助契约。
- [√] 固定 9 组互斥或条件必填约束和 7 个公开退出码。
- [√] 新增成功/失败 JSON 信封 Schema，并用 Draft 2020-12 校验器执行快照测试。
- [√] 新增人工可读的 CLI 命令矩阵，记录数据影响、锁要求、主要失败码和变更规则。

## 实现

- [√] `Stage7CommandMatrixTests` 覆盖命令树完整性、精确 usage、帮助哈希、参数集合、Argument arity、示例解析、默认值、非法组合、退出码和 JSON Schema。
- [√] 帮助快照固定使用 `en-US` UI Culture，只把 Usage 行中的测试宿主可执行名规范化为 `madorin`，避免操作系统语言和 `testhost` 名称造成假差异。
- [√] `serve` 与 `run` 帮助补充可观察默认值，不改变既有运行时默认行为。
- [√] EndToEnd 测试项目引入 `JsonSchema.Net 8.0.5`，仅用于机器输出 Schema 验证。
- [√] 命令矩阵与 JSON 信封分别写入版本化机器快照。

## 验证

- [√] 命令矩阵定向测试 11 / 11 通过。
- [√] `Madorin.AI.Runtime.EndToEnd.Tests` 252 / 252 通过。
- [√] 全解决方案 827 项通过，3 个真实 Provider 用例因缺少外部凭据明确跳过，0 失败。
- [√] Debug 与 Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- [√] Windows `win-x64` Release Native AOT 发布无 trimming/AOT 警告。
- [√] AOT 制品的 `madorin --help`、`madorin run --help`、`madorin serve --help` 和 `madorin version --json` 均返回 0；run/serve 帮助包含冻结默认值。
- [√] 协议保持 `1.1`，SQLite Schema 保持 `14`；CodeMap workspace `session` 最终刷新 199 个变更文件、1876 个符号，overlay revision 为 `156`。

## 进度与剩余边界

- [√] 07 文档执行任务由 71 / 74 更新为 74 / 74，测试要求由 3 / 14 更新为 4 / 14，完成标准由 3 / 8 更新为 4 / 8，总清单更新为 82 / 96（85.4%）。
- [√] 阶段 7 执行计划由 39 / 54 更新为 46 / 54（85.2%）。
- [√] 执行步骤总览由 750 / 912 更新为 755 / 912（82.8%）。
- [×] PATH 命令发现、Windows 双击、无参数 REPL 发布制品实测及剩余联合测试尚未完成，不提前勾选最终 74 / 74 门禁。

## 修改文件

- `Docs/未来策划/独立CLI/命令规范/09-CLI命令矩阵.md`
- `Docs/未来策划/独立CLI/README.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次25-完整命令矩阵与JSONSchema快照.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/ServiceCommands.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Madorin.AI.Runtime.EndToEnd.Tests.csproj`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Stage7CommandMatrixTests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Snapshots/stage7-cli-command-matrix.v1.json`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/Snapshots/stage7-cli-json-envelope.v1.json`
