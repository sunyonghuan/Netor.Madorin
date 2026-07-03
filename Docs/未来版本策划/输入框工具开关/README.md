# 输入框工具开关

> 思维整理稿 v1，待评审
> 当前状态：📝 思路对齐，等用户拍板细节后展开实施文档
>
> 代码核对（2026-06-13）：方案尚未落地，但仓库内已具备多块可复用基础设施 —— 重写本节澄清现状。
>
> - **数据 / 服务 / 迁移 / 弹窗均不存在**：`ToolBlocklist` 实体、`ToolBlocklistService`、对应迁移与浮窗均无源码。
> - **过滤逻辑已存在但只在一条路径上生效**：[AIAgentFactory.AssembleToolProviders](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L744) 已支持按 `pluginId` / `pluginId:toolName` / `mcpHostId:toolName` 三种粒度过滤，参数名 `taskBlacklist`。但当前**仅** [BuildWorkflowParticipants](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L569) → [BuildSubAgent](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L665) 把参数透传到这里；公开方法 [Build](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L141) 与 [BuildWithSubAgents](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L233) **未暴露该参数**。所以 Chat、Meeting、Work GeneralManager 三条主入口当前无法注入会话级黑名单。
> - **失效与重建机制已就位**：[ToolContextVersionService](../../../Src/Netor.Cortana.AI/Providers/ToolContextVersionService.cs) 提供 `Current` / `Bump()`；[ChatAgentResolver](../../../Src/Netor.Cortana.AI/ChatAgentResolver.cs#L163-L205) 提供 `InvalidateAgentIfToolContextChanged` / `InvalidateAgent` / `BumpToolContextVersion`；[ChatConfigurationCoordinator](../../../Src/Netor.Cortana.AI/ChatConfigurationCoordinator.cs#L71-L76) 已订阅 `OnPluginsChanged` 事件并自动 Bump。UI 层触发"下次对话重建 Agent"复用这套即可，不必新建。
> - **角色级过滤已存在**：[ToolFilterMode](../../../Src/Netor.Cortana.AI/ToolFilterMode.cs) 提供 Full / ReadOnly / MeetingHostExecution / WorkModeManager / None 五档；本方案的 toolBlocklist 是会话级、用户主动屏蔽，与角色级过滤正交叠加（详见 §5.5）。
> - **占位 UI**：[InputAreaView.axaml ToolButton](../../../Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml#L223-L227) 仍是占位符，[code-behind 强制隐藏](../../../Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml.cs#L198)，注释为"工具按钮（已移除，待重构）"。

---

## 一、要解决的问题

三种模式（专家 / 会议 / 工作）输入框下方都预留了"工具"按钮位置（[InputAreaView.axaml:223-227](../../../Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml#L223-L227)，目前 `IsVisible=False`），但**没有任何工具开关功能**。

实际场景：

- 用户开了 5 个插件，每个插件 20-30 个工具，AI 可见的工具一下就过 100，模型决策劣化
- 某些工具（写文件 / 删除 / 命令执行）用户希望在某次对话里临时关掉
- 一个一个去 Agent 配置里改 `EnabledPluginIds` 太重

---

## 二、核心设计

**工具屏蔽是会话级的临时关闭，不是权限管理。** 用户在输入框下方点"工具"按钮 → 弹出浮窗 → 按插件折叠展示 → 勾选屏蔽 → 仅对**当前会话/任务/会议**生效。

### 2.1 复用现有黑名单机制

[AIAgentFactory.AssembleToolProviders](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L744) 已经支持 `taskBlacklist` 参数（HashSet 大小写不敏感），粒度：

- `"pluginId"` —— 整个插件屏蔽（[L773-L774](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L773-L774) `IsPluginEntirelyBlacklisted`）
- `"pluginId:toolName"` —— 单个插件工具屏蔽（[L908-L930](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L908-L930) `AddPluginProvider`）
- `"mcpHostId:toolName"` —— 单个 MCP 工具屏蔽（[L834](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L834)）

但**该参数当前仅 Workflow 路径透传**：[BuildWorkflowParticipants](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L569) → [BuildSubAgent](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L665) → `AssembleToolProviders`。公开的 [Build](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L141) / [BuildWithSubAgents](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L233) 两个方法以及它们的调用方（Chat、Meeting、Work GeneralManager）**全都没有这个参数**。

**所以不重新设计过滤逻辑，做三件事**：

1. 把 `taskBlacklist` → `toolBlocklist` 提升为公开 API：在 `Build` / `BuildWithSubAgents` 两个公开方法上新增可选参数；现有 Workflow 路径的同名参数同步重命名。
2. 把参数贯通到三条入口的调用链（详见 §3.2）。
3. 在数据层 + UI 层提供"会话级勾选状态"，UI 改动通过既有 [ToolContextVersionService](../../../Src/Netor.Cortana.AI/Providers/ToolContextVersionService.cs) 触发 Agent 重建。

### 2.2 数据模型（1 张表，简化）

```sql
CREATE TABLE ToolBlocklists (
    Scope       TEXT NOT NULL,    -- 'chat' / 'meeting' / 'work'
    ScopeId     TEXT NOT NULL,    -- sessionId / meetingId / taskId
    BlockedKey  TEXT NOT NULL,    -- 'pluginId' 或 'pluginId:toolName'
    CreatedAt   INTEGER NOT NULL,
    PRIMARY KEY (Scope, ScopeId, BlockedKey)
);
```

不做全局默认表，不做导出导入，不做配置项 —— 临时关闭就是临时关闭。下次新建会话，默认全开。

### 2.3 UI 浮窗（最简版）

按插件折叠两层：

```text
┌──────────────────────────────────────┐
│ 工具屏蔽 · 当前会话                   │
├──────────────────────────────────────┤
│ ▼ ☑ FileSystemPlugin (3/3)            │
│     ☑ read_file                       │
│     ☑ write_file                      │
│     ☑ delete_file                     │
│ ▶ ☐ GoogleSearchPlugin (5/5)          │
│ ▼ ☑ memory-mcp (2/3)                  │  ← 部分启用,checkbox 三态
│     ☑ remember                        │
│     ☐ forget                          │
│     ☑ recall                          │
├──────────────────────────────────────┤
│ [恢复全部启用]              [完成]    │
└──────────────────────────────────────┘
```

**插件层的 checkbox 三态**：全选 / 全不选 / 部分。点击切换"全选 ↔ 全不选"。
工具层 checkbox 二态。
搜索框、危险等级、徽章、批量操作 —— **都不做**。

---

## 三、改动点

### 3.1 数据层

- 新建 `ToolBlocklists` 表（DB Migration）
- 新建 `ToolBlocklistService`：

  ```csharp
  IReadOnlyCollection<string> GetBlocklist(string scope, string scopeId);
  void Block(string scope, string scopeId, string blockedKey);
  void Unblock(string scope, string scopeId, string blockedKey);
  void ClearAll(string scope, string scopeId);
  ```

### 3.2 Factory 集成

#### 3.2.1 公开方法签名扩展

| 方法 | 当前状态 | 改动 |
| --- | --- | --- |
| [AIAgentFactory.Build](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L141) | 无 `taskBlacklist` 参数 | 新增 `IReadOnlyCollection<string>? toolBlocklist = null`，内部透传给 [AssembleToolProviders](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L744) |
| [AIAgentFactory.BuildWithSubAgents](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L233) | 无 `taskBlacklist` 参数 | 同上；主智能体 + 所有子智能体共享同一份 blocklist |
| [AIAgentFactory.BuildWorkflowParticipants](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L569) | 已有 `taskBlacklist` 参数 | 重命名为 `toolBlocklist`（无外部破坏，仅 Workflow 内部使用） |
| [AIAgentFactory.BuildSubAgent (private)](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L665) | 已有 `taskBlacklist` 参数 | 同步重命名 |
| [AIAgentFactory.AssembleToolProviders (private)](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L744) | 已有 `taskBlacklist` 参数 | 同步重命名；过滤逻辑零改动 |

#### 3.2.2 三模式调用点接入

| 模式 | 调用链入口 | 改动 |
| --- | --- | --- |
| Chat（专家） | [ChatAgentResolver.BuildAgentForTurn](../../../Src/Netor.Cortana.AI/ChatAgentResolver.cs#L120-L158) → `factory.Build` / `factory.BuildWithSubAgents` | 注入 `ToolBlocklistService`，在 `factory.Build*` 调用前读 `GetBlocklist("chat", sessionService.CurrentId)`；空集合按 null 处理 |
| Meeting（会议） | [MeetingAgentBuilder.BuildHostAgentAsync](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs#L82) / [BuildParticipantAgentAsync](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs#L168) | 三处 `_factory.Build(...)` 调用前读 `GetBlocklist("meeting", meetingId)`，主持人和参会者共用同一份 |
| Work（工作） | [GeneralManagerAgentBuilder.BuildAsync](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs#L66) / [BuildWithSubAgentsAsync](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs#L107) → [WorkflowExecutor 调用方](../../../Src/Netor.Cortana.AI/WorkMode/WorkflowExecutor.cs#L121) | `BuildAsync` / `BuildWithSubAgentsAsync` 透传 `toolBlocklist` 给 `_factory.Build`；scope=`"work"`、scopeId=taskId |

`AssembleToolProviders` 内部逻辑零改动，三种粒度过滤逻辑保留。`ToolFilterMode` 与 `toolBlocklist` 正交，详见 §5.5。

### 3.3 UI 层

- 新建 `ToolBlocklistPopup.axaml`（code-behind 模式，沿用项目无 Binding 约定）
- [InputAreaView.axaml ToolButton](../../../Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml#L223-L227) 设为可见、点击打开浮窗；同时移除 [InputAreaView.axaml.cs:198](../../../Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml.cs#L198) 中 `ToolButton.IsVisible = false` 的强制隐藏（按 Chat / Meeting / Work 三种模式分别决定显示策略）
- 浮窗数据源：[PluginLoader.GetActivePlugins](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L94) + [GetActiveMcpServers](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L108) + [GetLoadedPluginInfos](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs#L132)（区分全局 / 工作区 scope），按 `Scope/ScopeId` 取当前屏蔽集合
- 用户勾选 → 立即写库 → 调用 [ChatAgentResolver.BumpToolContextVersion](../../../Src/Netor.Cortana.AI/ChatAgentResolver.cs#L197)（或直接发布 `Events.OnPluginsChanged`，[ChatConfigurationCoordinator](../../../Src/Netor.Cortana.AI/ChatConfigurationCoordinator.cs#L71-L76) 会代为 Bump）→ 下一次 `BuildAgentForTurn` 通过 [InvalidateAgentIfToolContextChanged](../../../Src/Netor.Cortana.AI/ChatAgentResolver.cs#L163) 自动重建 Agent。会议 / 工作模式按各自的"下次构建"时机自然吃到新 blocklist，无需新增事件

### 3.4 系统工具豁免

`start_work_task` 等系统级工具不通过 `IPlugin.Tools` 暴露（它们是 `additionalTools` 直接注入），天然不在浮窗列表里。

---

## 四、范围边界（不做什么）

- ❌ 不引入危险等级（`IPluginTool` 没有元数据，不强行扩展）
- ❌ 不做"高危徽章"（用户自己判断，Tool Description 已经写得够清楚）
- ❌ 不做全局默认 / 配置项（场景太少，会话级足够）
- ❌ 不做"全部高危屏蔽"批量按钮（"恢复全部启用"已是兜底）
- ❌ 不做导出/导入屏蔽预设
- ❌ 不做按 Agent 维度的屏蔽（Agent 配置里 `EnabledPluginIds` 已经管准入）

---

## 五、关键问题与决策

### 5.1 会话切换时屏蔽规则跟着走吗？

**跟着走**。`ScopeId` 就是 `sessionId`/`meetingId`/`taskId`，新建会话默认空集合。
用户切回历史对话 → 自动恢复当时的屏蔽状态。

### 5.2 同一插件在不同 Agent 里启用状态不同怎么办

不冲突。屏蔽逻辑作用在 `AssembleToolProviders` 末尾，先按 `Agent.EnabledPluginIds` 决定准入，再用 `toolBlocklist` 二次过滤。两层独立。

### 5.3 现有 `taskBlacklist` 参数的迁移

仓库内 `taskBlacklist` 仅出现在 [AIAgentFactory.cs](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 内部（`BuildWorkflowParticipants` / `BuildSubAgent` / `AssembleToolProviders`），**无任何外部调用方**（Grep 整个 `Src` 目录确认）。Workflow 参与者目前没有 UI 入口喂值，参数恒为 null。

迁移成本极低：纯内部重命名 `taskBlacklist` → `toolBlocklist`，无破坏性 API 变更；同时为 `Build` / `BuildWithSubAgents` 新增公开参数，由三模式调用方从 `ToolBlocklistService` 读取后传入。

### 5.4 MCP 工具变化频繁

MCP 服务器可能动态增减工具。浮窗每次打开重新拉一次 `GetActiveMcpServers()`，旧的屏蔽记录如果对应 `mcpHostId:toolName` 已不存在，**保留记录但不显示** —— 工具回来时屏蔽自动生效。库里多保留几条孤儿记录无所谓。

### 5.5 与 `ToolFilterMode` 的关系

仓库已有 [ToolFilterMode](../../../Src/Netor.Cortana.AI/ToolFilterMode.cs)（Full / ReadOnly / MeetingHostExecution / WorkModeManager / None），通过 [ToolFilteringContextProvider](../../../Src/Netor.Cortana.AI/Providers/ToolFilteringContextProvider.cs) 在 Agent 决策前按角色裁剪工具列表（如 WorkMode Manager 只看见规划/编排/只读工具，会议参会者只暴露 `list_meeting_attachments` 这类只读控制工具）。

两套机制**正交叠加**，按以下顺序执行：

1. **构建期**：`AssembleToolProviders` 按 `toolBlocklist` 跳过被屏蔽的 plugin / 工具 —— 它们根本不进 Provider 列表
2. **决策期**：`ToolFilteringContextProvider` 按 `ToolFilterMode` 在每次 Function 决策前再过滤一次

| 维度 | `ToolFilterMode` | `toolBlocklist` |
| --- | --- | --- |
| 谁定义 | 系统按 Agent 角色硬编码（Manager / Selector / Host…） | 用户在浮窗里勾选 |
| 生命周期 | Agent 实例级 | 会话/会议/任务级 |
| 粒度 | 按工具名/前缀/可变性启发式 | `pluginId` / `pluginId:toolName` / `mcpHostId:toolName` |
| 失效触发 | 重建 Agent | `BumpToolContextVersion` → 下次 `BuildAgentForTurn` 重建 |

UI 浮窗只列出**当前 `ToolFilterMode` 过滤后仍可见**的工具，避免用户看到根本不会暴露给 AI 的内部工具。

---

## 六、文档结构

| 文档 | 内容 |
| --- | --- |
| README.md（本文） | 问题、设计、改动点、边界 |
| 02-数据模型与服务.md | DDL + `ToolBlocklistService` 接口 + AOT 安全说明 |
| 03-UI浮窗交互.md | 浮窗布局 + 三态 checkbox 实现 + 列表数据源（与 `ToolFilterMode` 联动） |
| 04-Factory集成.md | `Build` / `BuildWithSubAgents` 公开参数新增 + Workflow 内部 `taskBlacklist`→`toolBlocklist` 重命名 + 三模式调用点 |
| 05-实施路线图.md | 阶段 1 数据 / 阶段 2 Factory / 阶段 3 UI / 阶段 4 三模式接入 |

---

**最后更新**：2026-06-13（v1.1：按当前代码对齐参数路径、失效机制、`ToolFilterMode` 关系）
**初稿**：2026-06-01（v1）
