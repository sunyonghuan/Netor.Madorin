# 阶段 7 返工：Client 初始化就绪竞态 : 100%

> 状态：已完成

## 问题

解决方案全量测试连续两次在不同的 Runtime 进程用例中失败，均返回
`Method 'initialize' is not registered.`。服务端控制通道的接收循环在构造时启动，
请求处理器随后注册；Client 认证完成后立即初始化，可能命中这段短暂窗口。

## 约束

- 只重试 JSON-RPC `-32601` 映射出的、已标记 `IsRetryable` 的初始化未就绪错误。
- 重试受 `StartupTimeout`、`ConnectTimeout` 和调用方取消令牌共同约束。
- 认证、版本、能力、实例绑定和其他协议错误不得降级或重试。
- 不改变进程所有权与 Dispose 行为。

## 实施检查

- [√] 在 Runtime Client 初始化阶段增加有界就绪重试。
- [√] 定向复跑两个曾失败的进程测试。
- [√] EndToEnd 全量通过。
- [√] 解决方案全量 MSTest 通过，真实 Provider 仅按凭据条件跳过。
- [√] 严格 Debug 构建为 0 警告、0 错误。
- [√] 刷新 CodeMap overlay 并记录验证结果。

## 验证记录

- 返工前解决方案全量第 1 次：`DisposeAsync_KeepAlive_DoesNotStopOwnedRuntime` 失败。
- 返工前解决方案全量第 2 次：`RuntimeProcess_AuthenticatesAndConnectsBothChannels` 失败。
- 两次错误均为初始化处理器尚未注册；单独复跑可通过，符合启动竞态特征。
- 返工后定向复跑两个曾失败用例：2 / 2 通过。
- EndToEnd 全量：149 / 149 通过，未再出现 `initialize` 未注册错误。
- 解决方案全量：716 个通过；3 个真实 Provider 用例因缺少外部凭据按预期跳过。
- 严格 Debug 构建：0 警告、0 错误。
- CodeMap：workspace `session`，overlay revision `48`。

## 修改文件

- `Docs/未来策划/独立CLI/备档/执行批次/阶段7/阶段7-返工-Client初始化就绪竞态.md`
- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/RuntimeClient.cs`
