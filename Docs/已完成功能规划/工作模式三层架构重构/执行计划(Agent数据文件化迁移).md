# 执行计划：Agent 数据文件化迁移

> 状态：**已完成**，0.0–0.5 均已落地
> 日期：2026-06-07
> 关联文档：
> - [工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md)（阶段 0 引用本文档）
> - [智能体创建时机与工具上下文刷新问题讨论.md](../智能体创建时机与工具上下文刷新问题讨论.md)（已解决）

---

## 一、目标与动机

### 1.1 核心目标

把现有 Agent 数据从 SQLite `Agents` 表迁移到用户级文件系统，做到：

1. 智能体定义可分发（git、应用市场、备份）
2. 智能体定义和插件/工具体系**正交**——一个不绑死另一个
3. 简化 Agent 字段，去掉模型参数、上下文限制等不该绑定到 Agent 的设置
4. **保留**插件级别的"绑定到智能体"语义（独立站运维、特定业务专员需要）
5. 解决 [工具上下文刷新](../智能体创建时机与工具上下文刷新问题讨论.md) 悬案
6. 顺手清理 P2 旧动态智能体残留（`BuildDynamicSubAgent` 等死代码）

### 1.2 不做的事

- 不做"智能体应用市场"——使用现有应用市场体系即可
- 不做"项目级 Agent 覆盖"——项目里不放智能体
- 不做"运行时由 LLM 自动创建 Agent"——容易失控，必须用户介入
- 不做"自动降级到通用助手"——缺失 Agent 由用户从市场下载或手动创建
- **不做数据库内迁移、不做 ALTER/DROP**——改用**新数据库文件 `madorin.db`** + 从旧 `cortana.db` 选择性导入（D6 修订版 v3）

### 1.3 必须保护不崩的功能

| 功能 | 当前依赖 | 保护策略 |
|---|---|---|
| @智能体提及 | InputAreaView 按 name 索引 | 改造 AgentService 即可（已按 name） |
| 输入面板智能体下拉 | 列表 + 当前选中 | 列表读文件、选中写系统设置 |
| 启动时选当前 Agent | `IsDefault` 字段 | 改为系统设置 `Agent.DefaultName` |
| 6 个 LLM 工具（sys_list_agents 等） | AgentService CRUD | 改写为操作文件，**本次新增 sys_delete_agent**（D5）|
| 长期记忆控制 | `Agent.AllowWorkflowMemory` | manifest 保留字段 |
| 插件/MCP 绑定到 Agent | `EnabledPluginIds/EnabledMcpServerIds` | manifest 保留 `bound_plugins/bound_mcp` |
| Provider/Model 设置 | 独立体系 | 从旧库 `cortana.db` 导入到新库 `madorin.db` |
| 全局插件 / MCP 配置 | `GlobalPlugins` / `McpServers` | 从旧库导入到新库 |
| 系统设置（Workspace、压缩参数等） | `SystemSettings` | 从旧库导入到新库（过滤 `Agent.*` 相关 key） |

> 升级到 madorin.db 后：旧聊天会话 / 工作任务 / 会议记录、压缩段（CompactionSegments / MeetingCompactionSegments）**都不导入**——新库这部分为空，旧 cortana.db 静默保留作备份。
> 长期记忆数据**不在主库**——它由 `Cortana.Plugins.Memory` 插件维护独立 `memory.db`（[MemoryDatabaseOptions.cs](../../../Plugins/Src/Cortana.Plugins.Memory/Storage/MemoryDatabaseOptions.cs)），主库切换不影响长期记忆。
> Avatar 字段已作为 Agent 文件 schema 与设置页编辑项落地：设置页选择头像时会写入 Agent 目录内资源（如 `assets/avatar.png`），会议气泡会优先读取 `manifest.avatar`，找不到或加载失败时回退内置默认头像。

---

## 二、新模型概览

### 2.1 目录结构（用户级全局）

```text
%APPDATA%/Madorin/agents/             # Windows
~/.madorin/agents/                    # macOS/Linux
  ├─ default/                          # 默认助手（kind: builtin/system，不可删但可改）
  │   ├─ manifest.yaml
  │   ├─ prompt.md
  │   └─ assets/avatar.png            # 可选
  ├─ general-manager/                  # A 角色（kind: builtin/system，不可删）
  └─ project-lead/                     # B 角色（kind: builtin/system，不可删）

%APPDATA%/Madorin/agents-seed/        # 工厂目录（应用资源拷贝过去，已存在的同名 Agent 不覆盖）
  ├─ default/                          # 默认助手种子（原 xiaoyue 内容迁移到此）
  ├─ general-manager/
  └─ project-lead/
```

> **default 是 builtin/system，不可删但可改**——用户编辑 `default/manifest.yaml` 和 `default/prompt.md` 就能把它改成自己的助理（改 display_name / description / prompt）。需要新建独立助理时建一个 `kind: agent` 的新文件夹即可，不动 `default`。这样保证默认助手永远存在，避免删除后 fallback 链断裂。

> 仅预装 3 个内置 Agent。C 类专员（novel-writer / dotnet-developer 等）不预装，由用户从应用市场下载或手动创建。

### 2.2 manifest.yaml 最小 schema

```yaml
# 必填（缺任意一项 → 拒绝加载）
name: novel-writer                    # 唯一标识，与目录名相同；正则 ^[a-zA-Z0-9_-]{1,64}$
display_name: 小说作家                 # UI 显示
description: 按章节大纲撰写小说正文，遵循指定风格和节奏  # 必填，工作模式注入用

# 可选
kind: agent                           # agent（默认） / builtin/system
avatar: assets/avatar.png             # 设置页可编辑；会议气泡优先消费，失败回退默认头像
enabled: true                         # 默认 true
sort_order: 10                        # 列表排序
allow_workflow_memory: true           # 长期记忆控制（沿用现有语义）

# 工具绑定（保留"插件绑定到智能体"语义）
bound_plugins:                        # 仅这些插件对该 Agent 可用；列表中的插件必须已全局安装
  - independent-station-ops           # 独立站运维插件
bound_mcp:                            # 仅这些 MCP Server 对该 Agent 可用
  - novel-toolkit-mcp
```

**严格校验，不容错**：name / display_name / description 三个必填字段任一缺失或为空，加载时拒绝该 manifest（不在 UI 列表中显示，启动日志记录 warn）。这避免了"description 空导致工作模式 dispatch 时模型不知道这个 Agent 能干什么"的二次故障。

`kind` 字段语义：

| kind | 例子 | 可禁用 | 可删除 |
|---|---|---|---|
| `agent` | 普通用户/市场下载的 Agent | ✅ | ✅ |
| `builtin/system` | `default` / `general-manager` / `project-lead`（系统必需） | ❌ | ❌ |

`default` 是 `builtin/system`——用户**不能删除**它，但**可以编辑** `default/manifest.yaml` 和 `default/prompt.md` 的内容把它改成自己的助理（改 display_name / description / prompt）。这样既保证默认助手永远存在（避免删除后无 fallback），又给用户改造空间。

如果用户想加自己的全新助理，让它去新建一个 `agent` kind 的文件夹即可，不需要动 `default`。

`name` 字段约束（**严格校验，不容错**）：

- 字符集：字母、数字、下划线、短横线
- 长度：1–64 字符
- 与目录名严格相等
- 创建时校验、加载时校验
- **加载到不合规的 manifest 直接拒绝加载**（不在 UI 列表中显示，启动日志记录 warn），不做容错降级

### 2.3 prompt.md

完整系统提示词，原 `Agent.Instructions` 字段内容全部迁移到此文件。允许为空（用户在创建后还没写 prompt 时的中间状态）。

### 2.4 删除的字段

| 原字段 | 删除原因 |
|--------|---------|
| `Id` | 文件模式下用 Name 作标识 |
| `DefaultProviderId` | 模型/厂商不绑定 Agent，由会话/任务级决定 |
| `DefaultModelId` | 同上 |
| `Temperature` | 模型级或会话级参数 |
| `MaxTokens` | 模型级参数 |
| `TopP` | 模型级参数 |
| `FrequencyPenalty` | 模型级参数 |
| `PresencePenalty` | 模型级参数 |
| `MaxHistoryMessages` | 会话级参数 |
| `Image` | 与 Avatar 重复，保留 Avatar 即可 |
| `IsDefault` | **改为系统设置全局字段 `Agent.DefaultName`**（见 §三） |

---

## 三、IsDefault 改为系统设置

### 3.1 现状

`Agents` 表里 `IsDefault=1` 标记一条记录为默认。`SetDefault` 操作要 UPDATE 全表（先清零再设新）。当前 [SystemSettings](../../../Src/Netor.Cortana.Entitys/Services/SystemSettingsService.cs) 表里**没有**任何 Agent 相关 key（仅 `System.WorkspaceDirectory / Voice.* / Compaction.* / Memory.ModelId / Platform.BaseUrl / WebSocket.Port`），需新增。

### 3.2 改造后

