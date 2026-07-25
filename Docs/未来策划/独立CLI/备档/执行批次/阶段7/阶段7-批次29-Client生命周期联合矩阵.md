# 阶段 7 批次 29：Client 生命周期联合矩阵 : 100%

## 目标与结论

- [√] 补齐 `AttachExisting` 成功附着真实 Runtime 的端到端证据，Client 释放后不终止未拥有的进程。
- [√] 补齐 `StartIfMissing` 优先附着现有 Runtime 的端到端证据，并以不存在的可执行文件证明成功附着时不会启动新进程。
- [√] 以完成双向认证的最小假 Runtime 验证初始化 JSON-RPC 错误稳定映射为 `RuntimeClientProtocolException`。
- [√] 验证 Runtime 未声明 Blob 传输能力时稳定映射为 `RuntimeClientHealthException`，且失败发生在事件通道连接之前。
- [√] 联合既有重连、取消、宿主回调异常、Dispose 和 Runtime 版本切换用例，关闭阶段 7 的 Client 生命周期测试要求。
- [√] 本批不修改生产实现、协议 DTO、协议版本或数据库 Schema。

## 实施证据

- [√] `CreateAsync_ExistingRuntime_AttachesWithoutLaunchingConfiguredExecutable` 以 `AttachExisting` 和 `StartIfMissing` 两组数据运行真实 `madorin.dll serve`，断言绑定 PID、Runtime 实例、工作区和控制端点准确，且 `IsProcessOwned=false`。
- [√] 两组成功附着均配置不存在的 `RuntimeExecutablePath`；`StartIfMissing` 未错误回退启动，Client Dispose 后手动启动的 Runtime 仍存活。
- [√] 最小假 Runtime 使用真实 `NamedPipeTransport`、`FramedChannel`、`HandshakeProtocol`、`AuthenticatedFrameChannel` 和 `FramedControlChannel` 完成 Client Hello、Runtime Hello、Client Confirmation 与认证控制帧交换。
- [√] `CreateAsync_InitializeRpcError_ThrowsProtocolException` 返回非 `-32601` 初始化错误，断言稳定异常类型及原始诊断信息。
- [√] `CreateAsync_MissingBlobCapability_ThrowsHealthException` 返回合法 `InitializeResponse` 和 `BlobTransfer=false`，断言稳定健康异常且不可重试。
- [√] 既有 `ConnectionRecoveryTests`、`RuntimeClientHighLevelStage7Tests`、`ReverseRpcHotReconnectTests` 和 `RuntimeControlTests` 覆盖控制/事件重连、未确认事件重放、Run 取消、回调异常隔离、Dispose 与运行中恢复。

## 测试与门禁

- [√] `RuntimeClientLifecycleStage7Tests` 12 / 12 通过。
- [√] 重连、取消、宿主回调和高层 Client API 相关回归 26 / 26 通过。
- [√] `Madorin.AI.Runtime.EndToEnd.Tests` 258 / 258 通过。
- [√] 全解决方案 835 项通过，3 个真实 Provider 用例因缺少外部凭据跳过，0 失败。
- [√] Debug/Release `--no-restore -warnaserror -m:1` 均为 0 警告、0 错误。
- [√] 协议保持 `1.1`，SQLite Schema 保持 `14`；批次 25 的 Windows `win-x64` Release Native AOT 无警告证据继续有效。
- [√] CodeMap workspace `session` 刷新 1 个 C# 文件、26 个符号，overlay revision 为 `165`。

## 进度与剩余边界

- [√] 07 测试要求由 9 / 14 更新为 10 / 14，总清单由 90 / 96 更新为 91 / 96（94.8%）。
- [√] 执行步骤总览由 763 / 912 更新为 764 / 912（83.8%）。
- [√] 阶段 7 执行计划保持 46 / 54（85.2%）；本批关闭的主清单测试要求已由执行计划 Client SDK 章节覆盖，不对应新的独立未完成项。
- [×] 剩余 5 项继续保留：完整多进程拓扑、同 ID 多实例隔离、首次启动与发布联合验证、双 CLI 进程矩阵，以及新终端/PATH/双击实测。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-批次29-Client生命周期联合矩阵.md`
- `Docs/未来策划/独立CLI/备档/执行步骤/07-CLI-ClientSDK与参考宿主.md`
- `Docs/未来策划/独立CLI/执行步骤/README.md`
- `Docs/未来策划/独立CLI/备档/执行计划/执行计划(阶段7-CLI-ClientSDK与参考宿主).md`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientLifecycleStage7Tests.cs`
