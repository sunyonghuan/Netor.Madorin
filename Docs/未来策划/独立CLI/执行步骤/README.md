# Madorin AI Runtime V1 执行步骤总览 : 84.3%

> 文档状态：执行中 · 总进度：84.3%
>
> 本目录只定义后续实施顺序、交付物和验收门禁，不表示对应业务能力已经实现。
>
> 已完成阶段的计划、步骤和批次材料统一收录在[备档](../备档/README.md)。

## 0. 总体进度

| 统计项 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 全部检查项 | 769 | 912 | 84.3% |
| 已完成阶段 | 7 | 13 | 53.8% |

总进度按 13 份阶段文档中的全部执行任务、测试要求和完成标准计算，不按阶段数量平均计算。阶段 0、阶段 1、阶段 3、阶段 4、阶段 6B、阶段 6C、阶段 7 已完成，阶段 2 已完成 Windows 与 Linux x64 实机项，仅等待 macOS 实机权限验证；阶段 4A、阶段 4B、阶段 5 和阶段 6A 正在执行。阶段 6B 已闭合会议配置、Schema、角色编排、轮次/超时/终止条件、HITL、动态参与者及并发版本控制、投影、摘要、恢复和显式崩溃注入门禁；阶段 6C 已闭合 Work 计划/修订、步骤调度、子智能体、工具与审批、两级幂等、权限边界、ContextProjection、后台作业生命周期、宿主断线与事件重放、Provider 凭据等待、崩溃恢复、三模式 Resume、损坏诊断和全部故障注入门禁。阶段 7 已闭合 Client SDK、Session/数据库/存储维护、`ctl` 在线控制、统一输出、完整 REPL、工作区互斥、三 Runtime 参考宿主、Doctor、Session 维护审计、使用运维文档、完整命令与 JSON Schema 快照、记忆工具模型续调、SDK 特殊字符串往返、Client 公共传输边界、Schema 并发隔离、Client 生命周期联合矩阵、完整多进程拓扑和同标识多实例隔离，以及记忆、宿主配置隔离、维护故障矩阵、CLI 进程矩阵和 Windows 发布实测。

进度更新规则：

1. 完成检查项后，将对应标记从 `[×]` 改为 `[√]`。
2. 更新检查项所属子步骤和上级执行章节的百分比，百分比按 `已完成数 / 总数` 计算并保留一位小数，结果为整数时可以省略 `.0`。
3. 更新阶段文档 `进度跟踪` 表中的已完成数和百分比。
4. 更新阶段主标题、阶段状态和阶段总进度；存在未完成项时不得标记为已完成。
5. 更新本总览的阶段状态、完成数、阶段进度和总体进度。

阶段状态只使用 `待执行`、`执行中`、`已完成`：完成数为 0 时是待执行，完成数大于 0 且小于总数时是执行中，全部检查项完成时才是已完成。

## 1. 命名基线

原策划文档中的 `Netor.AI.Runtime` 是暂定名。本执行文档统一采用以下正式命名，并在命名冲突时覆盖原策划文档：

| 对象 | 正式名称 |
| --- | --- |
| 项目根目录 | `Src/Madorin.Ai.Runtime` |
| 解决方案 | `Madorin.AI.Runtime.slnx` |
| 项目、程序集和 NuGet 包前缀 | `Madorin.AI.Runtime.*` |
| C# 命名空间前缀 | `Madorin.AI.Runtime.*` |
| CLI 命令 / 可执行文件 | `madorin` / Windows `madorin.exe` |
| Pipe/Socket 默认前缀 | `madorin.ai.runtime` |
| 环境变量前缀 | `MADORIN_AI_` |

`Netor.Anthropic` 是外部包名，不得重命名。`Netor.Cortana.*` 只允许出现在迁移参考和“禁止依赖”说明中，不得成为新 Runtime 的项目引用。

## 2. 设计依据与优先级

实施前必须依次阅读：

