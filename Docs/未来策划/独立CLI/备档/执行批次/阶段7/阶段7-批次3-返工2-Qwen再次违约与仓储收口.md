# 阶段 7 批次 3 返工 2：Qwen 再次违约与仓储收口

## 失败记录

- 会话：`6f8a2851-b353-4bd1-85b2-a8100ae28d50`
- 会话再次违反明确指令，启动 Explore 子代理并读取禁止读取的阶段执行计划。
- 会话在第 46 次工具调用时超过 `--max-tool-calls 45`。
- 已完成且经主代理确认仅位于白名单内的修改：`session.list` 常量、三个 Contracts DTO、`RuntimeJsonContext` 元数据、`DbSchemaVersion = 14`。
- `SessionListQuery` 写入调用未完成，文件不存在；仓储、Schema 和测试均未实施。
- 处理：不使用 `--resume`；新会话只处理 4 个生产文件，不再要求其读取任何文档。

## 本次返工目标

1. 新建 `SessionListQuery`，字段与 `SessionListParameters` 一致。
2. `ISessionRepository` 保留旧 `ListSessionsAsync`，新增查询重载与 `TrySetInitialSessionTitleAsync`。
3. `SqliteSessionRepository`：旧重载委托新重载；参数化实现 mode/status/since/search/cursor；select 并返回 title；新增仅在 title 为空时写入的标题 API。
4. `SqliteSchema`：CurrentVersion 14、RequiredColumns 添加 `sessions.title`、迁移 switch 添加 14、独立事务迁移新增可空 title 并记录 schema_versions 14。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionListQuery.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`

禁止修改任何其他文件。不要读取任何文档，不要使用任何子代理，不要运行测试，不要修改已完成的 Contracts 文件。只读取这 4 个允许文件的相关局部，然后直接局部编辑。
