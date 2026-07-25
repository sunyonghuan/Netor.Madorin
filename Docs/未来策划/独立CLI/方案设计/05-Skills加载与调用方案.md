# Madorin AI Runtime Skills 加载与调用方案

> 文档状态：专项策划，待拆分到执行步骤
>
> 适用范围：`Madorin.AI.Runtime` 独立 CLI、Runtime Server、Client SDK 和宿主集成
>
> 前置文档：[总体方案](../README.md) · [架构修订 V1](./02-架构修订-V1.md) · [实现框架参考](./03-实现框架参考.md) · [CLI 命令规范](../命令规范/04-CLI命令规范.md)
>
> 本文只确定方案和后续执行要求，不表示 Skills 能力已经实现，也不修改当前执行进度。

---

## 1. 已确认结论

本方案采用以下确定结论，后续实施不得重新解释：

1. Agent 的实际构建和执行发生在 `Madorin.AI.Runtime` 内，Skills 也由 Runtime 加载并注入 Agent。
2. 独立启动 `madorin` 时，自动加载用户全局目录 `~/.madorin/skills/` 和当前工作区 `<workspace>/.madorin/skills/`。
3. 宿主启动 Runtime 时，工作区 Skills 仍由 Runtime 根据绑定工作区自动定位；宿主只需传入额外的全局 Skills 目录和远程 Skills 地址。
4. 宿主是来源信任的权威方。宿主传入的所有 Skills 来源均视为已信任，Runtime 不再建立白名单、签名审批、信任等级或人工确认流程。
5. 独立 CLI 中由用户配置或显式传入的 Skills 来源同样视为用户已信任，不重复询问。
6. Runtime 只负责来源格式校验、加载、合并、缓存隔离、错误报告和必要的文件/资源边界，不判断 Skill 内容是否“可信”。
7. 当前本地使用的 MAF 版本先实现文件型 Skills；公开契约现在就预留远程来源，后续升级到支持远程 Skills 的 MAF 版本时不改变宿主协议。
8. 普通工具仍由现有 Tool Catalog、权限申请和宿主反向 RPC 执行。Skill 只是告诉模型何时、如何使用能力，不能替代或扩张工具目录。
9. 工作区来源优先于全局来源；宿主传入多个同层级来源时按宿主给出的数组顺序处理。
10. 每次 `AgentInvocation` 使用不可变的 Skills 快照，目录或远程来源变化只影响下一次 Invocation，不改变正在执行的模型调用。

## 2. 目标与非目标

### 2.1 目标

- 让独立 CLI 与宿主模式使用同一套 Skills 加载、合并和 MAF 注入链。
- 支持用户全局 Skills 与项目 Skills 同时生效，并允许项目同名 Skill 覆盖全局 Skill。
- 允许一个宿主进程为不同 Runtime 实例传入不同的全局目录和远程地址。
- 让专家、会议、工作模式及其子 Agent 都能在各自 Invocation 中获得一致的有效 Skills 集合。
- 保持 Skills 来源协议与具体 MAF 版本解耦，远程能力上线时只增加内部适配器。
- 保持普通工具的宿主执行和权限边界不变。
- 提供最小但够用的列举、查看、校验和刷新命令。

### 2.2 非目标

- V1 不建设 Skill 商店、搜索市场、自动推荐、自动下载或自动更新系统。
- 不为宿主已确认的来源再增加 Runtime 侧的信任审批。
- 不设计复杂的 Skill Profile、租户权限模型或按用户角色分发策略。
- 不根据聊天内容自动生成、修改或安装 Skill。
- 不把 Skill 当作普通 Function Tool 注册，也不把 Skill 正文一次性塞入系统提示词。
- 不让 `allowed-tools` 或 Skill 正文自动授予任何普通工具权限。
- 当前阶段不实现任意 Skill 脚本的直接执行；脚本能力见第 11 节的明确边界。

## 3. MAF 能力基线

### 3.1 当前本地版本

当前项目锁定的 `Microsoft.Agents.AI` 版本为 `1.7.0`。该版本已经包含：

- `AgentSkillsProvider`
- `AgentFileSkillsSource`
- `AgentSkillsSource`
- `AgentSkill`、`AgentSkillFrontmatter`
- 文件型 Skill 的 `SKILL.md`、资源和脚本模型
- 多来源聚合、缓存、过滤和按名称去重能力

当前成熟项目已经验证的接入方式是：把用户全局 Skills 目录和工作区 Skills 目录交给 `AgentSkillsProvider`，再通过 `ChatClientAgentOptions.AIContextProviders` 注入每个 MAF Agent。独立 Runtime 应复用这个框架能力，但目录解析、来源优先级、Invocation 快照和宿主协议必须由自身控制。

### 3.2 MAF 的渐进披露

MAF 不会把每个 Skill 的完整正文都预先放进提示词。`AgentSkillsProvider` 先向模型公布名称和描述，模型按需使用以下上下文工具：

```text
load_skill
read_skill_resource
run_skill_script    仅存在可执行脚本并配置 Script Runner 时出现
```

对应流程：

