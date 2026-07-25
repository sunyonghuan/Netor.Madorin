# 阶段 7 批次 3：Session 列表与筛选 : 100%

> 状态：已完成

## 工作目录

- 仓库根目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 执行计划目录：`E:\Netor.me\Madorin\Netor.Madorin\Docs\未来策划\独立CLI`
- 待修改项目目录：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`
- 本任务文档：`E:\Netor.me\Madorin\Netor.Madorin\Docs\未来策划\独立CLI\备档\执行批次\阶段7\阶段7-批次3-Session列表与筛选.md`

## 目标

- 增加 `session.list` 公共协议、Server handler 和 Client 高层 API。
- 列表支持 mode、status、since、search、limit 和 keyset cursor。
- 默认 status 为 `Active`、limit 为 20，Server 接受的 limit 范围为 1 到 200。
- cursor 使用已有的 `updatedAt|sessionId` 降序 keyset 语义；只有存在下一页时返回 `nextCursor`。
- Session 标题持久化到 `sessions.title`，新 Session 从首个非空文本输入生成标题；search 对标题执行字面量包含过滤。
- 数据库 Schema 从 13 升到 14，迁移必须保留已有数据并通过结构验证。

## 公共契约

- `MessageTypes.SessionList = "session.list"`。
- 新增 `SessionListParameters`：`RuntimeMode? Mode`、`SessionStatus? Status = Active`、`DateTimeOffset? Since`、`string? Search`、`int Limit = 20`、`string? Cursor`。
- 新增 `SessionListItem`：`SessionId`、`Mode`、`Status`、`UpdatedAt`、`Title`。
- 新增 `SessionListResult`：`SessionListItem[] Sessions`、`string? NextCursor`。
- 所有新增 DTO 和数组类型注册到 `RuntimeJsonContext`，禁止反射序列化。
- `IRuntimeClient.ListSessionsAsync` 和 `RuntimeClient.ListSessionsAsync` 复用统一控制请求路径。

## 持久化与 Server

- 在 Persistence.Abstractions 新增窄的 `SessionListQuery`，不要让 Contracts 依赖 SQLite。
- 保留现有 `ListSessionsAsync(string? cursor, int pageSize, ...)` 行为和 100k 性能测试；新增筛选重载并由旧重载委托，避免破坏阶段 6C 调用方。
- SQL 使用参数化条件，cursor 条件必须整体加括号，排序固定为 `updated_at DESC, session_id DESC`。
- search 不得把 `%`、`_` 当通配符；优先使用 `instr(COALESCE(title, ''), $search) > 0`。
- 新增设置初始标题的仓储 API，只在标题为空时写入；标题取首个非空 `TextContentBlock.Text`，压平换行和连续空白，最长 120 个 UTF-16 字符。
- Schema 14 通过独立迁移新增可空 `sessions.title`，同步 `SqliteSchema.CurrentVersion`、`ProtocolVersions.DbSchemaVersion` 和 RequiredColumns。
- Server 校验非法枚举、limit、空白 cursor；解析/仓储参数错误返回 `-32602`，不泄露内部路径。

## 测试要求

- Protocol：新增 DTO 使用 `RuntimeJsonContext` 完成往返，默认值和 JSON 字段稳定。
- Persistence：覆盖 mode、status、since、标题 search、相同 updatedAt 的 keyset 无重复无遗漏、非法 cursor，以及 Schema 13 到 14 标题列迁移。
- EndToEnd：通过 `RuntimeClient.ListSessionsAsync` 创建至少三个不同模式 Session，验证默认 active、组合筛选、分页 nextCursor、标题和无结果页。
- 保留 `ListSessionsAsync_100kSessions_UsesKeysetIndexWithinBaseline` 并确保仍通过。

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
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/SessionListStage7Tests.cs`

禁止修改其他文件。上述已有文件包含阶段 6C 和阶段 7 前两批未提交修改，只能做局部增量补丁，不得重写、回退或格式化整文件。不要修改本任务文档和阶段执行计划。

## 实施检查

- [√] Session list DTO、MessageTypes 和 AOT JSON 元数据。
- [√] Schema 14 标题迁移和初始标题持久化。
- [√] mode/status/since/search/keyset 仓储查询。
- [√] Server handler 与参数校验。
- [√] Client 高层 API。
- [√] Protocol、Persistence 和 EndToEnd 测试。
- [√] 主代理 CodeMap 刷新、审查、构建与测试。

## 验证记录

| 日期 | 范围 | 结果 |
| --- | --- | --- |
| 2026-07-24 | `SessionListContractStage7Tests` | 5 / 5 通过 |
| 2026-07-24 | `SessionListStage7Tests` 与 `SessionListSchemaStage7Tests` | 11 / 11 通过 |
| 2026-07-24 | EndToEnd `SessionListStage7Tests` | 1 / 1 通过；真实 RuntimeServer、Named Pipe 与 RuntimeClient |
| 2026-07-24 | `ListSessionsAsync_100kSessions_UsesKeysetIndexWithinBaseline` | 1 / 1 通过，测试体耗时 644 ms |
| 2026-07-24 | EndToEnd.Tests 严格 Debug 构建 | 0 警告、0 错误 |
| 2026-07-24 | CodeMap | workspace `session`，overlay revision `15` |

## 修改文件

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
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/SessionListStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`