新增系统设置 key（沿用现有 SystemSettingsService 同步 SetValue/GetValue API）：

```text
Key:    Agent.DefaultName
Group:  Agent
Value:  default                       # 启动时由 AgentSeed 写入
DefaultValue: default
ValueType: string
```

涉及读改：

| 现有调用 | 改造后 |
|---------|-------|
| `agents.FirstOrDefault(a => a.IsDefault)` | `agentFileService.GetByName(systemSettings.GetValue("Agent.DefaultName"))` |
| `AgentService.SetDefault(id)` | `systemSettings.SetValue("Agent.DefaultName", name)`（写一处即可）|
| UI 列表里 `★默认` 标记 | manifest 不读，按系统设置匹配 |
| 用户在输入面板选 Agent → 设默认 | 直接更新系统设置字段 |

### 3.3 优势

- **不扫描所有文件**——查系统设置就一行
- **不修改所有文件**——切换默认时只动一处系统设置
- **下拉框选择更直观**——用户在系统设置页可以下拉选当前默认 Agent
- **删除 Agent 后兜底**——如果默认 Agent 被删，启动时检测到不存在则回退到第一个 enabled Agent

### 3.4 影响点（之前调研的 7 处）

| 位置 | 改造 |
|------|------|
| [AiChatHostedService.cs:1249-1263 (LoadDefaults)](../../../Src/Netor.Cortana.AI/AiChatHostedService.cs) | `var name = systemSettings.GetValue("Agent.DefaultName"); _currentAgent = agents.FirstOrDefault(a => a.Name == name) ?? agents.FirstOrDefault();` |
| [WorkHandoffTools.cs:207](../../../Src/Netor.Cortana.AI/Handoff/WorkHandoffTools.cs) | 同上模式 |
| [AgentSettingsPage.axaml.cs:243,269](../../../Src/Netor.Cortana.UI/Views/Settings/AgentSettingsPage.axaml.cs) | 改写系统设置 |
| [PluginModelCapabilityService.cs:151](../../../Src/Netor.Cortana.AI/Providers/PluginModelCapabilityService.cs) | 同样改用系统设置 |
| [AiConfigToolProvider.cs:165](../../../Src/Netor.Cortana.UI/Providers/AiConfigToolProvider.cs) | 列表 ★ 标记按系统设置匹配 |
| [AiConfigToolProvider.cs:542](../../../Src/Netor.Cortana.UI/Providers/AiConfigToolProvider.cs) | MCP 启用回退到默认 Agent，按系统设置查 |
| [MeetingAgentBuilder.cs:387](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs) | 现仅克隆 IsDefault 字段（用于复制内置标记），文件版后此字段不存在；克隆逻辑改为不复制默认标记，会议模式自身另行处理"哪个是默认主持人" |

### 3.5 SystemSetting key 注册

启动时由 `AgentSeedFromFactoryService` 兜底写入（首次启动且 key 不存在才写）：

```csharp
if (string.IsNullOrEmpty(_systemSettings.GetValue("Agent.DefaultName")))
{
    _systemSettings.SetValue("Agent.DefaultName", "default");
}
```

---

## 四、工具源策略

### 4.1 保留"插件绑定到智能体"

按你的反馈，**保留现有插件绑定 Agent 的语义**：

- 独立站运维 Agent 只能用独立站运维插件
- 该插件也只能在该 Agent 下被加载

manifest 字段 `bound_plugins` / `bound_mcp` 直接对应原 `EnabledPluginIds` / `EnabledMcpServerIds`，**语义不变**。

**前提**：`bound_plugins` 列出的插件必须**已全局安装**（`%APPDATA%/Madorin/plugins/`）。manifest 只声明"该 Agent 启用全局已安装的某插件"，并不安装插件本身。

### 4.2 全局插件机制不变

现状：[GlobalPlugins](../../../Src/Netor.Cortana.Entitys/Services/GlobalPluginService.cs) 表里 `IsEnabled=1` 的插件**所有 Agent 默认可用**，不需要每个 Agent 单独绑定。这块**保持原样**，不动。

### 4.3 插件安装作用域：仅 Global

调研结论（[PluginLoader.cs:20-24](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs)）：

```csharp
public enum PluginInstallScope
{
    Global   // 仅此一个枚举值
}
```

**当前插件只有全局安装一种**——历史曾有"工作目录安装插件"（早期 SDK 老程序的功能），现已移除。`PluginInstallScope` 枚举保留 `Global` 作单一值是为了保持 API 形状，未来若再加新作用域不破坏接口。

