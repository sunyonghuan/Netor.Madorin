# 阶段 7 批次 3 子批次 4：Persistence 与 Schema 测试

## 工作目录

- 仓库根目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 项目目录：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`
- 原任务文档：`Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`

## 目标

补齐结构化 Session 列表筛选、keyset cursor、非法 cursor 和 Schema 13 到 14 数据保留测试。

## 必须覆盖

1. 在现有 `SessionListStage7Tests.cs` 增加 mode、status、since 和 title search 的结构化查询测试。
2. search 必须证明 `%` 和 `_` 按字面量匹配，不具备 SQL LIKE 通配符语义。
3. 构造多个完全相同 `updated_at` 的 Session，以 `updated_at DESC, session_id DESC` 分页，断言无重复、无遗漏且顺序稳定。
4. 非法 cursor 至少覆盖缺少分隔符和非法时间，断言仓储抛出参数或格式异常，不静默回退。
5. 新增 `SessionListSchemaStage7Tests.cs`，构造真实 Schema 13 数据库，包含至少一个既有 Session 和相关版本记录；执行现有 schema 初始化/迁移入口后，断言升级到 14、`sessions.title` 可空列存在、既有 Session 数据完整保留。
6. 使用真实 SQLite，不 mock SQL；所有异步调用使用构造函数注入的 `TestContext.CancellationToken`；禁止 `Task.Delay` 和依赖墙钟的断言。
7. 保留现有两个兼容性测试和原 100k keyset 测试，不修改其行为目标。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`

不得修改生产代码或其他测试，不得运行构建或测试，不得使用子代理。只做局部补丁和新增独立测试文件，完成后立即停止。
