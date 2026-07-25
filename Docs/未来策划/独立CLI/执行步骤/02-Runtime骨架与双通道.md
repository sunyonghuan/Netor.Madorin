# 阶段 2：Runtime 骨架与双通道 : 98.6%

> 阶段状态：执行中 · 总进度：98.6%
>
> 前置阶段：[01-工程与协议基线](../备档/执行步骤/01-工程与协议基线.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 53 | 53 | 100% |
| 测试要求 | 10 | 11 | 90.9% |
| 完成标准 | 5 | 5 | 100% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

## 1. 目标

实现可独立启动和关闭的 Runtime Server、安全的本地双向连接、控制/事件双通道、Blob 传输与 Client 连接管理。本阶段使用模拟 Run，不接 Provider、不实现工具和三模式业务。

## 2. 前置门禁

- V1 协议版本、初始化消息、事件信封和错误码已经冻结。
- Fake Host 与 Fake Runtime 已完成三模式协议闭环。
- AOT JSON 上下文覆盖握手、控制消息、事件和 Blob 元数据。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Transport.Abstractions` | 帧、控制、事件、Blob 和连接生命周期抽象 |
| `Madorin.AI.Runtime.Transport.NamedPipes` | Named Pipe/Unix 本地传输及平台安全实现 |
| `Madorin.AI.Runtime.Server` | 进程生命周期、组合根、连接接入和健康状态 |
| `Madorin.AI.Runtime.Client` | 启动、连接、认证、重连、订阅和释放 |
| `Madorin.AI.Runtime.Cli` | `serve`、`version`、`doctor` 的阶段 2 行为 |
| `Madorin.AI.Runtime.Protocol.Tests` | 帧与握手一致性测试 |
| `Madorin.AI.Runtime.EndToEnd.Tests` | 真实进程、双通道、重连和退出测试 |

## 4. 执行步骤 : 100%

### 4.1 建立进程和工作区生命周期 : 100%

- [√] `serve` 必须接收 `--workspace`，启动时解析为规范化绝对路径并冻结。
- [√] `--data-dir` 缺省为 `{workspaceRoot}/.madorin`；配置、数据和日志目录分别验证。
- [√] 生成或校验 `instanceId`，默认 Pipe/Socket 前缀使用 `madorin.ai.runtime`。
- [√] 分别创建实例名称锁和规范化工作区写锁并定义重复启动错误；检查和创建必须由操作系统原子机制保证，改变实例名、Pipe 前缀或数据目录不能绕过工作区锁。
- [√] 不同工作区的实例使用不同实例 ID、IPC 端点、数据、日志和临时目录，可以在同一用户会话中同时启动和独立关闭。
- [√] 一个宿主可以同时启动多个 Runtime 子进程，多个宿主进程也可以各自启动多个 Runtime；实现中不得存在进程全局的当前实例、当前 Pipe 或当前工作区单例。
- [√] 组合根只装配本阶段传输和模拟服务，禁止把 CLI 作为服务定位器。
- [√] 捕获 Ctrl+C、宿主关闭命令和进程停止信号，按“停止接入 -> 排空控制任务 -> 关闭通道 -> 释放锁”退出。

### 4.2 实现长度前缀帧 : 100%

- [√] 控制和事件通道统一使用 4 字节大端无符号长度前缀。
- [√] 帧读取必须处理短读、分段到达、EOF、超长声明、无效 UTF-8 和取消。
- [√] 在分配缓冲前校验协商大小上限，拒绝整数溢出和超限帧。
- [√] 控制消息采用 JSON-RPC 2.0 语义；事件通道只发送 Runtime 到 Host 的事件帧。
- [√] 单连接写入串行化，避免并发写导致帧交叉；读取与写入可独立取消。
- [√] 所有 JSON 解析均使用 `RuntimeJsonContext`。

### 4.3 建立双通道与优先级 : 100%

- [√] 控制通道承载初始化、查询、取消、心跳、工具 RPC 和游标确认。
- [√] 事件通道承载 Delta、Invocation、状态、错误和终态。
- [√] 初始化成功后再发布事件通道地址，未认证客户端不能连接事件通道。
- [√] 控制通道与事件通道使用独立读写循环和容量边界。
- [√] 高频 Delta 允许短窗口合并，但不得跨 Run、跨内容类型或改变最终文本。
- [√] 取消、心跳和关闭不得被事件洪峰阻塞。
- [√] 事件通道连接时客户端必须携带控制通道建立时派生的会话凭证（如 sessionToken 或对应 HMAC），Runtime 验证通过后才允许订阅事件流，防止未认证客户端旁路连接事件通道。
- [√] Blob 传输通过独立的 Blob 子通道承载，该子通道复用已认证的控制连接而非单独建立新的 Pipe/Socket；不得将 Blob 数据直接嵌入控制帧或事件帧。

### 4.4 完成平台传输安全原型门禁 : 100%

- [√] Windows 使用当前用户 SID 的 Pipe ACL，只允许预期账户创建和读写实例。
- [√] macOS/Linux 本地 Socket 文件创建后设置为所有者读写，并验证实际权限。
- [√] 宿主在发送认证信息前校验服务端 PID 与刚启动的 Runtime 进程一致。
- [√] Windows 使用系统 Pipe PID API；Unix 的 peer credential 获取方式先做独立原型，验证目标 .NET 10 Runtime 和三种发布架构。
- [√] 任一平台无法可靠获取 peer PID 时，不得静默跳过；记录为阻断项并采用经过评审的同等强度方案。
- [√] Pipe/Socket 名称冲突、抢占和权限不正确必须产生安全诊断并拒绝继续。

本轮已建立独立原型 [`Madorin.AI.Runtime.UnixPeerCredentials`](../../../../Src/Madorin.Ai.Runtime/prototypes/Madorin.AI.Runtime.UnixPeerCredentials/README.md)，并完成生产集成、Windows 编译、Windows `SKIP` 分支及 `linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64` 的交叉发布。2026-07-22 已在 Ubuntu 24.04 / WSL2 完成 `linux-x64` 同用户 PID/UID、`0600` 权限、root 与 `nobody` 双端拒绝、错误 secret/PID、旧 timestamp、重放 nonce、四 Runtime 多实例和 Native AOT 实机验证，并修复 Unix 客户端早于监听 Socket 创建时的就绪竞态。macOS 实机错误用户验证尚未取得，因此第 5 节组合测试仍是唯一未完成项；详见[验证记录](../../../../Src/Madorin.Ai.Runtime/prototypes/Madorin.AI.Runtime.UnixPeerCredentials/验证记录.md)。

### 4.5 实现双向挑战响应 : 100%

- [√] 宿主生成 32 字节 `nonce_c`，Runtime 生成 32 字节 `nonce_s`。
- [√] Runtime 使用共享 secret、角色标签、两个 nonce 和 `instanceId` 计算 HMAC-SHA256。
- [√] 宿主验证 Runtime HMAC、实例 ID、PID 和时间窗口后，再发送宿主 HMAC。
- [√] Runtime 验证 `hostInstanceId` 绑定的宿主 HMAC。
- [√] 双方通过 HKDF-SHA256 派生连接会话密钥，后续控制消息使用会话密钥 MAC。
- [√] 共享 secret 通过受控句柄/匿名管道传递，不进入命令行、普通环境变量或日志；读取后清零可控缓冲。
- [√] 每次宿主启动 Runtime 都生成独立 secret 并绑定预期 `hostInstanceId`、`runtimeInstanceId` 和 PID；secret、会话密钥及 nonce 不得在同一宿主启动的多个子进程之间复用。
- [√] 重放 nonce、时间超窗、HMAC 不匹配或实例不匹配时立即断开。

### 4.6 实现 Blob 通道与 Staging : 100%

- [√] 内联内容超过协商阈值时转换为 Blob 引用。
- [√] Blob 以 64 位长度、SHA-256、内容类型和租约描述，不通过 JSON/Base64 整体装载。
- [√] 分片写入 `staging/`，持续校验单 Blob、单 Run 和全局配额。
- [√] 收齐后验证长度与哈希，再原子发布到内容寻址 `blobs/`。
- [√] 取消、超时或断线后按租约清理未完成 Staging 文件。
- [√] 文件路径只在受控本地实现内部使用，不成为远程可伪造的任意路径引用。

### 4.7 实现连接状态、心跳和租约 : 100%

- [√] Client 状态至少包含 Starting、Connecting、Authenticating、Initializing、Connected、Reconnecting、Closed、Faulted。
- [√] 心跳周期由初始化响应给出；双方记录最后收发时间和连续失败次数。
- [√] 控制通道断开后进入可配置重连窗口，使用同一 `hostInstanceId` 和最后确认 GSN。
- [√] 事件通道单独断开时先恢复事件通道，不重复初始化业务状态。
- [√] 宿主租约到期只触发已定义策略；本阶段模拟 Run 用于验证行为。
- [√] Client 释放时等待后台循环完成并回收由其启动的 Runtime 进程，禁止遗留进程。
- [√] Client 句柄分别记录 Runtime 子进程所有权；关闭、取消、重连或释放一个句柄只能操作其绑定的进程、控制通道和事件通道。

### 4.8 完成阶段 CLI 行为 : 100%

- [√] `version` 输出程序、协议、提交和构建平台；`--json` 遵循统一响应信封。
- [√] `serve` 实现本阶段全部启动参数、错误码和优雅关闭。
- [√] `doctor` 只实现运行时、传输能力、工作区权限、目录权限和 Pipe/Socket 安全检查。
- [√] 尚未进入的 Provider、数据库和业务检查明确报告 `NotImplemented` 或跳过原因，不伪造成功。

## 5. 测试要求 : 90.9%

- [√] 随机分片和短读下，长度前缀帧可以正确重组；超限、截断和无效 UTF-8 被拒绝。
- [√] 10 个以上模拟 Run 产生事件洪峰时，取消和心跳仍能在目标时间内处理。
- [×] 错误 secret、错误 PID、旧 timestamp、重放 nonce、错误用户权限均无法完成认证。
- [√] 同名实例并发启动只能有一个成功，失败实例不破坏已有实例。
- [√] 不同工作区、不同实例 ID 的两个 Runtime 可以同时启动和通信；同一工作区即使使用不同实例 ID，第二个写实例仍被拒绝。
- [√] 两个宿主进程各自同时启动至少两个 Runtime，四个实例都能独立认证、通信、重连和关闭，端点及后台循环不冲突。
- [√] 交换两个实例的 Pipe/Socket、PID、`runtimeInstanceId` 或 secret 进行交叉连接时认证失败；取消或关闭任一实例不影响其余实例。
- [√] 控制/事件任一通道断开后按确定顺序重连，无重复后台循环。
- [√] Blob 覆盖空文件、边界大小、超限、取消、哈希不一致和 1 GiB 稀疏测试数据。
- [√] 路径、URL 和中文 Prompt 经真实进程传输后逐字节一致。
- [√] 正常关闭、认证失败、异常退出和 Client Dispose 均无遗留 Runtime 进程。

## 6. 完成标准 : 100%

- [√] `serve`、`version` 和阶段版 `doctor` 可独立运行。
- [√] Windows 完整安全链路通过；macOS/Linux 传输与 peer credential 原型有验证记录。
- [√] 双通道、认证、心跳、重连、Blob 和关闭端到端测试全部通过。
- [√] 控制消息在事件压力下无饥饿，所有队列都有明确上限。
- [√] Release Native AOT 发布 0 条 trimming/AOT 警告。

## 7. 风险与禁止事项

- 不得把共享 secret 放入 `InitializeRequest`、命令行文本、日志或持久化配置。
- 不得用“先检查名称是否存在，再创建”代替原子实例互斥。
- 不得假定单次 `ReadAsync` 返回完整帧。
- 不得在 Unix 平台未经原型验证就宣称 PID 校验已实现。
- 不得让事件发送锁阻塞控制通道，也不得用 stdout 作为常驻服务业务通道。