```text
Agent 构建
  -> 提示词只包含有效 Skill 的名称与描述
  -> 模型判断需要某个 Skill
  -> load_skill 读取完整 SKILL.md 指令
  -> 必要时 read_skill_resource 读取引用资料
  -> 模型依据 Skill 指令调用已经存在的普通工具
```

`load_skill` 和 `read_skill_resource` 是 MAF 的上下文读取操作，由 Runtime 内部 Skills Provider 完成；它们不是宿主业务工具，也不需要宿主再次读取相同文件。它们的调用应进入 Invocation 诊断记录，但不得混入宿主 Tool Catalog。

### 3.3 远程 Skills 的后续 MAF 能力

MAF 新版本已经提供基于 MCP 的远程 Skills 来源，其主要形态是：

- `Microsoft.Agents.AI.Mcp`
- `AgentMcpSkillsSource`
- `AgentSkillsProviderBuilder.UseMcpSkills(McpClient)`
- 通过 `skill://index.json` 发现 Skill
- 通过远程资源读取 `SKILL.md` 和附属资源

该版本目前不在本地 NuGet 缓存中，因此本文不把尚未引用的 API 写进当前实现完成状态。公开协议只表达“目录来源”和“远程来源”，Runtime 内部以后用 MAF MCP Skills Source 适配远程地址。

## 4. 概念边界

| 概念 | 责任 |
| --- | --- |
| Skill | 向模型提供某类任务的指令、规范、参考资料和工具使用方法 |
| Skills Source | 发现并提供一组 Skill；当前为目录，后续为远程地址 |
| Effective Skills Catalog | 按来源顺序合并、去重后，本次 Invocation 可见的 Skill 集合 |
| MAF Skills Provider | 把有效 Skill 的摘要和按需加载工具提供给模型 |
| Tool Catalog | 当前 Agent 可以请求的普通工具定义，由 Runtime 与宿主现有机制维护 |
| Tool Grant | 宿主对具体普通工具调用的权限结果 |
| AgentInvocation | 一次 Agent 执行边界，固定使用一个 Skills 快照和一个 Tool Catalog 快照 |

Skill 与普通工具的关系必须保持为：

```text
Skill 指令
  -> 模型决定调用某普通工具
  -> Runtime Tool Gateway
  -> 宿主权限判断
  -> 宿主执行工具
  -> Runtime 把结果交还 Agent
```

以下关系不允许出现：

```text
Skill 文件
  -> 自动创建未由宿主提供的普通工具
  -> 自动获得 allowed-tools 中列出的权限
  -> 绕过 Tool Gateway 在 Runtime 内执行宿主工具
```

## 5. 来源目录与运行模式

### 5.1 独立 CLI

直接执行 `madorin`、`madorin.exe` 或 `madorin run` 时，来源固定为：

```text
用户全局：~/.madorin/skills/
工作区：  <workspace>/.madorin/skills/
```

- `workspace` 使用命令行显式值；未指定时使用当前工作目录的规范化绝对路径。
- 目录不存在表示该作用域没有 Skill，不要求首次启动必须创建空目录。
- 独立 CLI 不读取其他产品的 Skills 目录，也不扫描用户主目录寻找候选目录。
- 独立 CLI 后续如支持远程来源，使用与宿主协议相同的来源描述，不另建第二套加载器。

### 5.2 宿主启动 Runtime

宿主模式的来源由两部分组成：

```text
Runtime 自动来源：<workspace>/.madorin/skills/
宿主传入来源：  skillSources[]，元素可以是 directory 或 mcp
```

- Runtime 从已经绑定的规范化工作区自动推导工作区目录，宿主不重复传入该路径。
- `skillSources[]` 允许为空、一个或多个，目录和远程地址可以按宿主期望的优先级排列在同一数组内。
- `directory` 元素代表宿主希望该 Runtime 实例加载的额外全局目录；`mcp` 元素代表以后由 Runtime 本地连接和加载的远程 Skills 地址。
- 宿主模式不得隐式加载 `~/.madorin/skills/`；宿主若需要该目录，必须把它作为一个 `directory` 元素显式传入。
- 每个 Runtime 实例只使用自身初始化请求中的来源，不读取其他 Runtime 实例的来源状态。

### 5.3 为什么来源通过初始化协议传递

宿主启动参数中的“传递”是 Client SDK 到 Runtime 的结构化初始化参数，不应把数组拼成 Shell 字符串：

```text
Host
  -> 启动 madorin serve，绑定 workspace 和 IPC
  -> 完成 IPC 认证
  -> initialize(skillSources)
  -> Runtime 解析来源并返回实际能力与加载结果
```

这样可以无损传递带空格、中文和特殊字符的目录及 URL，也便于一个宿主同时管理多个来源集合不同的 Runtime。远程认证信息如果以后需要，只能通过安全的结构化协议传递，不写入进程命令行。

## 6. 公开协议预留

### 6.1 初始化来源描述

公开协议使用一个有序来源数组，最小字段为：

```text
skillSources[]
  sourceId       宿主提供的稳定标识，用于日志和错误关联
  kind           directory | mcp
  location       目录绝对路径或远程 MCP 地址
```

示意：

```json
{
  "skillSources": [
    {
      "sourceId": "host-global",
      "kind": "directory",
      "location": "E:\\Company\\MadorinSkills"
    },
    {
      "sourceId": "team-skills",
      "kind": "mcp",
      "location": "https://skills.example.com/mcp"
    }
  ]
}
```

