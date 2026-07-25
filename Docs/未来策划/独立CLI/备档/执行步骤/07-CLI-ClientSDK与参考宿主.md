# 阶段 7：CLI、Client SDK 与参考宿主 : 100%

> 阶段状态：已完成 · 总进度：100%
>
> 前置阶段：[06C-工作模式与故障恢复](./06C-工作模式与故障恢复.md)

## 0. 进度跟踪

| 类别 | 已完成 | 总数 | 进度 |
| --- | ---: | ---: | ---: |
| 执行任务 | 74 | 74 | 100% |
| 测试要求 | 14 | 14 | 100% |
| 完成标准 | 8 | 8 | 100% |

标记说明：[×] 未完成；[√] 已完成。每完成一个任务，同时更新所属章节、阶段总进度和本表计数。

## 1. 目标

完成全部 CLI 表面、稳定 .NET Client SDK、交互模式、维护命令和多任务参考宿主。`madorin`/`madorin.exe` 提供轻量直接使用入口，宿主程序仍通过公开 Client/Contracts 使用完整 Runtime 能力。

## 2. 前置门禁

- 专家、会议、工作三模式均可通过协议完成、取消和恢复。
- Runtime 具备 Provider、工具、Session、数据库、存储和在线控制服务。
- 维护操作已有服务层 API，不要求 CLI 直接打开内部文件实现业务。
- 独立 CLI 已能从用户目录配置完成基本专家对话。

## 3. 涉及项目

| 项目 | 本阶段职责 |
| --- | --- |
| `Madorin.AI.Runtime.Client` | Runtime 进程、连接、命令、事件、重连和宿主回调 SDK |
| `Madorin.AI.Runtime.Cli` | `madorin` 启动、配置/记忆工具、全部命令、交互模式、输出和退出码 |
| `Madorin.AI.Runtime.Contracts` | 必要的公开请求/响应和查询 DTO |
| `Madorin.AI.Runtime.Server` | 维护、查询和在线控制处理程序 |
| `Madorin.AI.Runtime.SampleHost` | 只通过 Client SDK 的多任务参考集成 |
| `Madorin.AI.Runtime.EndToEnd.Tests` | CLI、SDK、进程、恢复和 SampleHost 测试 |

## 4. 执行步骤 : 100%

### 4.1 冻结 CLI 命令矩阵 : 100%

- [√] 对照 CLI 命令规范建立命令、参数、默认值、是否修改数据、锁要求和退出码表。
- [√] 覆盖 `serve`、`run`、`config`、`agent`、`memory`、`version`、`doctor`、`session`、`db`、`storage`、`ctl`。
- [√] 先解决规范中的命名缺口：`db check` 提示的 `db repair` 与已定义 `db rebuild` 不是同一语义；评审后补充独立 repair 契约或修正文案，禁止实现隐式别名。
- [√] 默认 Pipe/实例品牌使用 `madorin.ai.runtime`，环境变量使用 `MADORIN_AI_*`。
- [√] 所有命令的 `--help`、示例、互斥参数、必填条件和错误提示纳入快照测试。
- [√] 用户命令统一为 `madorin`，不保留 `ai-runtime` 别名。

> **说明**：V1 独立配置的 Provider 管理通过 `config` 子命令（init/edit/show/validate）完整覆盖，不另建 `providers` 命令组。上位 CLI 命令规范中的旧 `providers` 命令组在 V1 中由 `madorin config` 替代，执行文档优先于上位规范中该命令组的定义。
>
> 批次 25 已冻结当前 46 条命令路径、精确 usage、帮助 SHA-256、局部参数、Argument arity、14 项默认值、9 组互斥或条件必填、7 个退出码及逐命令可解析示例，并以 JSON Schema 验证成功/失败信封。

### 4.2 完成 Client SDK 生命周期 : 100%

