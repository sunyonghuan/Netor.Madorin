# 阶段 7 批次 3 返工 3：Qwen 多行提示截断

## 失败记录

- 会话：`75879824-d209-40c1-a86a-ecfbd4f5d401`
- 结果：进程返回成功，但未修改任何文件。
- 会话只识别到提示第一句“直接完成一个 .NET 局部编码任务”，反复声称任务未指定；后续明确列出的四个文件和实现要求没有被识别。
- 会话仍违反禁止子代理、禁止 Git 和禁止扩大范围约束，启动 Explore 并读取大量无关文件。
- 推断：当前 Qwen CLI/PowerShell 组合可能截断了多行 `--prompt` 参数。
- 处理：不使用 `--resume`；新会话用单行 Prompt，显式要求读取本返工文档后只修改四个白名单文件。

## 唯一任务

读取本文件的“实现要求”，完成仓储与 Schema 补丁。

## 实现要求

- 新建 `SessionListQuery`：`RuntimeMode? Mode = null`、`SessionStatus? Status = Active`、`DateTimeOffset? Since = null`、`string? Search = null`、`int Limit = 20`、`string? Cursor = null`。
- `ISessionRepository` 保留旧列表方法，新增 `ListSessionsAsync(SessionListQuery query, CancellationToken ct = default)` 和 `TrySetInitialSessionTitleAsync(string sessionId, string title, CancellationToken ct = default)`。
- SQLite 仓储旧重载委托新重载；新查询按 mode/status/since/search/cursor 参数化过滤，search 使用 `instr(COALESCE(title, ''), $search) > 0`，cursor 条件整体括号，按 `updated_at DESC, session_id DESC`，返回 title；标题 API 仅在 title 为空时更新。
- `SqliteSchema.CurrentVersion = 14`，RequiredColumns 添加 `sessions.title`，迁移 switch 添加 14，`ApplyVersionFourteenAsync` 在事务中新增可空 title、写入 schema_versions 14 并提交。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/SessionListQuery.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Abstractions/ISessionRepository.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSchema.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Persistence.Sqlite/SqliteSessionRepository.cs`

禁止修改其他文件，禁止子代理，禁止 Git，禁止测试，禁止重写或格式化整文件。