这只是协议形状，不代表当前本地 MAF 包已经实现 `mcp` 来源。设计要求如下：

- 不增加 `trusted`、`approvalState`、`signatureRequired` 等字段；宿主传入即表示宿主已作出信任决定。
- 数组顺序具有稳定语义，同名 Skill 时靠前来源胜出。
- `sourceId` 在单个 Runtime 初始化请求内唯一，不能用于跨实例共享缓存。
- `location` 保留调用方原始值用于诊断，同时生成规范化副本用于实际连接或路径判断。
- URL 的查询参数不得写入普通日志；这属于凭据和日志保护，不属于来源信任判断。
- 后续需要远程认证时，在协议中增加独立的凭据引用或认证对象，不把 Token 拼入 `location`。

### 6.2 工作区来源不进入数组

工作区路径已经是 Runtime 实例的强绑定信息，因此协议不让宿主再次传 `<workspace>/.madorin/skills`。Runtime 在内部把工作区来源插入有效来源序列的第一位：

```text
effectiveSources = [workspaceSource] + initialize.skillSources
```

独立 CLI 没有宿主初始化请求时：

```text
effectiveSources = [workspaceSource, userGlobalSource]
```

这样既保证工作区覆盖优先级，也避免宿主传入一个与绑定工作区不一致的“项目 Skills 目录”。

### 6.3 能力协商

Runtime 能力响应至少公开：

```text
skills.enabled
skills.sourceKinds[]        directory、mcp
skills.resourcesSupported
skills.scriptsSupported
skills.hotRefreshSupported
```

当前实现只完成目录来源时，应返回 `sourceKinds = [directory]`。Client SDK 在发送 `mcp` 来源前可以先检查能力；如果仍发送当前版本不支持的来源，Runtime 返回明确的 `SkillSourceKindNotSupported`，不能静默丢弃。该错误表达的是软件版本能力不足，不是 Runtime 否定宿主的信任决定。

升级到带 `Microsoft.Agents.AI.Mcp` 的 MAF 后，把 `mcp` 加入能力集合，既有宿主请求形状保持不变。

### 6.4 初始化结果

成功响应应返回来源和目录的汇总，不返回完整 Skill 正文：

```text
skillCatalogVersion
sourceResults[]
  sourceId
  kind
  status          loaded | empty | failed
  skillCount
  diagnosticCode
```

缺失的自动默认目录按 `empty` 处理；宿主显式传入但不存在的目录、无法连接的远程地址或完全无法解析的来源按 `failed` 处理并使初始化失败。一个来源内的单个非法 Skill 可以跳过，但必须产生可查询诊断，不能让其他有效 Skill 一起失效。

## 7. Skill 文件规范

### 7.1 最小目录

每个文件型 Skill 使用一个独立目录：

```text
skills/
└── coding-standards/
    ├── SKILL.md
    ├── references/
    │   └── naming.md
    ├── assets/
    │   └── template.json
    └── scripts/
        └── validate.ps1
```

V1 的有效 Skill 至少满足：

- 目录内存在名称固定为 `SKILL.md` 的入口文件。
- `SKILL.md` 使用 UTF-8 文本。
- YAML Frontmatter 至少包含 `name` 和 `description`。
- `name` 使用 MAF 约束的 kebab-case，并与父目录名称一致。
- Skill 正文使用 Markdown，说明适用场景、执行步骤和如何使用已有工具。
- 附属资源由 MAF 文件型来源发现，具体可读扩展名保持在 Runtime 的集中配置中。

最小示例：

```markdown
---
name: coding-standards
description: Apply the project's C# coding and review rules.
---

# Instructions

Read the referenced rules before changing C# code and use the available file tools for edits.
```

### 7.2 不增加私有清单格式

V1 不在 `SKILL.md` 之外再创建 `skill.json`、数据库记录或 Madorin 私有 Frontmatter。来源、覆盖结果和哈希属于 Runtime 运行时清单，不回写到 Skill 目录。

MAF 支持的 Frontmatter 字段可以保留，但按以下规则解释：

| 字段 | Runtime 语义 |
| --- | --- |
| `name` | Skill 的唯一逻辑名称，也是同名覆盖键 |
| `description` | Agent 首次发现 Skill 时可见的摘要 |
| `license` | 展示和诊断信息，不参与运行授权 |
| `compatibility` | 兼容性说明；当前只用于校验和展示 |
| `allowed-tools` | Skill 对工具的使用提示，不是 Tool Grant，不自动授权 |
| `metadata` | MAF 扩展信息；Runtime 不依赖私有键完成核心流程 |

### 7.3 技术边界

对宿主来源的信任不等于取消基本的运行边界。加载器仍需：

- 防止附属资源通过 `..`、符号链接或目录联接点逃逸 Skill 根目录。
- 使用允许的文本/数据扩展名读取资源，未知二进制不自动进入模型上下文。
- 对单文件大小、单 Skill 文件数和总读取量设置集中上限，避免一次模型调用无界占用内存。
- 对无法读取、编码错误和 Frontmatter 错误返回具体诊断。

这些规则只保障加载器稳定性和目录隔离，不对 Skill 的业务内容作可信度判断。

