# 阶段 7 批次 3 返工 7：SQLite 时间格式

## 失败证据

- `SessionListStage7Tests` 构建通过，0 警告、0 错误。
- 测试结果：1 / 2 通过。
- `TrySetInitialSessionTitleAsync_DoesNotChangeUpdatedAt` 失败：SQLite 将 `DateTimeOffset` 参数存为 `2020-01-01 00:00:00+00:00`，仓储按固定 `"O"` 格式解析时抛出 `FormatException`。

## 返工要求

- 将测试写入 `$ts` 的值改为固定的 UTC `"O"` 格式字符串，例如 `2020-01-01T00:00:00.0000000+00:00`。
- 不改变生产代码和测试行为目标。
- 不使用子代理，不运行构建或测试；编辑完成后立即停止。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Persistence.Tests/SessionListStage7Tests.cs`
