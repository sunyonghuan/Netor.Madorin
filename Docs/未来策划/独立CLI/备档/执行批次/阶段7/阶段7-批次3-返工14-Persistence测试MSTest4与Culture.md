# 阶段 7 批次 3 返工 14：Persistence 测试 MSTest 4 与 Culture

## 来源会话

- Qwen session：`b70e65e2-eef0-4661-b5ed-e1981abc918f`
- 文件范围正确，新增行为覆盖完整，但严格 Debug 构建失败，0 个警告、12 个错误。

## 构建证据与修复要求

1. `SessionListStage7Tests.cs` 两处 `Assert.ThrowsExceptionAsync` 在 MSTest 4 不存在，改为 `Assert.ThrowsExactlyAsync<ArgumentException>` 和 `Assert.ThrowsExactlyAsync<FormatException>`，保持精确异常目标。
2. 所有对集合 `Count` 的 `Assert.AreEqual` 按 `MSTEST0037` 改为 `Assert.HasCount(expected, collection)`。
3. 所有 `Assert.IsTrue(string.Compare(...) > 0)` 按 `MSTEST0037` 改为 `Assert.IsGreaterThan(0, actualComparison, message)`。
4. `Assert.IsTrue(pageCount > 1)` 改为 `Assert.IsGreaterThan(1, pageCount, message)`。
5. `SessionListSchemaStage7Tests.cs` 两处 `Convert.ToInt32(object)` 按 `CA1305` 传入 `CultureInfo.InvariantCulture`；可增加 `using System.Globalization`。
6. 不得禁用或抑制分析器，不得删除测试，不得改变 mode/status/since/search、字面量 `%/_`、同时间 keyset、非法 cursor 和 Schema 13 到 14 数据保留的行为目标。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListSchemaStage7Tests.cs`

不得修改生产代码或其他测试，不得运行构建或测试，不得使用子代理；只做必要局部编辑，完成后立即停止。