## 8. 来源合并与同名覆盖

### 8.1 固定优先级

Runtime 采用“越靠前优先”的单一规则，不再另建权重系统。

独立 CLI：

```text
1. <workspace>/.madorin/skills
2. ~/.madorin/skills
```

宿主模式：

```text
1. <workspace>/.madorin/skills
2. initialize.skillSources[0]
3. initialize.skillSources[1]
4. ...
```

因此：

- 项目可以用同名 Skill 覆盖全局 Skill。
- 宿主可以通过数组顺序决定多个全局或远程来源之间的覆盖关系。
- Runtime 不根据来源是目录还是网络自行改变宿主给出的顺序。
- 同一来源内部出现两个同名 Skill 时，来源加载失败并给出冲突诊断，不能依赖文件系统枚举顺序随机选一个。

### 8.2 去重键与来源信息

Skill 名称按 MAF 的名称规则比较；跨来源去重使用不区分大小写的规范化名称。胜出的有效条目至少记录：

```text
name
description
sourceId
sourceKind
sourceOrder
contentHash
catalogVersion
```

被覆盖条目不注入 Agent，但保留在诊断结果中，状态为 `shadowed` 并指出胜出来源。该信息只用于解释“为什么某个 Skill 没生效”，不需要持久化完整 Skill 正文。

## 9. 有效目录与 Invocation 快照

### 9.1 Catalog 构建

Runtime 启动或刷新时执行：

```text
解析绑定工作区
  -> 组装有序来源
  -> 分别发现并校验 Skills
  -> 按名称合并和去重
  -> 生成 Effective Skills Catalog
  -> 递增 skillCatalogVersion
```

空目录不会增加无意义版本；只有有效集合、来源状态或 Skill 内容哈希发生变化时才生成新版本。

### 9.2 AgentInvocation 快照

每个 AgentInvocation 开始时固定：

```text
skillCatalogVersion
effectiveSkillNames[]
effectiveSkillHashes[]
skillSourceIds[]
```

快照与已有的 Provider、Agent、提示词和 Tool Catalog 快照一起进入 `InvocationSnapshot`。正在运行的 Invocation 不读取新目录版本，也不因文件变化替换其 `AgentSkillsProvider`。

会议模式的一位发言者、工作模式的一个子 Agent 步骤都各自属于独立 Invocation；它们在真正开始时获取最新 Skills 快照。已经开始的发言或步骤继续使用旧快照。

### 9.3 生命周期

```text
AgentInvocation 开始
  -> SkillCatalogService 取得当前不可变 Catalog
  -> 为本次 Invocation 创建 MAF AgentSkillsProvider
  -> 注入 ChatClientAgentOptions.AIContextProviders
  -> MAF 按需执行 load_skill / read_skill_resource
  -> Invocation 完成
  -> Provider 和快照一起释放
```

目录扫描服务和远程连接可以由 Runtime 实例管理，但 MAF Agent 和绑定的有效目录视图不能跨 Invocation 共享可变状态。

## 10. 刷新与变更生效

### 10.1 本地目录

V1 采用轻量刷新策略：

- Runtime 启动时加载一次。
- 文件监听器观察 `SKILL.md` 和附属资源变化，短防抖后把 Catalog 标记为过期。
- 下一次 AgentInvocation 开始前重建过期 Catalog。
- `madorin skill refresh` 可以立即触发同一重建流程。
- 正在运行的 Invocation 永远不热替换。

目录被删除、重新创建或工作区切换时，必须重建对应监听器。一个 Runtime 实例只监听自己的工作区目录和初始化时收到的目录来源。

### 10.2 远程来源

远程 MAF 能力接入后，V1 扩展只要求：

- Runtime 初始化时发现一次远程 Skills。
- `skill refresh` 或宿主刷新命令重新发现。
- 刷新失败时保留当前 Invocation 的既有快照，并把失败报告给调用方。
- 不在本策划中增加定时同步、后台自动更新、远程版本订阅或跨实例共享下载服务。

MAF 为远程归档建立的本地解压目录必须按 `runtimeInstanceId + sourceId` 隔离，不能让两个 Runtime 实例共用一个可被双方清理的目录。

## 11. Skills 调用与普通工具调用

### 11.1 MAF Skills 上下文工具

MAF 提供的 `load_skill` 和 `read_skill_resource` 属于 Agent 上下文获取机制：

- 由本次 Invocation 的 `AgentSkillsProvider` 注册。
- 只读取已经进入有效 Catalog 的 Skill 和资源。
- 在 Runtime 进程内完成，不向宿主发送普通工具执行请求。
- 不出现在宿主下发的 Tool Catalog 中，也不占用宿主工具名称空间。
- 读取结果只进入当前 Invocation 的模型上下文，不写入 `memory.md` 或项目文件。

Runtime 可以记录 Skill 名称、来源、资源名、耗时和结果大小，但默认不记录完整正文。

### 11.2 Skill 引用普通工具

Skill 正文可以告诉模型使用文件、HTTP、MCP 或宿主业务工具，但实际可见范围仍由本次 Tool Catalog 决定：