1. [总体方案与总需求](../README.md)
2. [实施方案 V1](../方案设计/01-实施方案-V1.md)
3. [架构修订 V1](../方案设计/02-架构修订-V1.md)
4. [实现框架参考](../方案设计/03-实现框架参考.md)
5. [CLI 命令规范](../命令规范/04-CLI命令规范.md)
6. [Skills 加载与调用方案](../方案设计/05-Skills加载与调用方案.md)
7. 本目录中当前阶段的执行文档

技术语义冲突时，以架构修订 V1 为准；CLI 的其余表面行为以 CLI 命令规范为准。

**命令名称以本执行文档为准**：本目录和上位 CLI 命令规范统一使用 `madorin` 命令（Windows 为 `madorin.exe`）；不保留旧暂定名别名。

正式 `madorin` 命名以及本目录新增的 `config`、`agent`、`memory` 命令以当前执行文档为准。原策划只出现未定义的“记忆引用”，全局/项目记忆的 V1 语义以阶段 4B 为准。Skills 的来源、优先级、MAF 注入、宿主传参与远程能力预留以独立 Skills 方案为准；后续实施时再将该方案拆入相关阶段检查项，不得因新增策划文档提前增加已完成数。

## 3. 当前起点

当前骨架已经具备：

- .NET 10、中央包管理、严格编译和 Native AOT 配置。
- 22 个源码项目、1 个 SampleHost 和 6 个测试项目。
- 初始领域模型、协议类型、Run 状态机、Provider/工具/传输抽象。
- CLI 命令树和未实现命令占位。
- Debug 构建及现有 15 项骨架测试通过。

当前骨架不代表 Runtime Server、Provider Adapter、持久化、Named Pipe、内置工具或三种运行模式已经实现。执行人员不得以“项目已存在”代替阶段交付验收。

### 3.1 当前多实例与并发结论

当前 CLI **不具备可运行的进程级多实例能力**。现有代码只建立了 `--instance`、`--pipe-prefix`、`--max-runs` 参数、`RuntimeServerOptions` 配置模型和相关抽象；`serve`、`run` 没有执行处理器，实例锁、工作区锁、进程管理、Named Pipe 和持久化均未实现。单进程多 Run 是另一项 Runtime 能力，不能代替本节要求的多进程、多工作目录隔离。

V1 必须支持以下实际拓扑：

```text
Host A -> madorin serve A1 -> Workspace A1
       -> madorin serve A2 -> Workspace A2

Host B -> madorin serve B1 -> Workspace B1
       -> madorin serve B2 -> Workspace B2

Terminal C -> madorin -> Workspace C
Terminal D -> madorin -> Workspace D
```

评审后的实现边界：

1. 一个或多个宿主进程都可以同时启动和管理多个 `madorin serve` 子进程；用户也可以在多个终端同时启动独立 CLI。
2. 每个 Runtime 进程只绑定一个规范化工作区。不同工作区的进程并行运行，实例 ID、IPC 端点、认证密钥、运行数据、日志、临时文件和生命周期必须隔离。
3. 同一规范化工作区始终只允许一个写实例。改变 `instanceId`、`--pipe-prefix` 或 `--data-dir` 不能绕过工作区写锁。
4. `runtimeInstanceId` 标识 Runtime 进程实例，`hostInstanceId` 标识宿主进程。每条连接在认证后固定绑定这两个标识，不能在连接存续期间切换。
5. GSN 只在单个 Runtime 实例内单调。宿主必须按 `(runtimeInstanceId, gsn)` 保存游标，按 `(runtimeInstanceId, runId)` 路由查询、取消和事件，不能把多个 Runtime 的 GSN 或 Run ID 放进同一无实例归属的键空间。
6. 每个宿主到 Runtime 子进程的启动关系使用独立认证 secret、独立 Client 句柄和独立进程所有权；关闭、取消、重连或认证失败只能影响目标实例。
7. 独立 CLI 默认把运行数据写入 `~/.madorin/data/workspaces/{workspaceKey}`；`workspaceKey` 由规范化工作区路径稳定派生，不在目录名中暴露原始路径。
8. `config.json`、`agents/` 和全局 `memory.md` 仍由同一系统用户的多个 CLI 共享。并发读取允许；修改必须使用跨进程写锁、重新读取最新内容和原子替换。
9. V1 不为独立 CLI 引入自动守护进程或复杂实例池，也不自动附着到首个独立 CLI 进程。相同工作区的第二个进程直接返回明确占用错误和持锁实例信息。
10. 单个 Runtime 内部仍按后续阶段实现多 Run，但进程级隔离测试必须独立存在，不能用单进程多 Run 测试替代。