- [√] 定义 AttachExisting、StartIfMissing、AlwaysStart 等明确启动策略。
- [√] Client Options 包含 Runtime 路径、版本、工作区、数据/日志目录、实例和重连策略；宿主启动不自动读取个人 CLI 配置。
- [√] 每个 Client 句柄创建后固定绑定 `hostInstanceId`、`runtimeInstanceId`、工作区、Runtime PID、控制/事件端点和进程所有权，禁止运行中改绑其他实例。
- [√] 同一宿主可以同时持有多个 Client 句柄，多个宿主进程也可以分别持有多个句柄；SDK 不使用静态“当前 Runtime”、共享事件循环或共享取消源。
- [√] 启动参数使用 `ArgumentList`/参数数组，不拼接 Shell 字符串。
- [√] 启动后执行 PID 校验、双向认证、初始化和能力检查。
- [√] SDK 按 Client 句柄分别管理控制/事件后台循环、心跳、重连和 `(runtimeInstanceId, gsn)` 游标。
- [√] Dispose/DisposeAsync 只关闭自身连接；是否关闭其绑定的 Runtime 依据启动所有权和策略决定，不得枚举或终止其他 Client 拥有的进程。
- [√] 进程启动失败、版本不兼容、认证失败和健康检查失败返回稳定 SDK 异常/结果。

### 4.3 完成 Client 业务 API : 100%

- [√] 提供 New Session Run、Existing Session Run、取消、查询和事件订阅。
- [√] 事件使用 `IAsyncEnumerable` 或等价可取消流，保留 GSN 和 Run 归属。
- [√] SDK 向宿主交付的事件显式携带 `runtimeInstanceId`；查询、取消和回调通过实例绑定 Client 或 `(runtimeInstanceId, runId)` 定位，禁止跨所有实例只按 `runId` 查找。
- [√] SDK 在调用方明确处理/持久化事件后才推进确认水位，不能在交付前确认。
- [√] 提供 Selection get/update、session list/get/messages/resume/rehydrate。
- [√] 提供工具目录发布、权限响应、审批响应、工具调用处理和结果查询回调。
- [√] 回调并发、超时、异常和取消映射为协议响应，异常不得终止事件读取循环。
- [√] SDK 不暴露数据库路径、Pipe 帧、MAF Agent、Provider SDK 或内部 DI 类型。

### 4.4 完成 `run` 和交互模式 : 100%

- [√] `madorin run` 单次模式支持 input/input-file、session、mode、Provider、模型、Agent、timeout、stream/no-stream 和输出文件。
- [√] `madorin` 无参数时从用户目录加载配置并进入 REPL；配置不存在时先运行初始化向导。REPL 支持 `/model`、`/provider`、`/agent`、`/memory`、`/remember`、`/mode`、`/compact`、`/context`、`/session`、`/new`、`/resume`、`/export`、`/tools`、`/history`、`/clear`、`/status`、`/help`、`/exit`。
- [√] 两个独立 CLI 可以在不同工作区并行执行；同一工作区的第二个写实例返回占用错误和持锁实例信息，V1 不自动附着到首个独立 CLI 进程。
- [√] `/model`、`/provider`、`/agent` 更新 NextTurnSelection，只影响下一轮。
- [√] `/mode` 只显示当前模式，不允许在 Session 内修改。
- [√] Run 执行期间第一次 Ctrl+C 只取消当前 Run，不关闭 Session 或 CLI；CLI 等待该 Run 形成唯一 `Cancelled` 终态并完成输出收尾后重新显示提示符，用户可以在同一 Session 中输入修改后的要求。Run 取消收敛期间再次 Ctrl+C，或 REPL 空闲时 Ctrl+C，退出 CLI 并返回 130。
- [√] REPL 使用简单单行提示符，输入一行并按 Enter 提交；Run 执行期间不并发读取下一条输入、不建立消息排队队列，长文本和多行内容通过 `--input-file` 输入。REPL 显示与协议数据分离，不解析自身控制台输出恢复状态。
- [√] `/tools` 必须显示 `builtin.memory.read/append` 为 CLI 内置模型工具，并区分只读与需要审批的写入操作。

> **V1 交互边界**：CLI 是 Runtime、Client SDK 和多 Agent 能力的轻量直接使用入口，不是全屏终端客户端。V1 不实现 Codex 风格固定输入区域、终端光标接管、流式输出期间编辑草稿、多行编辑器、输入历史选择、快捷键系统或全屏 TUI。只有在后续版本明确把 CLI 提升为主要产品入口，并单独完成跨平台终端、中文输入法、粘贴、窗口缩放和 SSH 兼容性评审后，才允许扩展这些能力。

### 4.5 完成配置、Agent、记忆与诊断命令 : 100%