```text
Skill 要求使用 tool-a
  -> tool-a 不在当前 Tool Catalog：模型无法调用，返回能力缺失诊断
  -> tool-a 在当前 Tool Catalog：进入现有权限申请和执行链
  -> 宿主拒绝：把拒绝结果返回 Agent，Skill 不产生绕过路径
  -> 宿主允许：由宿主执行并返回结果
```

`SKILL.md` 的 `allowed-tools` 字段只用于提示和校验：

- 可以帮助 `madorin skill validate` 报告当前环境缺少哪些工具。
- 不会把缺失工具添加到 Tool Catalog。
- 不会把已有工具标记为自动批准。
- 不会替代 Tool Grant、用户审批、委派范围或审计记录。

### 11.3 Skill 脚本边界

MAF 支持 `run_skill_script`，但它要求 Runtime 配置 `AgentFileSkillScriptRunner`，这会形成一条新的进程执行路径。当前系统已经确定普通工具由宿主权限和执行链控制，因此 V1 采用以下最小规则：

- 不配置文件型 Skill Script Runner。
- 不向模型公布 `run_skill_script`。
- 包含 `scripts/` 的 Skill 仍可加载其正文和资源，但校验结果提示 `scriptsUnsupported`。
- Skill 需要执行能力时，应在正文中调用宿主已经提供的普通工具。

这不是对 Skill 来源的信任判断，而是避免在 Runtime 内新增第二套工具执行器。以后确实需要 Skill 自带脚本时，必须单独决定脚本由宿主执行还是进入统一 Tool Gateway；在该决策完成前不得直接启用 MAF 示例中的子进程 Script Runner。

## 12. Runtime 内部接入方式

### 12.1 项目职责

Skills V1 不新建一组独立程序集，也不建设插件框架。现有项目按职责承载：

| 项目 | Skills 职责 |
| --- | --- |
| `Madorin.AI.Runtime.Contracts` | 初始化来源 DTO、能力响应、目录查询/刷新消息和错误码 |
| `Madorin.AI.Runtime.Core` | 来源顺序、Catalog、快照和合并规则的框架无关模型 |
| `Madorin.AI.Runtime.Services` | 目录发现、刷新、版本管理及 MAF Provider 工厂 |
| `Madorin.AI.Runtime.Server` | 初始化来源绑定、Invocation 注入和宿主协议处理 |
| `Madorin.AI.Runtime.Cli` | 独立默认目录解析及 `skill` 管理命令 |
| 现有测试项目 | 协议、目录、模式、并发和端到端验收 |

MAF 类型只能出现在 `Services`/`Server` 等实现层，不能进入 `Contracts` 和 `Core` 的公开模型。

### 12.2 当前文件来源

当前 MAF 版本的接入形态为：

```text
SkillCatalogService
  -> 生成有序目录列表和有效 Catalog
  -> 使用 AgentFileSkillsSource / AgentSkillsProvider
  -> SkillContextProviderFactory 为 Invocation 创建 AIContextProvider
  -> AgentFactory 写入 ChatClientAgentOptions.AIContextProviders
```

Runtime 必须显式传递“工作区在前、其他来源按数组顺序在后”的顺序，不能依赖 DI 容器枚举顺序或文件系统顺序。

### 12.3 后续远程来源

升级 MAF 后，在 `Services` 内增加远程来源适配：

```text
kind = mcp
  -> 根据 location 建立 Runtime 自己持有的 McpClient
  -> AgentSkillsProviderBuilder.UseMcpSkills(client)
  -> 与目录来源进入同一个有序 Skills Provider
```

远程连接由 Runtime 本地建立，宿主不负责下载 Skill 正文再转发。宿主只提供来源地址和以后可能需要的连接认证信息。Runtime 不对宿主来源作再次审批，但要把连接失败、协议不兼容和远程资源解析错误原样归入对应 `sourceId`。

### 12.4 最小内部边界

实现只需要保留三个清晰职责，不扩展成通用来源插件系统：

1. `SkillSourceResolver`：把工作区自动来源和调用方来源组成有序列表。
2. `SkillCatalogService`：发现、校验、合并、版本化和刷新有效 Catalog。
3. `SkillContextProviderFactory`：把某一 Catalog 快照转换为本次 Invocation 的 MAF `AgentSkillsProvider`。

远程能力以后作为 `SkillCatalogService` 的另一种来源实现加入，不改变 Agent、模式或宿主 Client SDK 的调用方式。

## 13. Agent 与三种模式

### 13.1 默认可见性

V1 不在 `agent.json` 中增加复杂的 Skills Profile 或逐 Skill 权限表。一个 Runtime 实例的所有 Agent 默认可以发现当前有效 Catalog 中的全部 Skills；真正能使用的普通工具仍取决于该 AgentInvocation 的 Tool Catalog。

这样保持以下行为一致：

- 专家模式：选中的专家 Agent 获得当前有效 Catalog。
- 会议模式：每位参与者在自己的 Invocation 开始时获得当前有效 Catalog。
- 工作模式：总经理和每个子 Agent 在自己的 Invocation 开始时获得当前有效 Catalog。
- 子 Agent 不继承父 Agent 的 Tool Grant，但可以发现相同 Skills。

如果将来确实需要为某个 Agent 隐藏部分 Skills，可在 Agent 定义中增加简单名称过滤；该需求不进入 V1。

