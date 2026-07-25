# 阶段 7 批次 1：Client SDK 生命周期

## 目标

在保持现有 `RuntimeClientOptions` 调用兼容的前提下，完成 Client SDK 的进程启动策略、实例绑定、版本检查、进程所有权和释放语义。

## 当前事实

- `RuntimeClient` 已完成 Named Pipe 连接、PID 校验、双向认证、初始化、事件通道、心跳和重连。
- `InitializeResponse.ProgramVersion` 可用于 Runtime 版本检查。
- `NamedPipeTransport.PeerProcessId` 可提供实际 Runtime PID。
- `madorin serve` 从 stdin 读取单行 handshake secret；命令行不接受 secret。
- `RuntimeClientOptions` 当前是位置 record，仓库中有大量既有构造调用，必须保持源兼容。
- 当前 `DisposeAsync` 只关闭连接；阶段 7 要求它在明确拥有子进程时按策略处理该进程，绝不能枚举或终止其他实例。

## 必须实现

- 增加 `RuntimeStartPolicy`：`AttachExisting`、`StartIfMissing`、`AlwaysStart`。
- 增加明确的 owned-process 关闭策略，至少支持保留进程和关闭所拥有进程。
- 扩充 `RuntimeClientOptions`：Runtime 可执行文件路径、期望版本、工作区、数据目录、日志目录、实例 ID、Pipe 品牌/前缀、启动策略、启动超时、拥有进程关闭策略；原有位置参数和现有调用必须继续编译。
- 增加只读 `RuntimeInstanceBinding`，公开 `HostInstanceId`、`RuntimeInstanceId`、规范化工作区、Runtime PID、控制端点、事件端点、是否拥有进程。
- 提供一个高层异步创建入口，根据启动策略 attach 或启动 Runtime 后连接；保留现有构造器 + `ConnectAsync` 低层兼容入口。
- 子进程启动统一使用 `ProcessStartInfo.ArgumentList`，`UseShellExecute=false`，secret 只写入重定向 stdin，不放入参数、环境变量或日志。
- `.dll` Runtime 路径通过 `dotnet <dll>` 启动；本机可执行文件直接启动。所有路径和参数逐项加入 `ArgumentList`，不得拼接命令字符串。
- 启动的进程必须绑定其 PID；连接完成后校验握手实例、PID、初始化能力和期望 `ProgramVersion`。
- 失败时抛出稳定的 Client SDK 异常类型，至少区分启动、连接、认证/协议、版本不兼容和健康/初始化失败；保留 inner exception 与可重试标记。
- `DisposeAsync` 只处理当前 Client 的后台循环、连接及其明确拥有的进程。保留进程策略不得终止；关闭策略应先尝试正常结束，仅在超时后回收该确切 PID/Process 对象，不扫描其他进程。
- 公共类型和公共成员写简洁 XML 文档。

## 测试

在新文件 `tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientLifecycleStage7Tests.cs` 中增加 MSTest，至少覆盖：

- 三个启动策略的选择语义。
- 参数数组保持中文、空格、引号和首尾空白值，不经 Shell 二次解析。
- owned 与 attached Client 的绑定信息和 Dispose 隔离。
- 期望版本不匹配返回稳定异常。
- 启动失败不遗留所创建的子进程。

允许为可测试性添加 Client 项目内的 `internal` 构造辅助，并通过现有 `InternalsVisibleTo` 或新建 Client 项目内 AssemblyInfo 授权测试程序集；不要把 Pipe 帧或内部传输公开到 SDK 表面。

## 允许修改

- `Src/Madorin.Ai.Runtime/src/Madorin.AI.Runtime.Client/**`
- `Src/Madorin.Ai.Runtime/tests/Madorin.AI.Runtime.EndToEnd.Tests/RuntimeClientLifecycleStage7Tests.cs`

## 禁止修改

- 其他任何源码、测试、项目文件或文档。
- 阶段 6C 当前未提交文件。
- Contracts、Server、CLI、SampleHost。

## 验收命令

```powershell
dotnet test .\tests\Madorin.AI.Runtime.EndToEnd.Tests\Madorin.AI.Runtime.EndToEnd.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~RuntimeClientLifecycleStage7Tests -m:1
dotnet build .\Madorin.AI.Runtime.slnx -c Debug --no-restore -warnaserror -m:1
```
