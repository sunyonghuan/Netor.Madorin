# 阶段 3 返工：Run Registry、并发与超时

## 1. 执行上下文

- 当前工作目录：`E:\Netor.me\Madorin\Netor.Madorin`
- 执行步骤目录：`E:\Netor.me\Madorin\Netor.Madorin\Docs\未来策划\独立CLI\执行步骤`
- 主任务文档：[03-Run状态机与持久化基线](./03-Run状态机与持久化基线.md)
- 待修改项目：`E:\Netor.me\Madorin\Netor.Madorin\Src\Madorin.Ai.Runtime`

## 2. 返工原因

上一个 Qwen 会话只接收了多行提示词的首段，误扫描主解决方案并在预算耗尽前退出。不要恢复该会话，不要扫描 `Netor.Cortana.slnx` 或 PowerShell 插件。

## 3. 文件白名单

只允许修改：

1. `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServer.cs`
2. `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RuntimeServerOptions.cs`
3. 可新建 `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Server/RunRegistry.cs`
4. `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeControlTests.cs`

禁止修改阶段文档、Contracts、Client、项目文件、中央包配置及其他文件。

## 4. 实现要求

- 将活动 Run、全局容量和取消管理收敛为清晰的 Run Registry/协调组件；只保留轻量句柄，终态后释放 gate、CTS 和 Provider 引用。
- 保留 `MaxConcurrentRuns` 非排队 Busy 行为。
- `RuntimeServerOptions` 新增正数的每 Provider 最大并发和 Run 总超时配置，并在 `StartAsync` 校验。
- Provider A 达到并发上限时不得阻止 Provider B 启动。
- 每个 Run 使用独立 linked CTS 和超时。用户取消只影响目标 Run。
- 用户取消保持 `Cancelled`/`run.cancelled`；超时必须进入 `Failed`/`run.failed`，错误码固定为 `RunTimedOut`，不得落成普通 `RunExecutionFailed`。
- 保持 `credentials.update`、Host lease、shutdown、幂等和唯一终态行为。
- 不增加业务排队，不用全局锁串行 Provider 调用，不新增 NuGet 包，不修改公开协议。
- 代码保持 ASCII，只为复杂逻辑添加简短英文注释。

## 5. 测试要求

使用现有 MSTest、真实 `RuntimeServer`/`RuntimeClient`/Named Pipe、`TestContext.CancellationToken`、合作式超时和确定性 `TaskCompletionSource`。禁止 `Thread.Sleep`。

至少新增：

1. 同 Provider 达限返回 Capacity，另一个 Provider 仍可并行启动。
2. 两个 Run 并行时取消一个不影响另一个。
3. Blocking Provider 达到 RunTimeout 后产生 `run.failed`，错误码为 `RunTimedOut`，`run.query` 为 `Failed`。

保持全部现有测试兼容。可以运行定向测试，但主代理会重新验证。

## 6. 输出要求

最终只报告实际修改文件、关键设计、测试结果和未完成风险。不要运行 Git，不提交，不修改白名单外文件。