## 4. 阶段顺序

| 顺序 | 执行文档 | 状态 | 完成数 | 进度 | 核心出口条件 |
| --- | --- | --- | ---: | ---: | --- |
| 0 | [00-项目骨架基线](../备档/执行步骤/00-项目骨架基线.md) | 已完成 | 11 / 11 | 100% | 解决方案、分层、首批抽象、命令树和骨架测试已建立 |
| 1 | [01-工程与协议基线](../备档/执行步骤/01-工程与协议基线.md) | 已完成 | 70 / 70 | 100% | Fake Host 与 Fake Runtime 完成三模式协议闭环 |
| 2 | [02-Runtime骨架与双通道](./02-Runtime骨架与双通道.md) | 执行中 | 68 / 69 | 98.6% | Windows 与 Linux x64 安全链路已闭合；macOS 不同用户 peer credential 待外部实机 |
| 3 | [03-Run状态机与持久化基线](../备档/执行步骤/03-Run状态机与持久化基线.md) | 已完成 | 75 / 75 | 100% | 多 Run、GSN Outbox、Session 与 `.madorin` 基线闭合 |
| 4 | [04-Provider适配层](../备档/执行步骤/04-Provider适配层.md) | 已完成 | 62 / 62 | 100% | 三个 Adapter 通过统一一致性测试 |
| 4A | [04A-独立配置与选择](./04A-独立配置与选择.md) | 执行中 | 65 / 75 | 86.7% | 用户配置、首次向导、Agent 管理和本地装配已通过本机门禁；发布与跨平台项待后续阶段 |
| 4B | [04B-全局与项目记忆](./04B-全局与项目记忆.md) | 执行中 | 50 / 53 | 94.3% | CLI/REPL、宿主一致性、双进程文件安全及 Tool Gateway 装配已闭合，等待后续阶段联合验收 |
| 5 | [05-工具权限与反向RPC](./05-工具权限与反向RPC.md) | 执行中 | 68 / 76 | 89.5% | 目录、权限、审批、幂等和反向 RPC 已闭环，等待跨平台安全矩阵与真实 Provider 验收 |
| 6A | [06A-专家模式与JSONL](./06A-专家模式与JSONL.md) | 执行中 | 64 / 74 | 86.5% | JSONL、索引自愈、有界 Blob 读取和投影已闭合，等待完整恢复与联合门禁 |
| 6B | [06B-会议模式与摘要](../备档/执行步骤/06B-会议模式与摘要.md) | 已完成 | 63 / 63 | 100% | 会议全链路与四个崩溃边界已通过 146 项定向用例 |
| 6C | [06C-工作模式与故障恢复](../备档/执行步骤/06C-工作模式与故障恢复.md) | 已完成 | 77 / 77 | 100% | Work 计划/步骤/子智能体/工具/审批闭环、两级幂等、权限边界、投影、后台作业生命周期、凭据等待、断线重放、崩溃恢复和三模式统一 Resume 已通过全量测试与故障注入门禁 |
| 7 | [07-CLI-ClientSDK与参考宿主](../备档/执行步骤/07-CLI-ClientSDK与参考宿主.md) | 已完成 | 96 / 96 | 100% | Client、CLI/REPL、维护与在线控制、三 Runtime 参考宿主、统一输出、文档、完整测试矩阵及 Windows 发布实测全部闭合 |
| 8 | [08-验收发布与质量门禁](./08-验收发布与质量门禁.md) | 待执行 | 0 / 111 | 0% | 全矩阵、AOT、多平台和发布门槛通过 |