- [√] `version` 支持人类和 JSON 输出。
- [√] `config init/edit/show/validate` 负责协议、Provider、BaseURL、Key、模型和默认项，不增加独立 Provider 管理命令组。
- [√] `agent list/create/edit/delete` 管理 `~/.madorin/agents/*.json`，创建和修改优先使用交互选择。
- [√] `memory init/show/add/edit/clear` 只操作全局和项目两个 `memory.md`；`show effective` 显示实际注入顺序和来源。
- [√] Key 只通过无回显输入写入用户配置，不提供 `--api-key <value>`，显示和诊断始终脱敏。
- [√] `doctor` 按运行环境、个人配置、记忆文件、工作区、数据、Provider、传输、日志和 Blob 顺序检查。
- [√] `doctor --fix` 修改前必须创建备份、展示计划并要求 `--yes` 或交互确认。

> 批次 23 已完成九项固定顺序诊断、配置和 Provider 探测、异常与 API Key 脱敏，以及只针对可恢复数据库损坏的计划、确认、排他锁、恢复点和修复后复检。

### 4.6 完成 Session 命令 : 100%

- [√] `session list` 使用游标分页和 mode/status/date/search 过滤。
- [√] `show` 只通过 Runtime/Store 服务读取 Session 元数据、Selection、最新 Run 和可选消息元数据摘要，不读取消息正文。
- [√] `delete`、`archive/unarchive`、`compact` 形成审计，危险操作需要确认。其中 `archive/unarchive` 已完成幂等状态切换、列表/详情联动和缺失 Session 错误映射，`delete` 已完成显式确认、恢复点和稳定错误映射，`compact` 已完成策略、选择、dry-run、缓存和稳定错误映射。
- [√] delete 默认不直接删除共享 Blob；孤立 Blob 由 storage gc 处理。
- [√] `export` 只通过 Runtime/Store 服务读取 CanonicalHistory，支持 jsonl/markdown/txt，reasoning 和工具详情默认不包含。
- [√] compact 的 dry-run、策略、Provider/模型和缓存复用行为与 ContextProjection 一致。

> 批次 23 已将四类维护操作的 `prepared/completed` 审计写入独立数据区；删除 Session 后审计仍可追溯，compact dry-run 不产生审计写入。

### 4.7 完成数据库与存储命令 : 100%

- [√] `db check` 保持严格只读，不截断、修复或迁移任何内容。
- [√] `db rebuild` 从消息文件重建可恢复索引，并明确无法恢复的 Run/Grant/工具状态。
- [√] `db vacuum`、`db migrate` 和写操作先检查工作区排他锁。
- [√] `db backup` 使用 SQLite 在线备份 API，消息目录是否包含由参数控制。
- [√] `storage check` 校验引用和可选哈希；`storage gc` 默认 dry-run/龄期保护/确认。
- [√] 所有修复、迁移、删除操作先备份或建立恢复点，失败时保留原始诊断。

> 批次 13 已完成独立 `db repair` 的计划、确认、排他锁、恢复点、尾行/索引定向修复和故障保留；批次 14 已完成 SQLite 在线备份、可选消息归档、锁边界和原子发布；批次 15 已完成 `db rebuild` 的只读计划、显式确认、排他锁、全新数据库替换、恢复点、`Recovered` Session、partial 与故障保留；批次 16 已完成 `db vacuum` 的排他锁、缺库零创建、真实空闲页回收与压缩结果报告；批次 17 已完成 `db migrate` 的只读预检、排他锁内复核、恢复点、向前迁移和稳定错误映射；批次 18 已完成 Blob 引用/哈希只读检查、默认 GC 计划、龄期保护、锁内复核、恢复点和中途故障保留。数据库与存储维护组合任务已闭合。

### 4.8 完成 `ctl` 在线控制 : 100%

- [√] `ctl status/sessions/runs/cancel/credential update` 连接已运行实例，不直接访问其数据文件。
- [√] 自动发现只限目标工作区/数据目录和正式实例前缀；`--instance` 必须匹配唯一的 `runtime.pid`，不扫描其他实例。
- [√] cancel 发送协议命令并区分 cancelAccepted 与最终 cancelled。
- [√] credential update 从环境/安全输入读取，不在参数和日志中保留明文。
- [√] 连接失败、实例不匹配和权限失败使用规定退出码。

### 4.9 统一输出和退出码 : 100%