### 13.2 提示词组装顺序

一次 Invocation 的上下文顺序保持稳定：

```text
Runtime 基础指令
-> Agent 自身指令
-> 全局 memory.md
-> 项目 memory.md
-> MAF Skills 摘要与加载说明
-> 当前历史投影
-> 本轮用户输入
```

Skills 正文仍按需加载，不在这里整体展开。Memory 是每轮自动注入的简短规范；Skill 是模型按任务选择加载的操作知识，两者不能合并成同一种文件或缓存。

### 13.3 Agent 切换

同一 Session 切换 Provider、模型或 Agent 时不改变 Skills 来源。下一次 Invocation 同时读取最新 Selection、Tool Catalog 和 Skills Catalog；当前 Invocation 继续使用原有三份快照。

## 14. 多实例与并发隔离

必须支持以下场景：

```text
Host A -> Runtime A1 -> Workspace A1 + Sources A1
       -> Runtime A2 -> Workspace A2 + Sources A2

Host B -> Runtime B1 -> Workspace B1 + Sources B1

Terminal C -> madorin -> Workspace C + ~/.madorin/skills
Terminal D -> madorin -> Workspace D + ~/.madorin/skills
```

隔离规则：

- 每个 Runtime 进程拥有自己的来源列表、Catalog 版本、监听器、远程连接和 MAF Provider 实例。
- 多个进程可以并发读取同一全局 Skills 目录，不建立不必要的全局读锁。
- 工作区 Skills 只能来自该 Runtime 绑定的工作区，不能因静态变量或单例缓存串到另一实例。
- Catalog 版本只在单个 `runtimeInstanceId` 内有意义；宿主使用 `(runtimeInstanceId, skillCatalogVersion)` 识别快照。
- 远程下载或解压目录按 Runtime 实例隔离，不跨进程清理或复用。
- 一个 Runtime 内多个 Run 可以共享不可变 Catalog 对象，但每个 Invocation 固定自己的版本引用。
- 某个实例刷新或加载失败不能停止其他 Runtime 进程，也不能修改其他实例的来源状态。

全局目录被多个进程同时修改不属于 Runtime 的写入职责。V1 的 Skills 命令只读取、校验和刷新，不提供安装、编辑和删除，因此不会引入新的跨进程写竞争。

## 15. CLI 与宿主操作面

### 15.1 最小 CLI 命令

只增加以下命令：

```text
madorin skill list [--scope effective|workspace|global]
madorin skill show <name> [--scope effective|workspace|global]
madorin skill validate [--scope effective|workspace|global]
madorin skill refresh
```

行为：

- `list` 显示名称、描述、来源、状态和是否被覆盖，不输出完整正文。
- `show` 显示解析后的 Frontmatter、来源、正文和资源清单，不执行脚本。
- `validate` 检查目录、Frontmatter、同名冲突、资源边界、当前工具缺失和脚本不支持提示。
- `refresh` 让当前独立 CLI 实例立即重建 Catalog；交互会话后续 Invocation 使用新版本。

V1 不提供 `skill install`、`skill remove`、`skill update`、`skill search` 或图形化管理。用户直接维护两个约定目录即可。

### 15.2 宿主操作

宿主通过 Client SDK 使用：

```text
initialize(skillSources)
skill.catalog.get
skill.catalog.refresh
```

来源集合在 Runtime 初始化后固定。宿主要更改目录或远程地址时启动新的 Runtime 实例；`refresh` 只重新读取现有来源，不修改来源数组。这样避免运行中替换来源集合导致多个 Run 对来源归属产生歧义。

### 15.3 交互模式

交互 CLI 只需提供：

```text
/skill list
/skill show <name>
/skill refresh
```

这些斜杠命令操作当前进程的 Catalog，不启动第二个 `madorin` 进程，也不修改 Skill 文件。

## 16. 错误、诊断与记录

### 16.1 稳定错误码

至少定义以下错误或诊断类别：

| 错误码 | 含义 | 处理 |
| --- | --- | --- |
| `SkillSourceKindNotSupported` | 当前 Runtime 版本尚不支持该来源类型 | 初始化失败并报告能力 |
| `SkillSourceNotFound` | 宿主显式目录不存在 | 初始化失败；默认约定目录除外 |
| `SkillSourceUnavailable` | 目录不可读或远程地址无法连接 | 初始化失败；刷新时保留旧 Catalog |
| `SkillDefinitionInvalid` | `SKILL.md` 或 Frontmatter 无效 | 跳过该 Skill，其他有效 Skill 继续加载 |
| `SkillDuplicateInSource` | 同一来源内名称重复 | 该冲突名称不生效并报告具体条目 |
| `SkillResourceBlocked` | 资源越界、类型不允许或超过技术限制 | 拒绝该资源，不影响 Skill 正文 |
| `SkillScriptUnsupported` | Skill 含脚本但当前不支持执行 | Skill 可加载，脚本不注册为工具 |
| `SkillToolUnavailable` | `allowed-tools` 或正文所需工具不在当前目录 | 校验提示；不能自动补充工具 |
| `SkillRefreshFailed` | 已运行实例刷新失败 | 保持旧版本，下一 Invocation 继续使用旧 Catalog |