阶段 0、阶段 1、阶段 3、阶段 4、阶段 6B、阶段 6C 已完成，阶段 2 已完成 Windows 与 Linux x64 安全链路，当前只等待 macOS 实机的不同用户 peer credential 拒绝验证。阶段 3 已完成 32 MiB 内存与 512 MiB 磁盘 replay、Delta 丢弃通知、终态文本查询、容量指标、凭据等待单次重试、状态机终态竞争、Outbox 故障恢复、全历史 Schema v7 迁移、Runtime 重启后 rehydrate、Selection Invocation Snapshot 隔离、Session 消息索引分页和 10 万 Session 游标分页基线；JSONL 正文与三模式专属快照的完整恢复已在阶段 6 闭合。阶段 4 的三个正式 Adapter 已完成离线一致性、故障注入、生命周期、脱敏、Blob/Provider 扩展、能力探测、远端取消声明、真实 Provider 冒烟测试分层和 Native AOT 门禁。阶段 4A 已完成独立配置、Agent CRUD、选择优先级、工作区锁、本地 Runtime 专家对话、配置安全及 Windows Native AOT 本机门禁，真正的多进程隔离、Linux/macOS 实机权限、Windows 双击、PATH 安装和阶段 7/8 发布验收仍未勾选；阶段 4B 已闭合 CLI/REPL 命令、双进程追加、配置凭据隔离、宿主/独立 Runtime 一致性及生产 Tool Catalog/Gateway 装配，剩余 Linux/macOS 实机路径矩阵、真实 Provider 调用及阶段 7/8 发布制品联合验收。阶段 5 已闭合协议 1.1、Schema v8、版本化目录、权限/审批/委派、统一网关、内置与反向 RPC 工具、重连恢复和 Expert 工具续调，剩余 Unix 目录句柄相对文件后端、`UnrestrictedShell`、完整安全测试矩阵和真实 Provider 闭环。阶段 6C 已闭合 Work 计划正文与修订、原子步骤启动、Stop/Continue/Retry/AskHost、循环审批、两级幂等、子智能体权限、ContextProjection、后台作业生命周期、断线继续与重放、Provider 凭据等待、永久活动态恢复、三模式统一 Resume、损坏诊断和五个崩溃边界；独立后台 worker、跨进程调度与分布式 Worker 不属于 06C/V1 范围。阶段 6A、6B、6C 虽然分文档管理，仍按 6A -> 6B -> 6C 顺序推进。宿主通过 Client SDK/协议调用 Runtime 始终是主要使用路径。

## 5. 全程不变量

任何阶段都不得破坏以下约束：