- [√] 默认人类输出简洁可读；JSON/JSONL 模式 stdout 只输出机器数据。
- [√] 诊断日志和进度写 stderr，`--no-color` 和非 TTY 时不输出 ANSI 控制码。
- [√] JSON 成功/失败信封包含 success、data/error、warnings 和 diagnosticId。
- [√] 固定退出码：0 成功、1 通用错误、2 参数/配置、3 认证、4 工作区/数据、5 连接、130 用户中断。
- [√] 流式 JSONL 每行是完整对象，终态行可独立判断成功/失败。
- [√] 输出文件采用安全写入，失败时不覆盖已有有效结果。

> 批次 20 已统一版本、配置、Session、数据库、存储和在线控制命令的 JSON 成功/失败信封，System.CommandLine 解析错误固定返回 2；Run JSONL、stdout/stderr、ANSI 和 Run/Session 原子输出均通过回归。

### 4.10 完成参考宿主 : 100%

- [√] SampleHost 只引用 `Madorin.AI.Runtime.Client` 和必要 Contracts。
- [√] 展示 Runtime 启动/连接、三模式请求、多 Run 并发、事件分流和 GSN 确认。
- [√] 展示单个 SampleHost 同时启动至少三个绑定不同工作区的 Runtime，并通过三个独立 Client 句柄并行收发、取消和关闭。
- [√] 展示工具目录、权限/审批回调、MCP/测试工具执行和 callId 结果查询。
- [√] 展示 Selection 更新、Session resume/rehydrate 和 Runtime 重启恢复。
- [√] Host 不引用任何 Provider SDK、MAF、MEAI、SQLite 或 Runtime 实现项目。
- [√] 示例密钥来自测试配置/环境变量，仓库不包含真实凭据。

> 批次 21 已保留原 `multi-instance` 入口并新增独立 `reference-host` 入口；真实三 Runtime 进程测试覆盖 Expert/Meeting/Work 并发、实例绑定、事件顺序与显式确认、取消、工具权限/审批/callId 查询、Selection 更新、断管重连、rehydrate 和 Runtime 重启后继续同一 Session。SampleHost 项目引用边界测试确认只直接依赖 Client/Contracts，握手 secret 只经 stdin 传递。

### 4.11 补齐使用与运维文档 : 100%

- [√] 编写 PATH 安装、双击启动、用户目录、首次配置、Agent 创建、两个 `memory.md`、最小运行示例，以及“执行中 Ctrl+C 取消当前 Run、返回提示符后修改要求、空闲 Ctrl+C 退出”的交互说明。
- [√] 编写宿主集成、生命周期、工具回调、重连、恢复和版本升级指南。
- [√] 编写安全边界，明确 Restricted、UnrestrictedShell 和非沙箱风险。
- [√] 编写数据库检查、备份、重建、迁移、Blob 清理和故障诊断手册。
- [√] 所有文档示例采用正式 `Madorin.AI.Runtime` 命名和当前命令输出。
- [√] 明确个人 `config.json` 只用于直接启动，宿主通过 Client SDK/协议提供配置和选择。

> 批次 24 新增安装与 CLI 使用、宿主集成、安全与数据运维三份独立指南；维护参数已与当前 Windows `win-x64` Release Native AOT 发布版帮助逐条核对，协议保持 `1.1`，SQLite Schema 保持 `14`。

## 5. 测试要求 : 100%

- [√] 对每条命令的 help、必填项、互斥项、默认值、退出码和 JSON Schema 做快照测试。
- [√] stdout/stderr 分离测试保证 JSON/JSONL 可被脚本稳定解析。
- [√] Client 启动、attach、重连、取消、回调异常、Dispose 和 Runtime 版本切换均有端到端测试。
- [√] 覆盖一个宿主启动多个 Runtime、多个宿主分别启动多个 Runtime，以及宿主与独立 CLI 同时运行的进程拓扑。
- [√] 多 Runtime 故意使用相同 `runId`、相同数值 GSN 和交错事件时，Client 路由、确认、取消、回调、Dispose 和进程回收始终只作用于绑定实例。
- [√] REPL 覆盖 Selection 更新、恢复、压缩和 EOF；Ctrl+C 分别覆盖 Run 执行中首次取消、取消收敛期间再次中断、空闲退出、取消后提示符恢复、同一 Session 重新输入，以及每个 Run 只形成一个终态。
- [√] 覆盖无配置首次启动、配置修改、Agent 增删改、Windows 双击启动和 PATH 命令发现。
- [√] 覆盖两个不同工作区 CLI 进程并行执行、相同工作区互斥、独立取消和退出后锁释放。
- [√] 覆盖 `memory` 命令、`/memory`、`/remember`、工作区缺失、审批拒绝和原子写入失败。
- [√] 覆盖 Tool Catalog 向 LLM 暴露两个记忆工具、参数 Schema、直接进程内执行和结果续调。
- [√] 验证宿主启动不会读取个人 `~/.madorin/config.json`。
- [√] 所有危险维护命令覆盖锁、备份失败、dry-run、拒绝确认和中途故障。
- [√] SampleHost 并发运行三种模式且不引用禁止包。
- [√] 路径/URL/Prompt 特殊字符通过 CLI 参数数组、文件和 SDK 往返不变。

