# 阶段 7 批次 3 返工 15：Persistence 剩余 Count 断言

## 来源会话

- Qwen session：`c177ce92-8a8d-4024-8925-f827c89ef192`
- 严格 Debug 构建从 12 个错误降为 2 个错误。

## 剩余错误

`SessionListStage7Tests.cs(262,9)` 和 `(266,9)` 仍触发 `MSTEST0037`。MSTest 4 对 Count 对 Count 比较同样要求集合专用断言。

## 精确修复

- `Assert.AreEqual(allResults.Count, distinctIds.Count, ...)` 改为 `Assert.HasCount(allResults.Count, distinctIds, ...)`。
- `Assert.AreEqual(sessionIds.Count, allResults.Count, ...)` 改为 `Assert.HasCount(sessionIds.Count, allResults, ...)`。
- 保留原断言消息和分页测试的全部其他代码。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`

不得修改其他文件，不得运行构建或测试，不得使用子代理；完成两个局部替换后立即停止。