- 层级固定为 `Session -> Run -> AgentInvocation -> ProviderRequest`。
- 一个 Run 只能形成一个 `Completed`、`Failed` 或 `Cancelled` 终态。
- 事件使用 Runtime 实例内持久化 GSN 和 Run 内 `runSequence`；发送前先写 Outbox。跨实例游标必须使用 `(runtimeInstanceId, gsn)`。
- 新建与续接使用 `NewSessionRunRequest`、`ExistingSessionRunRequest` 两个 DTO。
- V1 一个 Runtime 实例只绑定一个工作区。
- 一个宿主进程可以持有多个实例绑定的 Client 句柄，多个宿主进程也可以分别启动多个 Runtime；不得使用进程全局的“当前 Runtime”静态状态。
- 查询、取消、事件去重和回调路由使用 `(runtimeInstanceId, runId)` 或实例绑定 Client，不能只按 `runId` 在多个 Runtime 之间查找。
- 每个 Runtime 子进程使用独立 IPC 端点、认证 secret 和所有权记录；停止或释放一个实例不得关闭其他实例。
- 用户主目录 `~/.madorin/config.json` 和 `~/.madorin/agents/*.json` 只服务直接启动的个人 CLI；独立运行数据写入 `~/.madorin/data/workspaces/{workspaceKey}`，宿主运行数据写入宿主工作区 `.madorin`。
- 不同工作区的 Runtime/CLI 实例可以并行；同一规范化工作区只允许一个写实例，修改实例名、Pipe 前缀或数据目录不得绕过工作区锁。
- 独立 CLI 不依赖宿主或 IPC，但必须复用 Runtime 的 Provider、Agent、Run 和持久化服务；宿主模式不得隐式依赖或自动加载个人 CLI 配置。
- 独立配置在启动时一次性加载，V1 不实现 Profile、模式配置目录、远程配置中心或配置热更新。
- Key 可以由向导写入当前用户的 `config.json`；输入不回显，文件限制为当前用户可读，展示、日志和错误全部脱敏。
- 全局和项目记忆只使用 `~/.madorin/memory.md` 与 `<workspace>/.madorin/memory.md`；Agent 启动时自动注入全局后项目内容，项目规则更具体。
- 记忆只由用户命令或经审批的 `builtin.memory` 写入，不自动从对话、项目文件或工具结果提取。
- CanonicalHistory 是 append-only 权威历史；ContextProjection 是每次 Invocation 生成的模型视图。
- 工具意图必须先落库再发送；有副作用调用按 `callId` 幂等。
- 子智能体默认不继承权限；委派只能缩小作用域。
- `Contracts`、`Core` 和公共 Provider 抽象不得暴露 MAF、MEAI 或具体 Provider SDK 类型。
- 协议 JSON 只使用 `System.Text.Json` 源生成元数据，不回退到反射序列化。
- `.madorin` 是 Runtime 保留数据区，Agent 文件工具不得直接访问；两个固定 `memory.md` 只能由 Memory File Service 操作。

## 6. 阶段执行规则

每一阶段必须按以下顺序进行：

1. 复核当前阶段前置门禁和上阶段验收记录。
2. 冻结或版本化本阶段新增的公共契约、Schema 和命令表面。
3. 先建立失败用例、协议快照或测试替身，再实现业务代码。
4. 按项目职责实施，不跨层直接引用具体实现。
5. 完成单元、集成、协议一致性和必要的故障注入测试。
6. 记录性能基线、安全检查和已知限制。
7. 只有完成标准全部满足后，才进入下一阶段。

阶段验收记录至少包含构建号、协议版本、数据库 Schema 版本、测试结果、AOT 警告数、未关闭风险和回退点。Git 提交说明必须使用中文。

## 7. 通用质量门禁

- Debug/Release 均为 0 警告、0 错误，分析器不得通过全局禁用绕过。
- 所有公共异步 API 接受并向下传递 `CancellationToken`。
- 任何网络、文件、进程、Pipe 和数据库操作都有超时、取消和结构化错误。
- 路径、URL、Prompt、JSON、首尾空白和中文内容进行无损往返测试。
- 关键协议类型有快照测试和向后兼容规则。
- 数据库迁移支持预检、备份和失败回滚，不允许静默重建空库。
- 日志不得记录 API Key、共享密钥、完整 Prompt 或敏感工具结果。
- Release Native AOT 发布保持 0 条 trimming/AOT 警告。

## 8. 范围控制

V1 不实施 Gemini 正式 Adapter、跨机器 Runtime、分布式 Worker、Provider 热卸载、完整容器/AppContainer 沙箱、图形化 Workflow、通用 Agent 消息总线、音视频和 Computer Use。遇到这些需求时只记录到 V2 决策清单，不得插入当前阶段扩大范围。
