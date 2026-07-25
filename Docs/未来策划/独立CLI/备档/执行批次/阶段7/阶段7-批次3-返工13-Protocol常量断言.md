# 阶段 7 批次 3 返工 13：Protocol 常量断言

## 来源会话

- Qwen session：`8a0ac26c-f0d9-4a18-8df0-10cc23f0938e`
- 新增的 Protocol 测试文件范围正确，但严格 Debug 构建失败。

## 构建证据

`SessionListContractStage7Tests.cs(14,9)` 触发 `MSTEST0032`：直接断言编译期常量 `MessageTypes.SessionList` 等于字面量，分析器判定条件恒真。

## 返工要求

- 保留验证公开常量 wire value 为 `"session.list"` 的行为目标。
- 通过 `typeof(MessageTypes).GetField(...)` 和 `GetRawConstantValue()` 在运行时读取公开字段值后断言，或采用其他不会被编译器常量折叠且不降低覆盖的简洁方式。
- 不得禁用或抑制 MSTest 分析器，不得删除该测试。
- 不修改其他 4 个测试。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.Protocol.Tests/SessionListContractStage7Tests.cs`

不得修改其他文件，不得运行构建或测试，不得使用子代理；完成后立即停止。
