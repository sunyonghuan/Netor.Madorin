# 阶段 7 批次 2：Client 高层 API 与宿主回调

## 目标

- 复用协议 1.0/1.1 已有 DTO，为工具目录替换/补丁、Grant 撤销提供 Client 高层 API。
- 通过 `RuntimeHostCallbacks` 配置权限、审批、工具执行、callId 结果查询和取消回调，宿主不再注册 `ControlPeer` 请求处理器。
- 回调调度限制并发，统一超时、取消和异常到 JSON-RPC 错误的映射；单个回调失败不得终止控制通道接收循环。
- 修正事件读取的自动确认行为；只有调用方执行 `AcknowledgeEventsAsync` 才推进已确认 GSN。

## 允许修改范围

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClientOptions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbacks.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbackDispatcher.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientHighLevelStage7Tests.cs`
- 本批次文档

不得修改 Contracts、Server、Transport、阶段 6C 文件或其他现有测试文件。本批新增 JSON 不得使用反射序列化，必须复用 `RuntimeJsonContext` 已注册元数据。

## 实施检查

- [√] Client 高层工具目录替换、补丁和 Grant 撤销 API。
- [√] 有界宿主回调配置和调度器。
- [√] 回调超时、取消、异常和关联标识校验。
- [√] 重连后自动重新注册宿主回调。
- [√] 事件只由显式确认推进 GSN。
- [√] 高层 API、回调隔离和未确认事件重放 E2E 测试。
- [√] CodeMap overlay 刷新、定向测试、全量 E2E 和严格 Debug 构建。

## 验收命令

```powershell
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~RuntimeClientHighLevelStage7Tests -m:1
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj -c Debug --no-restore -m:1
dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1
```

## Qwen Code 委派记录

- `阶段7-批次2-返工1-Qwen提示识别.md`：新会话误判任务为纯环境说明，未修改文件。
- `阶段7-批次2-返工2-Qwen工具预算.md`：新会话达到工具调用预算，未修改文件。
- `阶段7-批次2-返工3-Qwen未遵守范围.md`：新会话未遵守限定范围且耗尽预算，未修改文件。
- 第四个新会话错误报告提示中没有文件路径和内容，未修改文件。最终实现由主代理完成并审查。

## 验证记录

| 日期 | 范围 | 结果 |
| --- | --- | --- |
| 2026-07-24 | `RuntimeClientHighLevelStage7Tests` | 5 / 5 通过 |
| 2026-07-24 | `Madorin.AI.Runtime.EndToEnd.Tests` Debug | 123 / 123 通过 |
| 2026-07-24 | Client 严格 Debug 构建 | 0 警告、0 错误 |
| 2026-07-24 | E2E 项目严格 Debug 构建 | 0 警告、0 错误 |
| 2026-07-24 | 全解决方案严格 Debug 构建 | 0 警告、0 错误 |
| 2026-07-24 | `git diff --check` | 通过；仅有既有 LF/CRLF 提示 |

## 修改文件

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/IRuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClientOptions.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbacks.cs`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeHostCallbackDispatcher.cs`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientHighLevelStage7Tests.cs`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-Client高层API与宿主回调.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工1-Qwen提示识别.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工2-Qwen工具预算.md`
- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次2-返工3-Qwen未遵守范围.md`
