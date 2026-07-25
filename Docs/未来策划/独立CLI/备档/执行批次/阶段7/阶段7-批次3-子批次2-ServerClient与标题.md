# 阶段 7 批次 3 子批次 2：Server、Client 与标题

## 工作目录

- 仓库根目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 执行计划目录：`E:\Netor.me\Madorin\Netor.Madorin\Docs\未来策划\独立CLI`
- 项目目录：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`
- 原任务文档：`Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`

## 目标

- 完成 `session.list` Server handler 和 Client 高层 API。
- 新 Session 从首个非空文本输入生成并持久化初始标题。

## Server 要求

- 在现有 Session handler 分派段增加 `MessageTypes.SessionList`。
- 仅使用 `RuntimeJsonContext` 反序列化 `SessionListParameters` 和序列化 `SessionListResult`。
- 验证 `Mode`、`Status` 是已定义枚举值，`Limit` 为 1 到 200，非 null `Cursor` 不能是空白。
- JSON 解析异常、非法 cursor 和仓储参数异常统一返回 JSON-RPC `-32602`，错误信息不得泄露内部路径。
- 仓储查询使用 `limit + 1`；只返回前 `limit` 项，且只有确有下一页时才返回 `nextCursor`。
- `nextCursor` 固定为最后一项的 UTC `updatedAt` 的 `"O"` 格式、字符 `|` 和 `sessionId`。
- 将 `SessionDescriptor` 映射为 `SessionListItem`。

## Client 要求

- `IRuntimeClient` 增加 `ListSessionsAsync(SessionListParameters, CancellationToken)`。
- `RuntimeClient` 复用现有 `SendControlRequestAsync`，不得直接接触帧或数据库。
- 参数为 null 时立即抛出 `ArgumentNullException`。

## 标题要求

- 仅在新 Session 分支调用 `TrySetInitialSessionTitleAsync`，并保持幂等。
- 从 `runRequest.InitialInput` 中选择第一个压平后非空的 `TextContentBlock.Text`。
- 去掉首尾空白，将换行和连续 Unicode 空白压成一个普通空格。
- 最长 120 个 UTF-16 code unit，不得留下未配对的高代理项。
- 没有非空文本时不写标题；不得改变 `updated_at`。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`

禁止修改其他文件。上述文件包含未提交修改，只能做局部增量补丁，不得重写或格式化整文件。不使用子代理，不运行 Git 提交；主代理负责构建和测试。
