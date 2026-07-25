# 阶段 7 批次 3 返工 8：Qwen 生产实现工具预算

## 失败会话

- Qwen session：`9f0dc0ec-eb04-4b7b-bb27-d72104692cb3`
- 结果：第 31 次工具调用时超过 `--max-tool-calls 30`，未编辑文件。
- 原因：重复整文件读取和符号检索，没有使用任务文档已给出的精确接入点。

## 禁止继续探索

新会话不得再次整文件读取、glob 或搜索 Contracts/Entities。以下信息已经确认，直接局部编辑：

- Server Session 分派位于 `RuntimeServer.cs` 约 898 行，`MessageTypes.SessionGet` 分支之前。
- Server Session handler 位于约 1238 行的 `CreateSessionGetResponseAsync` 附近。
- 新 Session 创建位于 `StartRunResponseAsync` 约 2129 行，`CreateSessionAsync` 后、`TrySaveSessionSelectionAsync` 前。
- `runRequest.InitialInput` 类型为 `ContentBlock[]`；文本块为 `TextContentBlock(string Text)`。
- Client Session API 位于 `RuntimeClient.cs` 约 985 行，统一帮助方法为 `SendControlRequestAsync`。
- `SessionListQuery` 构造参数：`Mode, Status, Since, Search, Limit, Cursor`。
- `SessionDescriptor` 属性：`SessionId, Mode, Status, UpdatedAt, Title`。
- Contracts 的 `SessionListParameters`、`SessionListItem`、`SessionListResult` 和 `RuntimeJsonContext` 元数据已经存在。

## 必须直接完成的编辑

1. 在 `MessageTypes.SessionGet` 前增加 `SessionList` 分支，调用新的 `CreateSessionListResponseAsync`。
2. 新 handler 使用 source-generated JSON，校验枚举、1..200 limit、空白 cursor；查询 `limit + 1`，映射项目，按要求生成 cursor；捕获 `JsonException`、`ArgumentException`、`FormatException` 返回 `-32602`。注意 `FormatException` 派生自 `Exception` 而不是 `ArgumentException`，catch 不得不可达。
3. `IRuntimeClient` 和 `RuntimeClient` 增加 `ListSessionsAsync(SessionListParameters, CancellationToken)`，复用 `SendControlRequestAsync`。
4. 新 Session 创建后从 `InitialInput` 生成标题并调用 `TrySetInitialSessionTitleAsync`。用小型私有静态帮助方法压平 Unicode 空白，限制 120 UTF-16 code unit，并避免末尾未配对高代理项。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`

不使用子代理，不运行构建或测试；编辑完成后立即停止。
