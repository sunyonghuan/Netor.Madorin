# 阶段 7 批次 3 子批次 5：EndToEnd 测试

## 工作目录

- 仓库根目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 项目目录：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`
- 原任务文档：`Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`

## 目标

通过真实 `RuntimeServer`、Named Pipe 和 `RuntimeClient.ListSessionsAsync` 验证 Session 列表、筛选、分页和标题。

## 必须覆盖

1. 启动隔离工作区的真实 RuntimeServer，并用 RuntimeClient 完成认证与初始化。
2. 通过 `StartNewSessionRunAsync` 创建至少 Expert、Meeting、Work 三种模式的 Session；复用项目现有确定性 Provider、Selection 和终态事件帮助模式，不访问外部凭据。
3. 标题输入包含前导/尾随空白、换行、连续 Unicode 空白和超过 120 UTF-16 code unit 的文本；断言标题压平、截断且不留下未配对高代理项。
4. 至少一个请求的第一个 TextContentBlock 为空白、第二个非空，断言使用第二个块；不得拼接后续块。
5. 通过 `RuntimeClient.ListSessionsAsync` 验证默认只返回 Active、mode/status/since/search 组合筛选、limit 分页、只在确有下一页时返回 NextCursor、下一页无重复、无结果页 Sessions 为空且 NextCursor 为 null。
6. 所有断言经 Client 高层 API 完成；测试不得直接调用 Server handler、Named Pipe 帧或 SQLite 来读取列表结果。
7. 使用构造函数注入的 `TestContext.CancellationToken` 和有界超时；完整清理 Server、Client 和临时目录，不使用 `Task.Delay` 等非确定性等待。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/SessionListStage7Tests.cs`

只允许新增上述文件。不得修改生产代码或其他测试，不得运行构建或测试，不得使用子代理。遵循现有 EndToEnd 测试帮助模式，完成后立即停止。
