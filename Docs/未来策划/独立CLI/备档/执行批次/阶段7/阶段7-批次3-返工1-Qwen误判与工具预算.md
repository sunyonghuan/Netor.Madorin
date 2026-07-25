# 阶段 7 批次 3 返工 1：Qwen 误判与工具预算

## 失败记录

- 会话：`0631dd8b-5339-41e2-8c60-599222d88e13`
- 结果：未修改任何代码文件。
- 问题 1：把任务文档中的 `[×]` 错解为“已完成”；本项目约定 `[×]` 表示未完成、`[√]` 表示已完成。
- 问题 2：提示明确禁止子代理，但会话仍启动 Explore 子代理。
- 问题 3：在 31 次工具调用时超过 `--max-tool-calls 30`，仍停留在探索阶段。
- 处理：不使用 `--resume`；启动全新会话，并将范围缩小为 Contracts 与 Persistence。

## 本次返工目标

完整读取原任务文档 `阶段7-批次3-Session列表与筛选.md`，只实施以下内容：

- `session.list` 三个公共 DTO、`MessageTypes` 和 `RuntimeJsonContext` 元数据。
- `SessionListQuery`、仓储筛选重载、初始标题写入 API。
- Schema 14 的 `sessions.title` 迁移与版本同步。
- Protocol 与 Persistence 测试，包括 Schema 13 到 14 迁移。

`[×]` 全部是待完成项。不要读取阶段执行计划或其他策划文档，不要使用 `agent`、`create_sub_session`、Explore 或任何子代理。只读取原任务文档和允许修改的源码/测试文件，然后直接编辑。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Messages/MessageTypes.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/ProtocolVersions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListParameters.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListItem.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Requests/SessionListResult.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Contracts/Serialization/RuntimeJsonContext.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionListQuery.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`

禁止修改其他文件，禁止修改本返工文档和原任务文档。已有文件只能局部补丁，不得重写或格式化整文件。
