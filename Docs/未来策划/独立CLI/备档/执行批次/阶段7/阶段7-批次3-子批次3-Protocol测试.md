# 阶段 7 批次 3 子批次 3：Protocol 测试

## 工作目录

- 仓库根目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 项目目录：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`
- 原任务文档：`Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次3-Session列表与筛选.md`

## 目标

新增独立 MSTest 文件，证明 `session.list` 公共契约的默认值、JSON 字段和 AOT source-generated 往返稳定。

## 必须覆盖

1. `MessageTypes.SessionList` 等于 `"session.list"`。
2. `new SessionListParameters()` 的默认值为：`Mode = null`、`Status = Active`、`Since = null`、`Search = null`、`Limit = 20`、`Cursor = null`。
3. 使用 `RuntimeJsonContext.Default.SessionListParameters` 序列化和反序列化完整参数，断言所有值不变。
4. 验证参数 JSON 的 camelCase 字段名稳定，不出现 PascalCase 字段名；断言 `mode`、`status`、`since`、`search`、`limit`、`cursor` 的实际存在性应与当前 source-gen 配置一致。
5. 使用 `RuntimeJsonContext.Default.SessionListResult` 往返包含至少一个 `SessionListItem` 的结果，断言 SessionId、Mode、Status、UpdatedAt、Title 和 NextCursor 不变。
6. 测试不得调用无 `JsonTypeInfo` 的反射序列化重载。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`

只允许新增上述文件。不得修改生产代码或其他测试，不得运行构建或测试，不得使用子代理。遵循现有 MSTest 4 风格，测试类必须 sealed；完成后立即停止。
