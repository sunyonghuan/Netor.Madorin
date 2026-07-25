# 阶段 7 批次 8：Session 列表与详情 : 100%

> 状态：已完成

## 目标

- 完成 `session list` 的游标分页、模式、状态、日期和关键字过滤。
- 完成 `session show <sessionId>` 的元数据、Selection、最新 Run 与可选消息摘要输出。
- 人类与 JSON 输出均通过 LocalRuntime 只读服务入口获取数据，CLI 不直接打开数据库或消息文件。
- 不提前实现或勾选 export、delete、archive/unarchive 和 compact。

## 冻结契约

- `--status` 接受 `active`、`archived`、`all`，默认 `active`。
- `--mode` 接受 `expert`、`meeting`、`work`；`--since` 使用带时区 ISO 8601。
- `--limit` 默认 20，范围 1 到 200；`--cursor` 原样传给服务层。
- `session show --messages` 输出按消息序号升序的只读摘要，不读取或展示完整 Prompt、推理或工具结果。
- Session 不存在或参数无效返回参数/配置错误；工作区占用或数据不可用返回工作区/数据错误。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/` 下本批相关测试
- 本批次文档与阶段 7 进度文档

## 实施检查

- [√] 完成 `session list` 参数解析、服务调用与人类/JSON 输出。
- [√] 完成 `session show` 元数据与 `--messages` 摘要输出。
- [√] 增加有效筛选、分页、空结果、详情、不存在与参数失败测试。
- [√] 保证 CLI 不直接引用 SQLite 或消息文件实现。
- [√] CodeMap overlay、严格构建与定向/全量回归。
- [√] 仅按测试证据同步阶段 7 进度。

## 验证记录

- 实现前红灯：本批新增 7 个用例为 0 / 7 通过。
- 本批定向测试：`StandaloneSessionCommandsStage7Tests` 为 7 / 7 通过。
- Session 与命令矩阵回归：12 / 12 通过。
- 事件重连复核：`ReadEventsAsync_WithoutExplicitAcknowledgement_ReplaysAfterReconnect` 连续 20 / 20 通过。
- EndToEnd 全量：149 / 149 通过。
- 解决方案全量：716 个通过；3 个真实 Provider 用例因缺少外部凭据按预期跳过。
- 严格 Debug 构建：0 警告、0 错误。
- `git diff --check`：通过；仅有既有 LF/CRLF 提示。
- CodeMap：workspace `session`，overlay revision `48`，119 个文件重建索引、1577 个符号更新。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次8-Session列表与详情.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/CliApplication.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Cli/Commands/SessionCommands.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/LocalRuntime.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/StandaloneSessionCommandsStage7Tests.cs`
