# 阶段 7 批次 3 返工 6：MSTest 上下文与确定性

## 失败会话与证据

- Qwen session：`bec88a58-d396-43d6-bc8f-f974e26dbb9d`
- 会话已完成生产补丁和测试文件，但继续寻找 Shell 工具，在第 31 次调用时超过预算。
- 严格 Debug 构建产生 11 个 `CS0120`：测试把实例属性 `TestContext.CancellationToken` 当作静态成员使用。
- 测试使用 `Task.Delay(50)` 制造时间差，属于不必要的非确定性等待。

## 返工要求

- 通过测试类构造函数注入 `TestContext`，保存到 `private readonly` 字段，并使用该实例的 `CancellationToken`。
- 删除 `Task.Delay`。
- 在标题测试中用参数化 SQL 先把目标 Session 的 `updated_at` 设置为固定历史时间，再读取 before、调用标题写入、读取 after；断言时间完全不变。
- 保留两个测试的行为目标，不修改生产代码。
- 不使用子代理，不运行构建或测试；编辑完成后立即停止。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`

## 验收

- Persistence.Tests 严格 Debug 构建为 0 警告、0 错误。
- 两个 `SessionListStage7Tests` 通过。
- 100k keyset 原回归测试通过。