不得定义 `SkillUntrusted`、`SourceApprovalRequired` 一类错误。宿主或用户已经决定来源是否可信，Runtime 不重复裁决。

### 16.2 结构化诊断

日志和诊断结果建议包含：

```text
runtimeInstanceId
workspaceId
skillCatalogVersion
sourceId
sourceKind
skillName
operation
durationMs
result
diagnosticCode
```

默认不记录：

- 完整 Skill 正文。
- 完整资源内容。
- 带查询凭据的远程 URL。
- 普通工具的敏感参数和结果。

### 16.3 Catalog 变更事件

Catalog 成功更新后发送 `skill.catalog.changed`，至少包含旧版本、新版本、有效 Skill 数和变更名称摘要。事件属于目标 Runtime 实例，宿主按 `runtimeInstanceId` 路由；不使用跨实例的全局 Catalog 版本。

## 17. 实施分段

### 17.1 A 段：契约与本地来源

- 在 `Contracts` 中定义有序 `skillSources[]`、来源种类、能力响应、查询/刷新消息和错误码。
- 在 `Core` 中定义框架无关的来源、有效条目和 Invocation 快照。
- 固定独立 CLI 与宿主模式的来源解析规则。
- 实现工作区与一个或多个全局目录的发现、合并和同名覆盖。
- 接入当前 MAF `AgentSkillsProvider`，只启用 Skill 正文和资源读取。

出口条件：独立 CLI 与 Fake Host 能得到相同的有效 Catalog；工作区同名覆盖全局；模型可以按需调用 `load_skill` 和 `read_skill_resource`。

### 17.2 B 段：Invocation 与现有工具链

- 把 `skillCatalogVersion` 写入 `InvocationSnapshot`。
- 在专家、会议、工作模式的每个 Agent 构建点注入 Skills Provider。
- 验证 Skill 引用的普通工具仍走现有 Tool Gateway 和宿主反向 RPC。
- 固定 `allowed-tools` 不授权和 Script Runner 不启用的行为。
- 完成本地目录监听、下一 Invocation 生效和多 Run 快照隔离。

出口条件：当前 Invocation 不受目录变化影响；下一 Invocation 使用新版本；会议参与者和工作子 Agent 不串用快照或工具权限。

### 17.3 C 段：CLI、Client SDK 与发布验收

- 完成 `skill list/show/validate/refresh` 和交互斜杠命令。
- Client SDK 支持宿主传入有序来源数组、查询 Catalog 和刷新内容。
- SampleHost 演示显式全局目录传入，不隐式依赖用户 `~/.madorin`。
- 完成多宿主、多 Runtime、多工作区并发验证。
- 把 Skills 能力加入 `doctor` 和发布诊断包。

出口条件：用户和宿主都能解释某个 Skill 的来源、覆盖结果和实际生效版本。

### 17.4 D 段：远程 MAF 来源

该段在包含远程 Skills 的 MAF NuGet 版本进入项目后执行：

- 引入匹配版本的 `Microsoft.Agents.AI.Mcp`。
- 把 `kind = mcp` 适配到 `McpClient` 和 `UseMcpSkills`。
- 支持 `skill://index.json` 的发现以及远程 `SKILL.md`、资源读取。
- 将远程来源与目录来源放进同一有序合并链。
- 复用既有 Catalog、Invocation、工具和多实例规则。
- 开启能力响应中的 `mcp`，不修改宿主初始化 DTO。

该段不附带 Skill 商店、签名系统、定时同步或自动安装功能。

### 17.5 与现有执行步骤的映射

本文后续需要拆入现有阶段，而不是单独打乱阶段顺序：

| 执行阶段 | 应补充的 Skills 内容 |
| --- | --- |
| 阶段 1 | 来源 DTO、能力协商、错误码和协议快照 |
| 阶段 2 | 初始化绑定、实例隔离和刷新消息 |
| 阶段 3 | Catalog 版本与 Invocation 快照持久化字段 |
| 阶段 4A | 独立 CLI 的 `~/.madorin/skills` 路径解析 |
| 阶段 5 | Skills 上下文工具与普通 Tool Gateway 的边界 |
| 阶段 6A/6B/6C | 三种模式的 Agent 构建和快照生效点 |
| 阶段 7 | CLI 命令、Client SDK、SampleHost 和宿主来源传入 |
| 阶段 8 | 本地、远程预留、多实例、安全边界和 AOT 验收 |

当前只新增策划，不把上述内容标记为完成，也不改动现有 `11 / 912` 的执行进度。正式实施前应把任务拆成可计数检查项并重新统计总数。

## 18. 测试矩阵