> 批次 27 以真实 Builtin Tool Catalog/Gateway 验证 `memory.read` 结果进入同一 Provider 的第二次调用，并以真实 RuntimeServer、Named Pipe 和 RuntimeClient 验证路径、URL、中文、引号、换行及首尾空白逐字符不变；全解决方案 829 项通过，3 项真实 Provider 用例按凭据条件跳过。

> 批次 28 将 Client 的底层控制 Peer 和 Blob 通道收口为内部测试能力，并以程序集级反射测试禁止全部导出签名暴露 Transport 类型；同时隔离 JsonSchema.Net 的 Schema registry 并补充并发回归。全解决方案 831 项通过，3 项真实 Provider 用例按凭据条件跳过。

> 批次 29 以真实 Runtime 进程补齐 `AttachExisting` 和 `StartIfMissing` 成功附着且不接管进程的证据，并以完成双向认证的最小假 Runtime 验证初始化协议错误与能力健康错误的稳定异常映射；联合既有重连、取消、宿主回调异常、Dispose 和版本切换用例后，生命周期测试矩阵闭合。全解决方案 835 项通过，3 项真实 Provider 用例按凭据条件跳过。

> 批次 30 将两个 SampleHost、四个 Runtime 和第五工作区独立 CLI 纳入同一真实进程拓扑；另以两套完成真实 Named Pipe 双向认证的 Runtime 测试替身固定复用相同 `runId`、数值 GSN 和 `callId`，验证事件、查询、确认、取消、回调与 Dispose 的实例绑定。进程回收隔离由真实多宿主用例联合证明；EndToEnd 259 / 259、全解决方案 836 项通过，3 项真实 Provider 用例按凭据条件跳过。

> 批次 31 补齐首次配置、配置修改、Agent CRUD、REPL Ctrl+C 联合矩阵，以及真实 CLI 多工作区并行、同工作区互斥、独立终止、锁释放、PATH 新终端发现和 Windows Shell 默认 `open` 动词实测；EndToEnd 261 / 261、全解决方案 838 项通过，3 项真实 Provider 用例按凭据条件跳过。

## 6. 完成标准 : 100%

- [√] CLI 命令规范中所有已冻结命令均为完整行为，不再是占位返回。
- [√] 新终端可以直接执行 `madorin`，Windows 双击 `madorin.exe` 可以进入相同的轻量单行 REPL；执行中的 Run 可以取消并返回可继续输入的提示符，不依赖固定输入区域或全屏 TUI。
- [√] 用户可以通过命令完成首次配置、Provider/模型修改和 Agent 管理。
- [√] 用户可以通过 CLI 管理全局/项目记忆，并确认 Agent 下一次启动获得实际有效内容。
- [√] Client SDK 隐藏进程、认证、Pipe、重连、排序和确认细节。
- [√] SampleHost 只依赖 Client SDK 即完成三模式、工具和恢复全链路。
- [√] JSON/JSONL 输出、退出码和文档示例通过自动化测试。
- [√] 安装、配置、集成、运维和安全文档可独立指导使用。

## 7. 风险与禁止事项

- 不得让 CLI 直接绕过服务层修改 `state.db` 或消息文件。
- 不得在 JSON 输出中混入进度、颜色或普通日志。
- 不得将 API Key 放入命令行明文参数。
- 不得由 SampleHost 直接引用 Provider/数据库/Runtime 实现项目。
- 不得为文档中未解决的命令歧义实现多个隐式别名。