[AIAgentFactory.cs:772](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 的 `if (pluginInfo.Scope != PluginInstallScope.Global) continue;` 在当前逻辑下**永远不进入 continue 分支**——所有插件都是 Global。这不影响新方案，但读代码时要知道这一行实质上没在过滤任何东西。

### 4.4 注入逻辑（[AIAgentFactory.AssembleToolProviders](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L737-L854)）

```text
最终工具集 = 全局已启用插件 ∪ Agent 绑定插件
          ∪ Agent 绑定 MCP
          - 任务级黑名单
          - 会话级开关（动态收窄，按规划落地）
```

仅把数据来源从 `agent.EnabledPluginIds` 改为读 manifest 的 `bound_plugins`，**逻辑零变化**。

迁移期字段映射（在 §八 数据迁移阶段被旧 → 新转换 → 但 D6 改为不迁移后此映射只在新 Agent 创建时使用）：

```text
manifest.bound_plugins  ← Agent.EnabledPluginIds
manifest.bound_mcp      ← Agent.EnabledMcpServerIds
```

### 4.5 动态收窄（按现有规划）

[输入框工具开关](../../未来版本策划/输入框工具开关) 已有规划，本次**不顺带实现**，但要做好接口预留：

- `AssembleToolProviders` 增加 `sessionToolFilter` 参数（可空）
- 任务级黑名单字段 `ToolBlacklistJson` 保持现有语义

---

## 五、6 个 LLM 工具改造

[AiConfigToolProvider.cs](../../../Src/Netor.Cortana.UI/Providers/AiConfigToolProvider.cs)

| 工具名 | 状态 | 改造点 |
|--------|------|-------|
| `sys_list_agents` | 改造 | 改读文件目录索引 |
| `sys_add_agent` | 改造 | 写 `manifest.yaml` + `prompt.md`；**Description 必填**校验 |
| `sys_set_default_agent` | 改造 | 写系统设置 `Agent.DefaultName` |
| `sys_get_agent_instructions` | 改造 | 读 `prompt.md` |
| `sys_update_agent_instructions` | 改造 | 写 `prompt.md` |
| `sys_delete_agent` | **新增** | 软删到 `agents/.trash/`，**不走 HITL**，依赖 ask_user 提示词约束；`builtin/system` 拒绝删除 |

### 5.1 sys_add_agent 描述更新

旧描述：`"Add a new agent (assistant/proxy/Agent)"` 接受 name + instructions。

新签名补必填校验：

```csharp
[Description("创建一个新的智能体。name 是唯一标识，description 必填用于工作模式调度。")]
public string AddAgent(string name, string description, string instructions);
```

迁移期 instructions 仍可空字符串（避免破坏调用方），但 description 校验非空。

### 5.2 sys_delete_agent 设计

```csharp
[Description("删除指定智能体。该工具直接执行删除（软删到 .trash），调用前必须先用 ask_user 工具确认用户意图；内置智能体不可删除。")]
public string DeleteAgent(string name);
```

**不走 HITL 工具授权机制**。原因：调研发现现有 HITL（[HitlTools.cs:25-66](../../../Src/Netor.Cortana.AI/Hitl/HitlTools.cs)）是非阻塞的——工具调用返回"已发起请求"立即结束，用户回复异步到达。这与"删除前需用户确认"的同步语义不匹配。

改用提示词约束 A：

> 在调用 `sys_delete_agent` 之前，**必须先用 `ask_user` 工具询问用户**："确认删除智能体 X 吗？该操作会移到回收站，可恢复。"
> 用户明确回答"确认"或类似肯定意思后，再调 `sys_delete_agent`。
> 用户拒绝或不确定时，不调用 `sys_delete_agent`。

执行流程：

```text
DeleteAgent(name)
  ├─ 校验：name 非空
  ├─ 查找：AgentFileService.GetByName(name)
  │   └─ 不存在 → 返回 "未找到智能体: {name}"
  ├─ 校验：manifest.kind 是否为 "builtin/system"
  │   └─ 是 → 返回 "系统内置智能体不可删除: {name}（包括 default / general-manager / project-lead）"
  ├─ 软删：move agents/{name}/ → agents/.trash/{name}-{timestamp}/
  ├─ 触发：ToolContextVersionService.Bump()
  └─ 返回 "已删除（移至 .trash）。如需恢复请在文件系统中将目录移回 agents/。"
```

会话兼容：删除后历史会话仍能打开。`AiChatHostedService` 在加载会话时按 AgentName 查询，找不到时降级显示"已删除的智能体"占位（不崩，且会话历史仍可阅读）。

> 注：D6 决策"新库不导入旧聊天"后，本次升级时新库聊天会话表为空，不存在历史会话需要兼容。但**未来用户在新版本里删除自己创建的 Agent**，老会话兼容仍要做——这是常态化能力，不是迁移期能力。

---

## 六、阶段拆分

每个阶段独立可上线、独立可回滚。

### 阶段 0.0：清理废弃代码（前置无风险）

**目的**：先把已废弃的 P2 动态智能体体系清理掉，减少后续改造干扰。

**前置调研**（动手删之前必做）：

把以下问题查清楚再删，避免引用悬空：

1. `BuildDynamicSubAgent` 的所有调用点（Magentic Manager 路径）
2. `create_subagent` / `dynamic_agent_*` 工具注册在哪个 ToolProvider
3. 删除 `MagenticDynamicCreationInstructionsProvider` 后，Magentic 模式还剩哪些 InstructionsProvider，是否需要兜底
4. `GetAvailableToolNames` 是否仅被 P2 残留代码使用，有无其他引用

**清理项**：

| 清理对象 | 文件位置 |
|---------|--------|
| `BuildDynamicSubAgent` 方法 | [AIAgentFactory.cs:999-1055](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) |
| `GetAvailableToolNames` | [AIAgentFactory.cs:968-989](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) |
| `AssembleDynamicToolProviders` | [AIAgentFactory.cs:1064-1121](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) |
| `MagenticDynamicCreationInstructionsProvider` | `Src/Netor.Cortana.AI/Providers/MagenticDynamicCreationInstructionsProvider.cs`（已删除） |
| 嵌入资源 `Magentic.DynamicCreation.md` | `Src/Netor.Cortana.AI/Resources/Prompts/Magentic.DynamicCreation.md`（已删除） |
| `create_subagent` / `dynamic_agent_*` 工具注册 | 找 `AIFunctionFactory.Create` 相关位置 |
| `Plugins/runner_data/memory-supply-probe/Program.cs:10` 旧默认 Agent 常量 | 已随阶段 0.5 后续收尾改为文件化 Agent 名 `default` |

**验证清单**：
- 聊天模式正常（普通对话、@智能体提及）
- 工作模式正常（启动任务、dispatch 子智能体）
- 会议模式正常（克隆 Agent、Manager 协调）
- 编译通过、单测通过

### 阶段 0.1：基础设施

**新增**：

- [AgentFileService.cs](../../../Src/Netor.Cortana.Entitys/Services/AgentFileService.cs)（CRUD 文件版）
- `AgentFileIndex`（启动扫描 + 内存索引）
- `FileSystemWatcher` 监听 manifest 变更
- `AgentManifest` 序列化类（YamlDotNet 或类似）
- `AgentManifestValidator`（schema 校验）
- 工厂目录约定（`agents-seed/`，从应用资源拷贝）

**验证清单**：
- 单测覆盖 schema 序列化/反序列化
- 单测覆盖目录扫描
- 单测覆盖 watcher 触发
- 工厂目录拷贝到用户目录的逻辑正确

**执行记录（2026-06-07）**：

- 已新增文件版 Agent 基础设施：
  - `AgentManifest` / `AgentManifestKinds`
  - `AgentManifestSerializer`
  - `AgentManifestValidator`
  - `AgentFileRecord`
  - `AgentFileIndex`
  - `AgentFileWatcher`
  - `AgentFileService`
  - `AgentSeedFromFactoryService`
- 已在 UI 启动流程注册并初始化文件版 Agent 基础设施：
  - 启动时先执行 `AgentSeedFromFactoryService.EnsureSeedAgents()`
  - 再执行 `AgentFileService.RebuildIndex()`
  - 最后启动 `AgentFileWatcher.Start(agentFileService.AgentsDirectory)`
- 已新增系统设置 `Agent.DefaultName`，默认值为 `default`。
- 已新增 `agents-seed/` 内置资源目录，并预装 3 个 `builtin/system` Agent：
  - `default`
  - `general-manager`
  - `project-lead`
- 已配置 `Netor.Cortana.UI.csproj` 将 `agents-seed\**\*` 复制到输出目录和发布目录。
- `AgentSeedFromFactoryService` 当前优先读取应用输出目录 `AppContext.BaseDirectory/agents-seed`；若不存在，则回退到用户数据目录 `agents-seed`，用于测试与兼容。
- `FileSystemWatcher` 已按 200ms 去抖实现；`AgentFileWatcher.AgentChanged` 已在 UI 启动流程接入 `ToolContextVersionService.Bump()`，Agent 文件变更会推进工具上下文版本。
- 验证结果：
  - `dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false`：通过，7/7
  - `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false`：通过，0 警告，0 错误
  - 已确认 `Src/Netor.Cortana.UI/bin/Debug/net10.0-windows/agents-seed/` 下生成 3 个内置 Agent 目录及各自的 `manifest.yaml` / `prompt.md`

### 阶段 0.2：UI 适配

**改造**：

- [AgentSettingsPage.axaml](../../../Src/Netor.Cortana.UI/Views/Settings/AgentSettingsPage.axaml)
  - 删除：Provider/Model 下拉、Temperature/TopP 等滑块、MaxTokens/MaxHistoryMessages
  - 保留：Name、Description（必填红星）、Instructions、Avatar、Enabled、AllowWorkflowMemory、Plugin/MCP 绑定多选
  - **builtin/system Agent 的删除按钮禁用**（default / general-manager / project-lead 不可删，但 manifest / prompt 可改）
  - **bound_plugins / bound_mcp 多选展示**：与全局已安装插件 / MCP 列表 join；已卸载或已禁用的项显示为灰色 + "（未安装）"标记。用户保存时可选择保留或剔除（默认保留，避免插件临时下线时误清；运行时 AssembleToolProviders 找不到的项会被 PluginLoader 自动忽略，不影响 Agent 工作）
- 新增：系统设置页"默认智能体"下拉框（绑定 `Agent.DefaultName`）
- AgentSettingsPage 保存逻辑改为调 `AgentFileService.Save()`，保存前校验 name / display_name / description 三个必填非空（不容错）

**验证清单**：

- 创建 Agent 走文件路径
- 编辑 Agent 走文件路径
- 删除 Agent 走文件路径（builtin/system 的删除按钮置灰）
- 切换默认 Agent 写系统设置
- UI 字段简化后不影响现有 Agent 显示
- 必填字段缺失时保存失败 + 红框提示
- bound_plugins 列表中含已卸载插件时显示灰色"未安装"标记，不影响保存

**执行记录（2026-06-07）**：

- 已将 `AgentSettingsPage` 改为文件版 Agent 管理：
  - 列表读取 `AgentFileService.GetAll()`
  - 新建/编辑保存 `manifest.yaml` + `prompt.md`
  - 删除走 `AgentFileService.SoftDelete()`
  - 表单字段改为 `name` / `display_name` / `description` / `prompt.md` / `avatar` / `enabled` / `allow_workflow_memory`
  - 头像选择在保存时复制到 Agent 目录内 `assets/`，manifest 写入相对 manifest 所在目录的路径，避免工作区资源目录与 Agent 包资源混用
  - 删除旧 Provider/Model、Temperature、TopP、Penalty、MaxTokens、MaxHistoryMessages UI
- 已实现 `builtin/system` 限制：
  - 删除按钮置灰
  - 启用开关置灰，保存时强制保持 enabled=true
- 已实现默认 Agent 设置：
  - Agent 设置页“设为默认”写入 `Agent.DefaultName`
  - 系统设置页 `Agent.DefaultName` 使用 Agent 下拉框展示
- 已实现 `bound_plugins` / `bound_mcp` 多选：
  - 从已加载插件、全局插件状态、MCP 配置列表构建选择项
  - 已卸载或已禁用项显示灰色标记，不阻止保存
- 已将 `AgentService` 改为文件版兼容壳：
  - 旧兼容 API `GetById()` 仍保留为 `GetByName()` 代理；UI/工具/VM 已迁移到 manifest name 语义的 `GetByName()` / `FindByNameOrDisplayName()`
  - 内部转换为 `AgentFileService` + `SystemSettingsService.Agent.DefaultName`
  - `AgentEntity.Id` 暂映射为 `manifest.name`，`AgentEntity.Name` 暂映射为 `manifest.display_name`
- 验证结果：
  - `dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false`：通过，7/7
  - `dotnet test Tests/Netor.Cortana.Store/Netor.Cortana.Store.Tests.csproj -p:UseSharedCompilation=false`：通过，28/28
  - `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false`：通过，0 警告，0 错误

### 阶段 0.3：AIAgentFactory 重构

> 取消原 0.3"会话表 AgentName 双写"——D6 修订版 v3 改为"新数据库 madorin.db + 选择性导入旧库"，新库直接按目标 schema 建表（`AgentName` 字段，无 `AgentId`），**不需要双写灰度**。

**改造**：

- [AIAgentFactory.Build](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs#L100)
  - 不再读 `Agent.DefaultProviderId/ModelId/Temperature/...`
  - Provider/Model 由调用方传入（保持现有签名）
  - ChatOptions 用模型级默认或会话级覆盖
- `AssembleToolProviders` 数据来源：`manifest.bound_plugins/bound_mcp` 替代 `Agent.EnabledPluginIds/EnabledMcpServerIds`
- `ResolveSubAgentProviderAndModel` 简化：不再读 Agent 的 Provider/Model 字段
- `BuildSubAgent` 同步简化

**验证清单**：

- 聊天模式构建 Agent 正常
- 工作模式构建主/子 Agent 正常
- 会议模式构建 Agent 正常
- @智能体提及构建子 Agent 正常
- 工具注入数量与改造前一致（按工厂目录预装的 3 个 Agent 验证）
- ChatOptions 参数正确（来自模型级默认）

**执行记录（2026-06-07）**：

- 已收敛 `AIAgentFactory` 的 Agent 配置读取：
  - `ResolveSubAgentProviderAndModel()` 保留兼容签名，但文件版 Agent 不再保存 Provider/Model，统一跟随调用方传入的 Provider/Model
  - Workflow Manager 仍支持 UI 显式 override Provider/Model；未指定时回退到调用方传入值
  - 工具绑定读取优先从 `AgentFileService.GetByName(agent.Id).Manifest.BoundPlugins/BoundMcp` 获取，缺失时回退兼容 DTO 字段
  - 插件/MCP 绑定匹配改为大小写不敏感
- 已收敛 ChatOptions：
  - `AiProviderDriverBase.CreateCommonOptions()` 不再从 Agent 读取 Temperature / TopP / MaxTokens
  - `CreateOpenAiCompatibleOptions()` 不再从 Agent 读取 FrequencyPenalty / PresencePenalty
  - 仍保留 `agent.Instructions` 作为系统提示词来源
- 已清理工作/会议模式克隆路径中的旧 Agent 模型参数复制：
  - `GeneralManagerAgentBuilder.CloneWithGmPromptAsync()`
  - `MeetingAgentBuilder.BuildHostAgentAsync()`
  - `MeetingAgentBuilder.BuildSelectorAgent()`
  - `MeetingAgentBuilder.CloneParticipantForMeeting()`
- 验证结果：
  - `dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false`：通过，7/7
  - `dotnet test Tests/Netor.Cortana.MeetingMode.Tests/Netor.Cortana.MeetingMode.Tests.csproj -p:UseSharedCompilation=false`：通过，72/72
  - `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false`：通过，0 警告，0 错误

### 阶段 0.4：AgentService 切换为文件代理 + 实体重命名

**改造**：

- `AgentService` 改为薄壳代理 `AgentFileService`：内部从文件加载 manifest，包装成 `AgentEntity` DTO 返回。`AgentEntity.cs` 保留作 DTO（去 SQL 注解、去废弃字段、字段重命名）
- 6 个 LLM 工具改写为文件操作（含新增 sys_delete_agent）
- `AgentSeedService` → `AgentSeedFromFactoryService`：不写库，从 `agents-seed/` 拷贝到 `agents/`
- [MeetingAgentBuilder.cs](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs) 改读文件版 manifest（克隆时不再复制 IsDefault）
- **实体重命名两处**（同次完成）：
  - [ChatSessionEntity.cs:46](../../../Src/Netor.Cortana.Entitys/Entities/ChatSessionEntity.cs) `AgentId` → `AgentName`
  - [WorkTaskEntity.cs:62](../../../Src/Netor.Cortana.Entitys/Entities/WorkTaskEntity.cs) `AgentId` → `AgentName`

**实体重命名涉及的代码改动清单**（每个实体相同模式）：

- 实体类属性名：`AgentId` → `AgentName`
- 服务类 SQL 字符串：`InsertSql` / `UpdateSql` / `ReadEntity` 中所有 `AgentId` 引用
- 调用方写入路径：
  - ChatSession：[ChatHistoryDataProvider.cs:140](../../../Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs)（消息回灌）、[MeetingSessionService.cs:89-96](../../../Src/Netor.Cortana.Entitys/Services/MeetingSessionService.cs)（会议会话创建）、聊天会话创建路径
  - WorkTask：工作模式任务创建/更新路径
- 调用方读取路径：所有 `session.AgentId` / `task.AgentId` 读取处
- AIAgentFactory 中按 ID 查 Agent 的代码改为按 Name 查（`agentService.GetById(session.AgentId)` → `agentService.GetByName(session.AgentName)`）

> 注：现 [WorkTaskEntity.cs:62](../../../Src/Netor.Cortana.Entitys/Entities/WorkTaskEntity.cs) 实际字段名是 `AgentId`，**不是早期文档误写的 `ManagerAgentId`**（`ManagerAgentId/ManagerAgentName` 仅在 OrchestrationTask 表中）。新库 `madorin.db` 的 WorkTasks / ChatSessions 表直接按目标 schema 建立——只有 `AgentName` 字段，没有 `AgentId`。

**AgentEntity DTO 化的字段改动**：

- 删除：`DefaultProviderId` / `DefaultModelId` / `Temperature` / `MaxTokens` / `TopP` / `FrequencyPenalty` / `PresencePenalty` / `MaxHistoryMessages` / `Image` / `IsDefault`
- 重命名：`EnabledPluginIds` → `BoundPlugins`，`EnabledMcpServerIds` → `BoundMcp`
- 保留：`Id`（运行期承载 manifest name） / `Name` / `Description` / `Instructions` / `Avatar` / `IsEnabled` / `SortOrder` / `AllowWorkflowMemory`
- 去掉所有 `[MaxLength]` 等持久化注解（DTO 不再入库）

**验证清单**：

- 6 个 LLM 工具全部走文件路径
- 启动时工厂目录拷贝逻辑正确（已存在不覆盖）
- 会议模式 Manager 创建正常
- 现有 Agent 列表显示正确（来自文件）
- ChatSession 写入 / 读取路径用新字段 `AgentName`
- WorkTask 写入 / 读取路径用新字段 `AgentName`
- AIAgentFactory 按 Name 查 Agent，与 Memory 插件等下游消费 AgentEntity DTO 的链路正常

**执行记录（2026-06-08）**：

- 已完成 `ChatSessionEntity.AgentId` → `AgentName`：
  - 会话创建 / 更新路径改为写入 `ChatSessions.AgentName`
  - 会话读取路径（主窗口、历史面板、ChatHistoryDataProvider）改为读取 `AgentName`
  - 会议支撑 ChatSession 创建路径改为写入 `AgentName`
  - PluginBus conversation 历史导出从 `s.AgentId` 改为 `s.AgentName`
- 已完成 `WorkTaskEntity.AgentId` → `AgentName`：
  - WorkTask Insert / Read / Bind 改为 `AgentName`
  - 工作模式新任务创建、续跑、标题生成、handoff 创建任务路径改为使用 `AgentName`
  - `WorkflowExecutor` / `WorkTaskTitleService` 按 `AgentName` 调用 `AgentService.GetByName`
- `CortanaDbContext` 已对齐 0.4 过渡态：
  - 新建 `ChatSessions` / `WorkTasks` 表使用 `AgentName`
  - 旧库增量添加 `AgentName` 列，并从旧 `AgentId` 回填
  - `ChatMessages` 保留消息层 `AgentId` / `AgentName`，不参与本次实体重命名
- 已同步会议模式测试中的 `ChatSessions` 原始 SQL 插入列名。
- 验证结果：
  - `dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false`：通过，0 警告，0 错误
  - `dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false`：通过，7/7
  - `dotnet test Tests/Netor.Cortana.MeetingMode.Tests/Netor.Cortana.MeetingMode.Tests.csproj -p:UseSharedCompilation=false`：通过，72/72（期间出现一次 DLL 文件占用重试警告，最终通过）
  - `dotnet test Tests/Netor.Cortana.Networks.Tests/Netor.Cortana.Networks.Tests.csproj -p:UseSharedCompilation=false`：通过，26/26

### 阶段 0.5：新数据库 `madorin.db` + 选择性导入旧库

依据 D6 修订版 v3：**新建 `madorin.db` + 从旧 `cortana.db` 选择性导入**。不做表内迁移，不做 ALTER/DROP，旧库永远保留不动。

#### 5.1 数据库路径

新库与旧库放同一目录：[CortanaDbContext.cs:881-892](../../../Src/Netor.Cortana.Entitys/CortanaDbContext.cs) 的 `GetDefaultDbPath()` 优先用 `Environment.ProcessPath` 目录，fallback 到 `AppContext.BaseDirectory`：

- **正式安装**：exe 所在目录（如 `C:\Program Files\Madorin\madorin.db`）
- **调试运行**：bin 输出目录（如 `e:\Netor.me\Cortana\Build\bin\...\madorin.db`）

改造 `GetDefaultDbPath()` 把 `cortana.db` 改成 `madorin.db`，旧 `cortana.db` 与新 `madorin.db` 在同一目录共存。

#### 5.2 新库 schema：直接按目标版本建

`CortanaDbContext.EnsureTables()` 内 SQL 直接更新为目标 schema，**不创建已废弃字段**：

```sql
-- 不再创建 Agents 表（数据已搬到文件系统）

CREATE TABLE IF NOT EXISTS ChatSessions (
    Id TEXT PRIMARY KEY,
    ...
    AgentName TEXT NOT NULL DEFAULT '',   -- 替代旧 AgentId
    ...
);

CREATE TABLE IF NOT EXISTS WorkTasks (
    Id TEXT PRIMARY KEY,
    ...
    AgentName TEXT NULL,                   -- 替代旧 AgentId
    ...
);
```

`AgentEntity.cs` **保留作纯 DTO**（不再持久化、去掉 SQL 注解）——用于 `AgentService` 兼容层把 manifest 转换成 `AgentEntity` 供 Memory 插件等下游消费者使用。

#### 5.3 启动时是否触发导入：以 `Db.SchemaVersion` 系统设置版本号判定

**判定逻辑**：用 SystemSettings 写一个版本号 key，比业务表行数判定更可靠（避免用户清空 AiProviders 后被重复导入）。

```text
SystemSettings key:
  Id:           Db.SchemaVersion
  Group:        Db
  Value:        2                          # 当前版本号；后续若再做迁移依次递增
  DefaultValue: 0
  ValueType:    int

检测顺序（每次启动都跑，但只有"首次升级"才真正导入）：
  1. 打开 madorin.db 并 EnsureTables（按目标 schema 幂等建表）
  2. 调用 SystemSettingsService.EnsureSeedData()
     ├─ 若 SystemSettings 表里没有 Db.SchemaVersion → 写入默认值 0
     └─ 若已有 → 不动
  3. 读取 currentVersion = SystemSettings.GetValue("Db.SchemaVersion")
  4. 若 currentVersion >= 2：导入已完成，跳过
  5. 若 currentVersion < 2 且同目录 cortana.db 存在：触发导入流程（见 5.4）
  6. 若 currentVersion < 2 且 cortana.db 不存在：全新安装，直接把版本号置为 2
```

**为什么用版本号而不是 AiProviders 行数**：

- AiProviders 行数只能判定"用户是否配过厂商"，无法识别"用户清空表后重启"的场景
- 版本号是显式语义，未来再做任何迁移（V2→V3）只需递增版本号 + 写新 migrate 分支
- SystemSettings 的同步 SetValue/GetValue API（[SystemSettingsService.cs:84-121](../../../Src/Netor.Cortana.Entitys/Services/SystemSettingsService.cs)）天然支持

#### 5.4 选择性导入流程

只导入与"用户配置"相关的表：

| 旧库表 | 是否导入 | 说明 |
|--------|---------|------|
| `AiProviders` | ✅ 全量 | 厂商配置 |
| `AiModels` | ✅ 全量 | 模型配置 |
| `GlobalPlugins` | ✅ 全量 | 全局插件启用状态 |
| `McpServers` | ✅ 全量 | MCP Server 配置 |
| `SystemSettings` | ✅ 过滤导入 | 排除 `Id LIKE 'Agent.%'`、`Id LIKE 'Db.%'` 的 key（前者已废弃，后者是新版控制位，不应被旧值覆盖）|
| `Workspaces` | ✅ 全量 | 工作区设置 |
| `Agents` | ❌ | 改用文件版，工厂目录拷贝 |
| `ChatSessions / ChatMessages` | ❌ | 旧聊天和会议数据**都在 ChatSessions 表里**（按 Categorize 字段区分），整表不导入即一并丢弃 |
| `CompactionSegments` | ❌ | 聊天压缩段，与 ChatSessions 一同丢弃 |
| `MeetingCompactionSegments` | ❌ | 会议压缩段，与 ChatSessions（Categorize=会议）一同丢弃 |
| `WorkTasks` 及其工作流相关表 | ❌ | 旧任务不带过来 |
| `OrchestrationTask`（如存在）| ❌ | 旧编排任务不带过来 |

> **注**：长期记忆数据**不在 `cortana.db` 中**——它由 `Cortana.Plugins.Memory` 插件维护独立 `memory.db`，主库切换不涉及它。

**实施说明（连接与事务模式）**：

- **两连接独立模式**：用两个独立 SqliteConnection——一个只读连旧库，一个读写连新库。**不使用 ATTACH DATABASE**，因为新库的 EnsureTables、SystemSettingsService.EnsureSeedData 等已在新库连接上工作，引入 ATTACH 反而复杂
- **事务范围**：只包裹新库连接，旧库只读不在事务内
- **失败语义**：异常 → 新库事务 ROLLBACK，旧库不动；下次启动 `Db.SchemaVersion` 仍 < 2，会再次触发重试
- **写入策略**：所有 INSERT 用 `INSERT OR REPLACE` —— 因为 SystemSettingsService.EnsureSeedData() 在 EnsureTables 之后会预先写入若干默认值（如 `Voice.* / Compaction.* / Memory.ModelId / WebSocket.Port`），用 `INSERT` 会主键冲突；用 `INSERT OR REPLACE` 让"用户已配置过的旧值"覆盖"新装机的默认种子值"，符合"保留用户配置"的语义

伪代码：

```text
function import_from_legacy(legacy_path, new_db_conn):
    legacy_conn = open_readonly(legacy_path)
    
    BEGIN TRANSACTION on new_db_conn
    try:
        copy_replace(legacy_conn, new_db_conn, 'AiProviders')
        copy_replace(legacy_conn, new_db_conn, 'AiModels')
        copy_replace(legacy_conn, new_db_conn, 'GlobalPlugins')
        copy_replace(legacy_conn, new_db_conn, 'McpServers')
        copy_replace(legacy_conn, new_db_conn, 'Workspaces')
        copy_filtered_replace(legacy_conn, new_db_conn, 'SystemSettings',
            where="Id NOT LIKE 'Agent.%' AND Id NOT LIKE 'Db.%'")
        new_db_conn.SystemSettings.Set('Agent.DefaultName', 'default')
        new_db_conn.SystemSettings.Set('Db.SchemaVersion', '2')
        COMMIT
    except Exception as e:
        ROLLBACK
        log_error(e)
        raise  # 启动失败让用户看到错误
    finally:
        legacy_conn.close()
    
    # 旧库不动、不改名、不删除
    
    
function copy_replace(legacy_conn, new_db_conn, table):
    rows = legacy_conn.Query("SELECT * FROM " + table)
    for row in rows:
        new_db_conn.Execute("INSERT OR REPLACE INTO " + table + " VALUES (...)", row)
```

#### 5.5 旧代码清理（同次升级内完成）

- 删除 [AgentService.cs](../../../Src/Netor.Cortana.Entitys/Services/AgentService.cs) 内部 SQL 实现，**仅保留薄壳**调用 `AgentFileService` 并把 manifest 包装成 `AgentEntity` DTO
- 删除 `Src/Netor.Cortana.Entitys/Services/AgentSeedService.cs`（被 `AgentSeedFromFactoryService` 替代）
- 删除 `EnsureTables()` 中创建 `Agents` 表的 SQL
- [AgentEntity.cs](../../../Src/Netor.Cortana.Entitys/Entities/AgentEntity.cs) **保留**作纯 DTO，但去掉 `[MaxLength]` 等持久化注解；删除已不需要的字段（DefaultProviderId、DefaultModelId、Temperature、MaxTokens、TopP、FrequencyPenalty、PresencePenalty、MaxHistoryMessages、Image、IsDefault），保留 Id（运行期承载 manifest name）、Name、Description、Instructions、Avatar、Enabled、SortOrder、AllowWorkflowMemory、EnabledPluginIds（重命名为 BoundPlugins）、EnabledMcpServerIds（重命名为 BoundMcp）
- 删除 `ChatSessions.AgentId` 字段相关的读写代码（仅保留 `AgentName`）
- 删除 `WorkTasks.AgentId` 字段相关的读写代码（仅保留 `AgentName`）
- App 启动逻辑里 `AgentSeedService.EnsureSeedData()` 调用换成 `AgentSeedFromFactoryService.EnsureSeedData()`

#### 5.6 commit 拆分建议

阶段 0.5 内部按以下顺序拆分 commit，每个 commit 独立可编译可运行：

1. **commit 1：路径切换** — `GetDefaultDbPath()` 改返回 `madorin.db`；EnsureTables() 中 ChatSessions/WorkTasks 字段改为 AgentName；删除 Agents 表 SQL。此时新库可建可用，但旧用户启动会发现"配置全没了"——这是中间态
2. **commit 2：版本号注册** — SystemSettingsService.EnsureSeedData 注册 `Db.SchemaVersion=0` 默认值
3. **commit 3：导入流程** — 新增 `LegacyDbImporter` 类（实施 5.4 流程），App 启动时调用；导入完成写入 `Db.SchemaVersion=2`。此时旧用户升级体验完整
4. **commit 4：DTO 重构** — AgentEntity 去持久化注解、删除废弃字段、字段重命名（EnabledPluginIds→BoundPlugins 等）；AgentService 改为薄壳代理 AgentFileService
5. **commit 5：删除老类** — 删除 AgentSeedService.cs，App 启动改调 AgentSeedFromFactoryService

每个 commit 单独可回滚。前 3 个 commit 完成后即可发预览版给少量用户验证导入；4-5 commit 是清理性质，风险更低。

#### 5.7 验证清单

- 全新安装：启动后 `madorin.db` 存在、`AiProviders` 为空、agents/ 目录有 3 个内置 Agent（kind 都是 builtin/system）、`Agent.DefaultName=default`、`Db.SchemaVersion=2`
- 升级用户：`madorin.db` 与 `cortana.db` 同目录共存；新库 `AiProviders` 等表数据与旧库一致；新库无聊天/任务/会议数据；旧 `cortana.db` 文件未被修改；新库 `Db.SchemaVersion=2`
- 升级用户重启：第二次启动 `Db.SchemaVersion` 已为 2 → 跳过导入流程
- 旧库为空（用户从未配过厂商）的升级：跳过导入，直接置版本号为 2
- 导入失败回滚：手动制造冲突（如往新库预先写入数据）后启动，验证事务回滚 + 版本号未推进 + 下次启动重试
- 用户删除 default：sys_delete_agent 调用拒绝（kind=builtin/system）；UI 删除按钮在 default 上禁用
- INSERT OR REPLACE 语义：用户旧 SystemSettings 中的 `Compaction.SegmentSize=128` 应覆盖种子默认值
- **调试模式测试导入流程**：开发者把已有 cortana.db 复制到新版本 bin 目录，启动验证导入正确（Debug 与 Release 用不同 bin，需手动构造测试数据）

**执行记录（2026-06-08）**：

- 已将默认数据库路径从 `cortana.db` 切换为 `madorin.db`，并新增 `CortanaDbContext.GetLegacyDbPath()` 用于定位同目录旧库。
- 已移除新库 `Agents` 表创建、`IX_Agents_*` 索引与旧 `Agents` 迁移残留；`ChatSessions` / `WorkTasks` 直接按目标 schema 使用 `AgentName`。
- 已新增 `LegacyDbImporter`：
  - 注册并使用 `Db.SchemaVersion=2` 作为导入完成标记；
  - 旧库不存在时直接推进版本号；
  - 旧库存在时只读打开 `cortana.db`，在新库事务内选择性导入 `AiProviders`、`AiModels`、`GlobalPlugins`、`McpServers`、`Workspaces`；
  - `SystemSettings` 过滤 `Agent.*` / `Db.*`，避免旧 Agent 默认值和旧控制位污染新库；
  - 使用源/目标列交集 + `INSERT OR REPLACE`，兼容旧表缺少新列的情况，并让旧用户配置覆盖种子默认值；
  - 导入完成写入 `Agent.DefaultName=default` 与 `Db.SchemaVersion=2`。
- 已删除 `AgentSeedService.cs`，App 启动流程改为先执行 `LegacyDbImporter.EnsureImported()`，再执行 `AgentSeedFromFactoryService.EnsureSeedAgents()`。
- 已将 `AgentEntity` 清理为文件版运行期 DTO：
  - 删除 SQL 持久化注解和 Agent 级模型/采样/历史字段：`DefaultProviderId`、`DefaultModelId`、`Temperature`、`MaxTokens`、`TopP`、`FrequencyPenalty`、`PresencePenalty`、`MaxHistoryMessages`、`Image`；
  - `EnabledPluginIds` 重命名为 `BoundPlugins`，`EnabledMcpServerIds` 重命名为 `BoundMcp`；
  - `IsDefault` 兼容桥已移除，默认 Agent 统一通过 `AgentService.GetDefaultOrFirst()` / `Agent.DefaultName` 解析；
  - `Id` 继续作为运行期 DTO 标识保留，承载 manifest name，供 AIAgentFactory、@智能体提及、handoff 与插件绑定链路使用。
- 已同步调用点：
  - `AgentService` 文件代理读写 `BoundPlugins` / `BoundMcp`；
  - `AIAgentFactory`、会议模式、工作模式、插件绑定 UI、会议测试改用新字段；
  - Chat/Work 输入框不再读取 Agent 级默认 Provider/Model，切换 Agent 时保留当前厂商/模型选择；
  - Proxy / 插件模型能力调用不再通过 `AgentEntity` 携带采样参数。
- 已新增 `LegacyDbImporterTests` 覆盖旧库选择性导入、`SystemSettings` 过滤、列交集复制和版本号推进。

验证结果：

```text
dotnet build Netor.Cortana.slnx -p:UseSharedCompilation=false
  通过：0 警告，0 错误

dotnet test Tests/Netor.Cortana.AI.Tests/Netor.Cortana.AI.Tests.csproj -p:UseSharedCompilation=false
  通过：8/8

dotnet test Tests/Netor.Cortana.MeetingMode.Tests/Netor.Cortana.MeetingMode.Tests.csproj -p:UseSharedCompilation=false
  通过：72/72

dotnet test Tests/Netor.Cortana.Networks.Tests/Netor.Cortana.Networks.Tests.csproj -p:UseSharedCompilation=false
  通过：26/26
```

**后续收尾记录（2026-06-08）**：

- 已将 `Plugins/runner_data/memory-supply-probe/Program.cs` 的旧默认 Agent 常量改为文件化 Agent 名 `default`。
- 已同步阶段 0.0 与阶段 0 文档说明，避免继续引用旧默认 Agent。
- 验证结果：

```text
dotnet build Plugins/runner_data/memory-supply-probe/memory-supply-probe.csproj -p:UseSharedCompilation=false
  通过：0 警告，0 错误

rg "agent\.default\.xiaoyue|@xiaoyue" Plugins Src Tests
  源代码、插件工具与测试范围零结果
```

---

## 七、工具上下文版本号机制（已解决）

### 7.1 现状

[智能体创建时机与工具上下文刷新问题讨论.md](../智能体创建时机与工具上下文刷新问题讨论.md)
描述的悬案：插件/MCP/Agent 文件变化后，已构建的 Agent 不会自动获得新工具。

### 7.2 改造

新增 `ToolContextVersionService`：

```csharp
public class ToolContextVersionService
{
    private long _version;
    public long Current => Interlocked.Read(ref _version);
    public long Bump() => Interlocked.Increment(ref _version);
}
```

触发递增的事件：

- 插件安装/卸载/启用/禁用
- MCP Server 增删改启停
- Agent manifest 变更（FileSystemWatcher 触发）
- Agent prompt 变更（FileSystemWatcher 触发）
- 用户技能目录 / 工作区技能目录变更（`SkillDirectoryWatcherService` 触发）

`AiChatHostedService` 维护 `_builtToolContextVersion`：

- 发送消息前对比当前版本
- 不一致则重建 Agent
- 不打断当前流式响应

### 7.3 关于 Bump 频次

实际场景：

- 用户安装一个新插件 → 1 次 Bump
- 同时启用 → 又 1 次 Bump
- 自动安装相关 MCP Server → 又 1 次 Bump
- 短时间内可能触发 3-5 次

**多次 Bump 不会立即重建多次**——重建是**懒重建**：只有用户下次发送消息时，`AiChatHostedService` 才比对版本号决定是否重建。所以无论 Bump 几次，重建只发生一次。

Bump 频次只影响"是否需要重建"的判定，不影响重建次数；不需要为 Bump 加节流。

### 7.4 验证清单

- 插件安装后下一轮对话生效
- MCP 启用后下一轮对话生效
- 编辑 Agent manifest 后下一轮对话生效
- 流式响应中变更不被打断
- 短时间多次 Bump 仅触发一次重建（用消息发送日志确认）

---

## 八、启动初始化伪代码

D6 修订版 v3 的启动流程：**新数据库 `madorin.db` + 选择性导入旧 `cortana.db`**。

```text
const TARGET_SCHEMA_VERSION = 2

function on_startup():
    db_dir = get_exe_directory()
    new_db_path = db_dir + "/madorin.db"
    legacy_db_path = db_dir + "/cortana.db"
    
    # 1. 打开/创建新库，幂等建表（按目标 schema）
    new_db = open_or_create(new_db_path)
    new_db.EnsureTables()
    
    # 2. SystemSettings 种子（必须先于版本号读取，确保 Db.SchemaVersion key 存在默认值 0）
    SystemSettingsService.EnsureSeedData(new_db)
    # 该方法注册的 key 中包含：
    #   Db.SchemaVersion = 0    （DefaultValue=0；首次启动时写入）
    #   Agent.DefaultName = default
    #   Voice.* / Compaction.* / Memory.ModelId / WebSocket.Port / ...
    
    # 3. 读版本号判定
    current_version = int(new_db.SystemSettings.GetValue("Db.SchemaVersion") or "0")
    
    if current_version < TARGET_SCHEMA_VERSION:
        if exists(legacy_db_path):
            legacy_db = open_readonly(legacy_db_path)
            try:
                import_from_legacy(legacy_db, new_db)
            finally:
                legacy_db.close()
            # 旧库不动、不改名、不删除
        else:
            # 全新安装，无旧库，直接置版本号
            new_db.SystemSettings.SetValue("Db.SchemaVersion", str(TARGET_SCHEMA_VERSION))
    
    # 4. 工厂目录拷贝（独立于数据库导入）
    ensure_factory_seeds()


function import_from_legacy(legacy_db, new_db):
    BEGIN TRANSACTION on new_db
    try:
        copy_replace(legacy_db, new_db, 'AiProviders')
        copy_replace(legacy_db, new_db, 'AiModels')
        copy_replace(legacy_db, new_db, 'GlobalPlugins')
        copy_replace(legacy_db, new_db, 'McpServers')
        copy_replace(legacy_db, new_db, 'Workspaces')
        # SystemSettings 过滤：跳过 Agent.* / Db.* 开头的 key
        copy_filtered_replace(legacy_db, new_db, 'SystemSettings',
            where="Id NOT LIKE 'Agent.%' AND Id NOT LIKE 'Db.%'")
        # 推进版本号（在事务内）
        new_db.SystemSettings.SetValue("Db.SchemaVersion", str(TARGET_SCHEMA_VERSION))
        COMMIT
    except Exception as e:
        ROLLBACK
        log_error(e)
        raise  # 启动失败，让用户看到错误信息；下次启动版本号仍 < TARGET 会重试


function copy_replace(legacy_db, new_db, table):
    # 用 INSERT OR REPLACE：避免与 EnsureSeedData 预先写入的默认值主键冲突
    # 语义：用户旧值覆盖种子默认值（用户已配置过的优先于全新安装的默认）
    rows = legacy_db.Query("SELECT * FROM " + table)
    for row in rows:
        new_db.Execute("INSERT OR REPLACE INTO " + table + " VALUES (...)", row)


function ensure_factory_seeds():
    # 从应用资源目录拷贝 agents-seed/ 到用户级 agents/
    # 已存在的同名 Agent 不覆盖（用户可能已修改）
    user_agents_dir = get_user_agents_dir()
    if not exists(user_agents_dir):
        mkdir(user_agents_dir)
    
    for seed in app_resources/agents-seed/:
        target = user_agents_dir + "/" + seed.name
        if not exists(target):
            copy_directory(seed → target)
```

**关键点**：

- **新库总是建立**——`EnsureTables()` 幂等执行；判定导入用 `Db.SchemaVersion` 系统设置版本号
- **旧库永不动**——只读打开，导入完成不改名不删除；用户可手动查看历史数据
- **版本号显式语义**——未来再做迁移（V2→V3）只需递增 TARGET 常量 + 写新分支，不必再拼凑业务行数判定
- **失败可重试**——事务回滚后版本号未推进，下次启动会再次尝试
- **导入用 INSERT OR REPLACE**——SystemSettingsService.EnsureSeedData 已写入种子默认值，旧值覆盖种子是符合用户期望的语义
- **工厂目录拷贝独立于数据库导入**——即使用户手动删除了内置 Agent 目录，下次启动会从 agents-seed 补回（这正好和"builtin/system 不可删"配套——文件层补回 + sys_delete_agent 拒绝删，双重保护）

---

## 九、阶段间依赖关系

```text
0.0 清理废弃代码（无依赖，先做）
    ↓
0.1 基础设施（AgentFileService / Index / Watcher / Manifest）
    ↓
0.2 UI 适配 ─────────┐
                     │（与 0.3 并行无依赖）
0.3 AIAgentFactory 重构
    ↓               ↓
    └────合并────→ 0.4 AgentService 切换为文件代理 + 6 个 LLM 工具改写
                    ↓
                  0.5 新数据库 madorin.db + 从旧 cortana.db 选择性导入
                       └─ 旧库 cortana.db 不动，永远保留作备份
```

> 整个迁移在一次升级内完成。不需要灰度、不需要双写、不需要事后清理 PR。

---

## 十、已拍板决策

§六–§九 的所有改造均依据本节决策展开。

### D1：工厂目录预置内容

`agents-seed/` 仅预装 **3 个 Agent**，全部 `builtin/system`：

| name | kind | 用途 | 可禁用 | 可删除 |
|---|---|---|---|---|
| `default` | builtin/system | 默认助手（原小月内容迁移到此 prompt.md） | ❌ | ❌ |
| `general-manager` | builtin/system | A 角色（总经理） | ❌ | ❌ |
| `project-lead` | builtin/system | B 角色（项目组长） | ❌ | ❌ |

**`default` 是 builtin/system，不可删但可改**——用户编辑 `default/manifest.yaml` 和 `default/prompt.md` 就能把它改成自己的助理（改 display_name / description / prompt）。需要新建独立助理时建一个 `kind: agent` 的新文件夹即可，不动 `default`。这样保证默认助手永远存在，避免删除后 fallback 链断裂。

C 类专员（novel-writer / dotnet-developer 等）**不预装**——由用户从应用市场下载或手动创建。理由：

- 应用包保持轻量
- C 类专员业务定制性强，预置反而干扰用户
- 应用市场已存在，分发链路完整

首次启动时，应用从内置资源（`agents-seed/`）拷贝到用户级 `agents/` 目录；已存在的同名 Agent**不覆盖**。

### D2：字段命名 `bound_plugins / bound_mcp`

manifest.yaml 中"绑定到该智能体的插件/MCP"字段：

```yaml
bound_plugins:
  - independent-station-ops
bound_mcp:
  - novel-toolkit-mcp
```

语义对照：

- "全局启用" → 全局插件管理里的开关，对所有 Agent 默认生效
- "绑定" → manifest 里的 `bound_*`，仅对该 Agent 生效

两个语义清晰区分。技能（Skills）暂不加 `bound_skills`，等技能体系真正绑定到 Agent 时再扩。

> 现状：[PluginInstallScope](../../../Src/Netor.Cortana.Plugin/PluginLoader.cs) 仅 `Global` 一个枚举值，**当前插件只有全局安装**——历史曾有"工作目录安装插件"的功能（早期老程序），现已移除。`bound_plugins` 列出的插件必须是**已全局安装**的。

### D3：FileSystemWatcher 去抖 200ms

watcher 触发后启动 200ms 计时器；窗口内新事件重置计时器；到期才执行重建：

- 重建 `AgentFileIndex` 中该 Agent 项
- 触发 `ToolContextVersionService.Bump()`

多文件同时变更各自独立去抖。批量目录操作（如 git pull 一次性更新多个 Agent）依赖去抖窗口合并事件。

实现方式：`System.Reactive` 的 `Throttle` 或手写 `Timer`，按代码风格选。

### D4：Avatar 文件查找规则

| 维度 | 规则 |
|---|---|
| 路径解释 | 相对 manifest 所在目录（不允许绝对路径，避免分发污染） |
| 子目录 | 允许（`avatar: assets/avatar.png` 合法） |
| 支持扩展名 | png / jpg / jpeg / webp |
| 字段省略 | 用默认头像 |
| 找不到文件 | 回退默认头像 + 日志 warn，不抛错 |
| 大小限制 | 不强制；UI 上传时建议小于 1MB |

> **已落地边界**：AgentSettingsPage 已支持选择、清除和预览 `avatar`，保存时复制到当前 Agent 目录内并写入相对路径。MeetingBubbleBuilder 已支持从 `MeetingBubbleArgs.AvatarPath` 读取会议参会 Agent 头像；MeetingViewController 按 `speakerId` 解析 `manifest.avatar`，找不到、越界或加载失败时回退内置默认头像。

### D5：sys_delete_agent 不走 HITL，依赖 ask_user 提示词约束

依据调研结论：现有 HITL（[HitlTools.cs:25-66](../../../Src/Netor.Cortana.AI/Hitl/HitlTools.cs)）是非阻塞模式——工具调用立即返回提示文字，用户回复异步到达。这与"删除前需用户确认"的同步语义不匹配，无法用 HITL 实现"等用户确认才删"。

**改用提示词约束 A 在调用顺序上加 ask_user 前置**：

> 在调用 `sys_delete_agent` 之前，**必须先用 `ask_user` 工具询问用户**："确认删除智能体 X 吗？"
> 用户明确回答确认后，再调 `sys_delete_agent`。
> 用户拒绝或不确定时，不调用 `sys_delete_agent`。

`sys_delete_agent` 工具本身行为：

- **直接执行删除**（软删到 `agents/.trash/<name>-<timestamp>/`）
- **保护内置**：`kind: builtin/system` 的 Agent 一律拒绝删除（包括 `default` / `general-manager` / `project-lead`）
- **会话兼容**：删除后历史会话仍能打开（按 AgentName 反查找不到时降级显示"已删除的智能体"占位）

### D6：新数据库 `madorin.db` + 选择性导入旧库（修订版 v3）

软件未正式投放、无大规模历史用户，但表内迁移仍然太脏。**改用新数据库 `madorin.db` + 从旧 `cortana.db` 选择性导入**——新库按目标 schema 建立，零兼容包袱；旧库永远保留不动。

**升级时的判定与导入流程**：

1. 新库 `madorin.db` 与旧库 `cortana.db` 在同一目录共存（exe 所在目录）
2. 应用启动时新库总会被创建并 `EnsureTables()` 幂等建表（按目标 schema）
3. SystemSettingsService.EnsureSeedData 注册版本号 key `Db.SchemaVersion`（默认值 0）
4. 用版本号判定是否需要导入：
   - 新库 `Db.SchemaVersion >= 2` → 跳过导入
   - 新库 `Db.SchemaVersion < 2` 且同目录旧 cortana.db 存在 → 触发选择性导入
   - 新库 `Db.SchemaVersion < 2` 且旧库不存在 → 全新安装，直接置版本号为 2

**为什么用 `Db.SchemaVersion` 版本号而不是业务表行数**：

- 用业务表行数（如 AiProviders）判定有边角失败：用户清空 AiProviders 后重启会被错误地"重新导入"
- 版本号是显式语义，未来再做任何迁移（V2→V3）只需递增 TARGET 常量 + 写新分支
- SystemSettings 的同步 SetValue/GetValue API（[SystemSettingsService.cs:84-121](../../../Src/Netor.Cortana.Entitys/Services/SystemSettingsService.cs)）天然支持

**导入的表**：

- `AiProviders`（全量）
- `AiModels`（全量）
- `GlobalPlugins`（全量）
- `McpServers`（全量）
- `Workspaces`（全量）
- `SystemSettings`（过滤导入：排除 `Id LIKE 'Agent.%'` 和 `Id LIKE 'Db.%'` 的 key——前者已废弃，后者是新版控制位不应被旧值覆盖）

**写入策略**：所有 INSERT 用 `INSERT OR REPLACE`——SystemSettingsService.EnsureSeedData 会预先写入若干默认值（如 `Voice.* / Compaction.* / Memory.ModelId / WebSocket.Port`），用 `INSERT` 会主键冲突；`INSERT OR REPLACE` 让用户旧值覆盖种子默认值，符合"保留用户配置"的语义。

**不导入的表**（新库这部分为空）：

- `Agents`（改用文件版 + 工厂目录拷贝）
- `ChatSessions` / `ChatMessages`（旧聊天和会议数据都在 ChatSessions 表里，按 Categorize 字段区分，整表不导入即一并丢弃）
- `CompactionSegments` / `MeetingCompactionSegments`（聊天/会议压缩段）
- `WorkTasks` / `WorkExecutionLogs` / `WorkflowCheckpoints` / `WorkPendingInputs` / `WorkBackgroundJobs`（工作流相关）
- `OrchestrationTask`（如果存在）

> 注：会议数据**不在独立的 MeetingSessions 表中**——调研确认 [MeetingSessionService.cs:89-96](../../../Src/Netor.Cortana.Entitys/Services/MeetingSessionService.cs) 实际写入的是 `ChatSessions` 表，靠 `Categorize` 字段区分聊天/会议。所以"不导入 ChatSessions"已经把会议数据一同排除。

**长期记忆数据不在主库**——它由 `Cortana.Plugins.Memory` 插件维护独立 `memory.db`（[MemoryDatabaseOptions.cs](../../../Plugins/Src/Cortana.Plugins.Memory/Storage/MemoryDatabaseOptions.cs)），主库切换不涉及它。

**老库与老代码的处理**：

- 旧 `cortana.db` **不动、不改名、不删除**——永远保留作为静默备份；用户若需要可手动打开查看
- 同次升级清理：`AgentService.cs` 改为薄壳代理 / `AgentSeedService.cs` 删除 / `AgentEntity.cs` 保留作 DTO（去 SQL 注解、删废弃字段）
- 不创建 `Agents` 表；新库 `ChatSessions` / `WorkTasks` 直接按目标 schema（用 `AgentName` 字段，不要 `AgentId`）建立

**优势**：

- 新 schema 零兼容包袱：CREATE TABLE 即按目标版本，不需要 ALTER/DROP
- 失败可重试：导入事务回滚后版本号未推进，下次启动会再次尝试
- 版本号显式语义：未来再做迁移直接加新分支
- 出问题可对照：旧库静默保留，调试时可对比新旧两边数据
- 心智简单：开发者只看新库 schema，不用纠结历史字段

---

## 十一、影响范围矩阵

| 模块 | 风险等级 | 改造点 |
|------|---------|--------|
| AgentEntity / AgentService | 高 | 切换为文件代理；阶段 0.5 删除老类 |
| AIAgentFactory | 高 | 工具源、Provider/Model 解析 |
| 数据库路径 / 连接串 | 中 | `cortana.db` → `madorin.db`，旧库静默保留 |
| ChatSession 持久化 | 低 | 新库直接按目标 schema 建表（仅 AgentName，无 AgentId）|
| WorkTask 持久化 | 低 | 新库直接按目标 schema 建表（仅 AgentName，无 AgentId）；现 [WorkTaskEntity.cs](../../../Src/Netor.Cortana.Entitys/Entities/WorkTaskEntity.cs) 字段名是 `AgentId`，**不是早期文档误写的 `ManagerAgentId`**（`ManagerAgentId/ManagerAgentName` 仅 OrchestrationTask 表用）|
| AgentSettingsPage UI | 中 | 字段大量删减；description 必填 |
| 6 个 LLM 工具 | 中 | 改写文件操作 + 新增 sys_delete_agent（不走 HITL，依赖 ask_user 提示词约束）|
| AgentSeedService | 中 | 改为工厂目录拷贝（`AgentSeedFromFactoryService`）|
| MeetingAgentBuilder | 低 | 读文件版 manifest；克隆时不再复制 IsDefault |
| @智能体提及 (InputAreaView) | 低 | 已按 name 索引 |
| 上下文压缩 (CompactionSegments) | 低 | 不依赖 Agent 字段；新库不导入旧压缩段 |
| 长期记忆 AllowWorkflowMemory | 低 | manifest 字段保留；长期记忆数据在独立 `memory.db` 不受主库切换影响 |
| 工具上下文版本号 | 已解决 | 见 §七 |
| P2 旧动态智能体 | 顺手清理 | 见 §六.0.0 |
| Avatar UI 渲染 | 已落地 | AgentSettingsPage 已可编辑/预览 manifest.avatar；MeetingBubbleBuilder 已消费会议参会 Agent 的 manifest.avatar，失败回退默认头像 |
| 旧聊天/任务/会议数据 | 不导入 | 新库这部分为空；旧 cortana.db 保留作备份 |

---

## 十二、完成记录

- [x] 逐项讨论 §十 的 D1–D6 决策（D6 修订版 v3：新库 + 选择性导入）
- [x] 完成阶段 0.0（清理废弃代码 + 残留代码地图前置调研）
- [x] 完成阶段 0.1（基础设施 + AgentSeedFromFactoryService）
- [x] 完成阶段 0.2（UI 适配，含 description 必填）
- [x] 完成阶段 0.3（AIAgentFactory 重构）
- [x] 完成阶段 0.4（AgentService 文件代理 + 6 个 LLM 工具）
- [x] 完成阶段 0.5（新数据库 madorin.db + 从旧 cortana.db 选择性导入）
- [x] 对齐 Avatar 路径语义：设置页头像保存到 Agent 目录内 `assets/`，manifest 校验禁止绝对路径和 `..` 逃逸路径；会议气泡已消费 manifest.avatar 并保留默认头像回退
- [x] 收口 Agent 默认值兼容桥：移除 `AgentEntity.IsDefault`，默认 Agent 统一通过 `AgentService.GetDefaultOrFirst()` / `Agent.DefaultName` 解析；`AgentEntity.Id` 保留为 manifest name 运行期标识
- [x] 收口 AgentService API 命名：外部调用从 `GetById()` 迁移到 `GetByName()`；计划步骤角色匹配使用 `FindByNameOrDisplayName()` 保留显示名兼容
- [x] 收口用户可选 Agent 列表：`general-manager` / `project-lead` 仅作为工作模式内部 A/B 角色使用，不出现在普通 Agent 选择、会议参会者选择、默认 Agent 下拉和 LLM 配置工具列表中
- [x] 补默认头像种子：`default`（董秘小月）使用 `Assets/headers/1_100.png` 作为 `assets/avatar.png`；已存在用户 Agent 且 avatar 为空时由 `AgentSeedFromFactoryService` 回填，不覆盖用户自定义头像
- [x] 文档稳定后从 `Docs/未来版本策划/工作模式三层架构重构/` 移到 `Docs/已完成功能规划/`