| 类别 | 必测场景 | 预期结果 |
| --- | --- | --- |
| 独立默认来源 | 两个默认目录都不存在 | 正常启动，有效 Catalog 为空 |
| 独立默认来源 | 用户全局目录有一个 Skill | 自动发现并可按需加载 |
| 工作区覆盖 | 全局与工作区同名 | 工作区胜出，诊断显示全局被覆盖 |
| 宿主来源 | 宿主传入多个目录 | 严格按数组顺序合并 |
| 宿主隔离 | 宿主不传 `~/.madorin/skills` | Runtime 不隐式加载个人目录 |
| 路径无损 | 目录包含空格、中文和前导数字 | 来源路径无损解析 |
| URL 无损 | 地址包含编码路径和查询参数 | 连接使用原始语义，日志不泄露凭据 |
| 无效定义 | 缺少 Frontmatter 或名称不匹配 | 跳过该 Skill，报告稳定诊断 |
| 来源内重复 | 同一来源出现同名 Skill | 不随机选择，报告来源冲突 |
| 跨来源重复 | 工作区与宿主来源同名 | 工作区胜出 |
| 资源边界 | `..`、符号链接或联接点越界 | 资源读取被阻止，进程保持正常 |
| 渐进披露 | Agent 未选择 Skill | 不加载完整正文 |
| 渐进披露 | Agent 选择 Skill 和引用资源 | `load_skill`、`read_skill_resource` 返回正确内容 |
| 工具缺失 | Skill 要求不存在的普通工具 | 不新增工具，返回能力缺失 |
| 工具允许 | Skill 调用宿主工具且获批 | 经过现有反向 RPC 并返回结果 |
| 工具拒绝 | 宿主拒绝 Skill 引发的普通工具调用 | Agent 收到拒绝，不能绕过 |
| `allowed-tools` | Skill 声明宿主未授权工具 | 不产生自动 Grant |
| 脚本 | Skill 目录包含脚本 | 不出现 `run_skill_script`，校验给出提示 |
| 快照 | Invocation 运行中修改 Skill | 当前 Invocation 不变，下一 Invocation 更新 |
| 多 Run | 同实例并发多个 Run 后刷新 | 各 Invocation 保持自己的 Catalog 版本 |
| 多实例 | 两个 Runtime 读取同一全局目录 | 可并发读取，版本和监听器不串线 |
| 多工作区 | 两个 Runtime 使用不同项目 Skill | 各自只看到绑定工作区内容 |
| 多宿主 | 不同宿主传入不同来源数组 | 每个 Runtime 只加载自己的来源 |
| 刷新失败 | 更新后目录暂时不可读 | 保留旧 Catalog 并报告刷新失败 |
| 远程能力预留 | 当前版本收到 `kind = mcp` | 明确返回版本能力不足，不静默忽略 |
| 远程接入后 | MCP 提供多个有效 Skill | 与本地来源统一发现、覆盖和加载 |
| 远程实例隔离 | 两实例使用不同远程地址 | 连接、缓存和解压目录完全隔离 |
| 三种模式 | 专家、会议、工作分别调用 Skill | 每个 AgentInvocation 均按规则获得 Catalog |
| Agent 切换 | 同 Session 切换 Agent 后继续 | Session 不变，下一 Invocation 使用最新 Skills 快照 |

## 19. 验收标准

满足以下条件后，Skills V1 本地能力才可视为完成：

1. `madorin` 直接启动可以自动加载用户全局和工作区两个目录。
2. 宿主只传额外来源，Runtime 自动补充绑定工作区来源。
3. 宿主来源不经过 Runtime 信任审批，并严格按数组顺序参与合并。
4. 宿主模式不隐式读取个人 `~/.madorin/skills`。
5. 工作区同名 Skill 稳定覆盖全局或远程 Skill。
6. Agent 通过 MAF 渐进披露加载正文和资源，而不是在启动时注入全部内容。
7. Skill 引用的普通工具仍由现有 Tool Catalog、权限和宿主反向 RPC 执行。
8. `allowed-tools` 无法创建工具或获得权限。
9. V1 没有任何 Skill 脚本直接执行路径。
10. 每个 Invocation 能查询实际使用的 Catalog 版本和有效 Skill 哈希。
11. 本地变化只影响下一 Invocation，当前流不被热替换。
12. 专家、会议和工作模式使用同一规则，子 Agent 不共享可变 Provider 状态。
13. 多个宿主和多个 CLI/Runtime 进程同时运行时，来源、版本、连接和结果不串线。
14. CLI 可以列举、查看、校验和刷新，但不引入商店或自动安装器。
15. 当前版本不支持远程来源时返回明确能力错误；以后升级 MAF 后无需修改宿主来源协议即可启用。

## 20. 禁止事项

- 禁止 Runtime 对宿主传入的 Skill 来源再做信任审批或要求签名。
- 禁止把宿主来源数组拼接成单个命令行字符串传递。
- 禁止宿主重复传入工作区 Skills 路径并覆盖 Runtime 的工作区绑定。
- 禁止宿主模式擅自加载个人 `~/.madorin/skills`。
- 禁止按文件系统未定义枚举顺序解决同名冲突。
- 禁止把全部 Skill 正文预先注入每次系统提示词。
- 禁止让 `allowed-tools` 绕过 Tool Catalog 或宿主审批。
- 禁止直接采用 MAF 示例 Script Runner 在 Runtime 内启动任意子进程。
- 禁止多个 Runtime 共用可变远程下载/解压目录或静态 Catalog。
- 禁止把 MAF 类型写入公开协议、`Contracts` 或 `Core` 边界。
- 禁止远程版本尚未接入时静默忽略宿主传入的远程地址。
- 禁止因新增本文就提前修改执行完成数；必须在任务实际实施并通过门禁后更新进度。
