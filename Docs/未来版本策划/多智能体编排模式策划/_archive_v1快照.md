# 多智能体编排模式策划方案

## 1. 背景与目标

当前项目已经基于 `Microsoft.Agents.AI` 和 `Microsoft.Agents.AI.Workflows` 构建 AI 对话能力，并且在 `AIAgentFactory.BuildWithSubAgents(...)` 中实现了“主智能体 + @提及子智能体工具”的轻量子智能体模式。

本方案目标是在不大面积重构现有 Cortana 架构的前提下，逐步引入以下编排模式：

1. **Magentic 模式**：策划规划、分派任务、执行返回、检测总结，作为复杂任务的主编排模式。
2. **GroupChat 讨论模式**：让多个角色智能体围绕问题开会、讨论方案、形成结论。
3. **Handoff/备用智能体模式**：用于专家转接或备用智能体接管，但不作为复杂任务拆解主方案。

并引入与主模式正交的"执行策略"维度：

- **Concurrent 执行策略**：作为 Magentic 分派阶段或 GroupChat 内部环节的并行执行器，把多个独立子任务交给多个智能体同时处理。它不是与 Magentic / GroupChat 平级的主模式，而是它们内部的执行策略选项之一（详见 §5.2）。

设计原则：

- 优先复用现有 `AIAgentFactory`、`AiChatHostedService`、`AIContextProvider`、`AgentEntity`、`IAiOutputChannel`。
- 不直接侵入各厂商 `IAiProviderDriver`。
- 不在第一阶段修改 UI 消息结构、WebSocket 协议和数据库表。
- 先支持“UI 只展示最终单一回复”，后续再支持“多智能体过程可视化”。
- 第一阶段不强行过滤工具调用链历史，优先保证历史恢复和工具协议完整。
- 避免一次性引入架构级大重构。

---

## 2. 当前代码现状

### 2.1 当前 AI 对话入口

核心入口是：

- `Src/Netor.Cortana.AI/AiChatHostedService.cs`
- `Src/Netor.Cortana.Entitys/Interfaces/IAiChatEngine.cs`

当前输入链路：

```text
UI / WebSocket / Voice
  ↓
IAiChatEngine.SendMessageAsync(...)
  ↓
AiChatHostedService.SendMessageAsync(...)
  ↓
AIAgentFactory.Build(...) 或 BuildWithSubAgents(...)
  ↓
_agent.RunStreamingAsync(...)
  ↓
IAiOutputChannel 广播 token
  ↓
ChatHistoryDataProvider 持久化历史
  ├── CompactAndReplaceAsync(...) 触发段落压缩并回写（StoreChatHistoryAsync 末尾）
  └── 新会话首次对话：异步 GenerateAndUpdateTitleAsync(...) 生成 AI 摘要标题
```

需要注意：`ChatHistoryDataProvider.StoreChatHistoryAsync(...)` 不仅写入消息，还会触发"压缩段落"和"AI 摘要标题"两个副作用。多智能体编排引入后，这两个副作用是否要禁用或延后由 Orchestration 层显式触发，需要在阶段 2 明确。

`IAiChatEngine` 当前接口已经支持：

```csharp
Task SendMessageAsync(
    string userInput,
    CancellationToken cancellationToken,
    List<AttachmentInfo>? attachments = null,
    List<AgentMention>? mentions = null);
```

这说明已有 `mentions` 参数可以作为第一阶段多智能体选择入口，不需要立即扩展输入协议。

---

### 2.2 当前智能体构建中心

核心文件：

- `Src/Netor.Cortana.AI/AIAgentFactory.cs`

当前职责：

- 根据 `AgentEntity`、`AiProviderEntity`、`AiModelEntity` 构建 `AIAgent`。
- 解析 Provider Driver。
- 创建 `IChatClient`。
- 包装 `TokenTrackingChatClient`。
- 注入 `AIContextProvider`。
- 注入技能、长期记忆、项目设置、插件、MCP 工具。
- 配置 `ChatHistoryDataProvider`。
- 根据 `@智能体` 构建子智能体工具。

当前已有方法：

```csharp
public AIAgent Build(AgentEntity agent, AiProviderEntity provider, AiModelEntity? model)
```

```csharp
public AIAgent BuildWithSubAgents(
    AgentEntity mainAgent,
    AiProviderEntity mainProvider,
    AiModelEntity mainModel,
    List<AgentMention> mentions,
    AiProviderService providerService,
    AiModelService modelService)
```

`BuildWithSubAgents(...)` 当前已经做了：

```text
主智能体
  ↓
判断主模型是否支持 FunctionCall（InteractionCapabilities.FunctionCall）
  ├── 不支持 → 跳过所有工具/插件/MCP/子智能体注入，仅写日志，无任何用户提示
  └── 支持
        ↓
      将被 @ 提及的子智能体构建为轻量 AIAgent
        ↓
      subAgent.AsAIFunction(...)
        ↓
      SubAgentContextProvider 注入为工具
        ↓
      主智能体通过 function call 调用子智能体
```

这说明当前项目已经具备“轻量子智能体”雏形。

**关键边界提示**：当前实现中如果主模型未启用 `InteractionCapabilities.FunctionCall`，所有 @ 子智能体会被**静默丢弃**（仅 `logger.LogInformation` 记录），用户层面感知不到任何区别。多智能体编排所有阶段方案的前置条件是"主模型支持 FunctionCall"，否则需要在 §6.4 的失败降级路径中明确提示用户、或退回单 Agent 普通对话模式，不能假装编排已生效。

---

### 2.3 当前子智能体能力边界

核心文件：

- `Src/Netor.Cortana.AI/Providers/SubAgentContextProvider.cs`

当前 `SubAgentContextProvider` 非常轻量，只负责把子智能体函数注入为工具：

```csharp
internal sealed class SubAgentContextProvider(IReadOnlyList<AIFunction> agentFunctions) : AIContextProvider
{
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        return new ValueTask<AIContext>(new AIContext
        {
            Tools = agentFunctions
        });
    }
}
```

当前 `BuildSubAgent(...)` 的注释明确说明：

```text
构建子智能体（轻量）：仅携带 instructions + plugins + MCP，不带历史/memory/skills。
```

这对第一阶段是优点：改动小、风险小。  
但对 Magentic / Concurrent / GroupChat 完整落地是限制：子智能体暂时没有独立历史、独立记忆、独立会话状态。

更准确地说，当前轻量子智能体也不会注入以下主 Agent 上下文能力：

- `AgentSkillsProvider` 技能目录。
- `ProjectSettingsProvider` 项目设置上下文。
- `LongMemoryContextProvider` 长期记忆上下文。
- `ChatHistoryDataProvider` 历史记录 Provider。
- `TokenTrackingChatClient` token 统计包装器。

因此第一阶段的子智能体更接近“带自身 instructions、插件和 MCP 的一次性专家工具”，而不是完整独立会话 Agent。

需要特别注意：当前“工具能力”和“技能能力”的管理粒度不同。

- 插件工具已经支持**全局插件**和**智能体绑定插件**两种来源。
- 全局插件需要同时满足两个条件才会注入：
  1. 插件的安装范围是 `PluginInstallScope.Global`（即来自全局插件目录）。
  2. 该插件 ID 出现在 `GlobalPluginService.GetEnabledPluginIds()` 启用列表中。
  两个条件缺一不可，仅启用未安装到全局目录的插件不会生效。
- 智能体绑定插件通过 `AgentEntity.EnabledPluginIds` 控制，仅绑定的智能体可用；同一插件已通过全局通道注入时会自动去重（`injectedPluginIds`）。
- MCP 服务通过 `AgentEntity.EnabledMcpServerIds` 绑定到具体智能体。
- 公共文件读取、常用宿主工具等能力可以作为全局插件或宿主内置公共工具提供。
- 特殊能力应通过智能体绑定插件或 MCP 只开放给特定智能体。
- 技能目录当前通过 `AgentSkillsProvider(skillDirs)` 注入（该类由 `Microsoft.Agents.AI` 1.3.0 NuGet 包提供，不是项目自有类型），主智能体统一加载用户技能目录和工作区技能目录；当前没有按 `AgentEntity` 绑定技能的机制。

因此，后续多智能体方案不需要重复设计一套完整的“工具归属模型”，应优先复用现有全局插件、智能体插件绑定和 MCP 绑定机制。真正缺口在于：**技能目前是全体主智能体共享加载，暂时没有可靠方案限制某个技能只给某个智能体使用。**

---

### 2.4 当前历史与记忆约束

核心文件：

- `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs`
- `Src/Netor.Cortana.AI/Memory/LongMemoryContextProvider.cs`
- `Src/Netor.Cortana.AI/Providers/TokenTrackingChatClient.cs`

当前历史持久化以一次主 `AgentSession` 为中心，`AiChatHostedService` 在不同时机向 `Session.StateBag` 写入以下键：

| 键名 | 写入位置 | 用途 |
| --- | --- | --- |
| `sessionid` | `LoadOrCreateSessionAsync` / `NewSessionAsync` | 会话 ID，`ChatHistoryDataProvider`、`LongMemoryContextProvider` 都依赖它 |
| `providerid` / `providername` | `UpdateSessionSelectionState` | 当前 AI 提供商 ID / 名称 |
| `agentid` / `agentname` | `UpdateSessionSelectionState` | 当前主智能体 ID / 名称，`LongMemoryContextProvider` 依赖 |
| `modelid` / `modeldbid` | `UpdateSessionSelectionState` | 模型显示名（OpenAI model id） / 数据库主键 |
| `sessiontitle` | `AddOrUpdateSessionAsync` 内部 | 会话标题，`LongMemoryContextProvider` 依赖 |
| `turnid` / `traceid` | `SendMessageAsync` 每轮开头 | 单轮编号 / 跨服务追踪 ID |
| `usermessageid` | `SendMessageAsync` 每轮开头 | 用户消息预生成 ID |
| `currenttask` | `SendMessageAsync` 每轮开头 | 当前用户问题正文（截断 500 字内） |
| `assistantmessageid` | `SendMessageAsync` 写入 contents 后 | 助手消息预生成 ID |
| `isnewsession` | `ChatHistoryDataProvider.CreateNewSessionAsync` | 私有标记，触发"首轮 AI 摘要标题" |

`ChatHistoryDataProvider.StoreChatHistoryAsync(...)` 会从 `context.Agent` 和 `context.Session.StateBag` 中读取当前 Agent 与会话信息，然后保存消息。`LongMemoryContextProvider` 额外依赖 `agentname` / `sessiontitle` / `usermessageid` 三个键。

这意味着：

- 第一阶段 UI 可以只展示“最终单一回复”，但历史层不应简单承诺“只保存最终回复”。
- 当前 `BuildWithSubAgents(...)` 通过 `AsAIFunction(...)` 触发子智能体时，主 Agent 响应里可能包含 assistant tool_calls、tool result 和最终 assistant 消息；这些消息会随 `ResponseMessages` 被 `ChatHistoryDataProvider` 持久化。
- 如果强行过滤中间工具消息，可能破坏 OpenAI 兼容接口要求的 assistant tool_calls 与 tool result 邻接关系，影响历史恢复后的继续对话。
- 如果要保存每个子智能体独立发言，需要中到大改。
- 如果 Concurrent 多个 Agent 并发写同一个 Session，必须处理消息归属、排序和工具链完整性。
- 如果 GroupChat 要展示“谁说了什么”，需要消息层显式记录 `speakerAgentId`。

---

### 2.5 当前 Agent 配置模型

核心文件：

- `Src/Netor.Cortana.Entitys/Entities/AgentEntity.cs`

当前 `AgentEntity` 包含：

- `Name`
- `Instructions`
- `Description`
- `DefaultProviderId`
- `DefaultModelId`
- 模型参数
- `MaxHistoryMessages`
- `EnabledPluginIds`
- `EnabledMcpServerIds`

当前没有以下字段：

- 编排模式
- 成员智能体列表
- 最大轮次
- 是否允许并发
- 讨论终止条件
- Magentic 计划审批
- GroupChat 选人策略

因此不建议第一阶段直接改 `AgentEntity` 和数据库。更适合先使用运行时策略或配置文件承载。

其中工具绑定相关能力已经存在，不属于第一阶段新增重点：

```text
全局插件 GlobalPlugins
  ↓
所有智能体默认可用

AgentEntity.EnabledPluginIds
  ↓
指定插件只绑定到特定智能体

AgentEntity.EnabledMcpServerIds
  ↓
指定 MCP 服务只绑定到特定智能体
```

当前缺少的是“技能绑定到智能体”的配置模型，例如：

```text
AgentEntity.EnabledSkillIds / EnabledSkillPaths / DisabledSkillIds
```

但技能系统暂时不建议在第一阶段扩展，因为技能目录与 Agent Framework 的加载方式、技能发现规则、冲突处理和 UI 管理都需要重新设计。

---

### 2.6 当前实现需要统一的元数据约束

当前 `AIAgentFactory.Build(...)`、`BuildWithSubAgents(...)`、`BuildSubAgent(...)` 三条路径的 Agent 元数据设置并不完全一致，逐项对照如下：

| 构建路径 | Name | Id | Description | Instructions（来自 ChatOptions） |
| --- | --- | --- | --- | --- |
| `Build()` | ✅ `agent.Name` | ❌ 未设置 | ❌ 未设置 | ✅ `driver.BuildChatOptions` 中带 `agent.Instructions` |
| `BuildWithSubAgents()` | ✅ `mainAgent.Name` | ✅ `mainAgent.Id` | ❌ 未设置 | ✅ 同上 |
| `BuildSubAgent()` | ✅ `agent.Name` | ❌ 未设置 | ✅ `agent.Description` | ✅ 同上 |

可以看到三个路径在 Id 和 Description 上是"两两不同"的：`Build()` 缺 Id 和 Description、`BuildWithSubAgents()` 缺 Description、`BuildSubAgent()` 缺 Id；只有 Name 和 Instructions 三处一致。

这在现有单 Agent 对话中问题不大，因为消息归属主要依赖 `Session.StateBag`。  
但引入 Orchestration 层后，如果编排步骤依赖 `context.Agent.Id`、`context.Agent.Description` 或 `AIAgent.Id`，可能出现普通模式、@子智能体模式、子智能体模式之间的身份不一致：例如阶段 2 想根据 `context.Agent.Description` 让 Coordinator 自动选择候选子智能体时，`Build()` 路径下拿不到值。

因此在实施第一阶段前，建议先统一以下规则：

```text
Build(...)、BuildWithSubAgents(...)、BuildSubAgent(...)
  ↓
三个路径都显式设置 Id = agent.Id / Name = agent.Name / Description = agent.Description
  ↓
历史归属仍以主 Session.StateBag 为准
  ↓
子智能体真实执行归属由后续 OrchestrationStep / UsedAgentIds 记录
```

---

## 3. 修改量评估

### 3.1 小改动方案：增强现有子智能体工具模式

目标：快速验证“多个智能体参与处理问题”。

改动范围：

- `AIAgentFactory.cs`
- `SubAgentContextProvider.cs`
- `AiChatHostedService.cs` 少量分支

不改：

- `IAiChatEngine`
- 数据库
- UI 消息结构
- WebSocket 协议
- Provider Driver

模式：

```text
用户 @多个智能体
  ↓
主智能体构建为 Coordinator
  ↓
被 @ 的智能体作为工具函数注入
  ↓
主智能体按需要调用子智能体
  ↓
主智能体综合总结
  ↓
现有输出通道输出最终回复
```

优点：

- 修改量小。
- 与当前 `BuildWithSubAgents(...)` 完全顺路。
- 不需要 Agent Framework Workflow 编排运行时。
- 不破坏现有历史保存。

缺点：

- 不是严格 Magentic。
- 不是真正并行。
- 子智能体没有独立历史和长期记忆。
- 中间过程不可完整可视化。

评估：**小改动，不属于架构级修改。**

---

### 3.2 中改动方案：新增 Orchestration 层

目标：引入真实的 Magentic / GroupChat 编排能力（Concurrent 作为内部执行策略），但对外仍保持一次聊天回复。

新增目录建议：

```text
Src/Netor.Cortana.AI/Orchestration/
  AgentOrchestrationMode.cs
  AgentExecutionStrategy.cs
  AgentOrchestrationRequest.cs
  AgentOrchestrationResult.cs
  IAgentOrchestrator.cs
  AgentOrchestrator.cs
  AgentOrchestrationPlanner.cs
  AgentOrchestrationAggregator.cs
```

接入方式：

```text
AiChatHostedService.SendMessageAsync(...)
  ↓
判断是否启用编排
  ↓
普通模式：继续 _agent.RunStreamingAsync(...)
  ↓
编排模式：交给 IAgentOrchestrator
  ↓
IAgentOrchestrator 内部基于 Microsoft.Agents.AI.Workflows 构建：
  - Magentic：使用 Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder（具体 API 见阶段 3 spike 结论）
  - GroupChat：同上
  - Concurrent：作为 Magentic / GroupChat 内部执行环节，必要时调用
    Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.BuildConcurrent(...)
  ↓
返回最终汇总文本
  ↓
复用现有 IAiOutputChannel 输出
```

> 说明：上述 `AgentWorkflowBuilder.BuildConcurrent(...)` 命名空间路径为 `Microsoft.Agents.AI.Workflows` 包（项目已引用 1.3.0）。
> 该 API 在 1.3.0 的精确签名和 Builder 形态需要在阶段 3 接入前以 spike 形式确认，避免与后续 SDK 升级冲突。
> 如 spike 发现 SDK API 与文档不一致，请以 spike 结论为准，并在本文档对应章节更新。

优点：

- 编排逻辑从 `AiChatHostedService` 分离。
- 不污染 Provider Driver。
- 可以逐步支持 Concurrent 策略、GroupChat、Magentic。
- 仍然不需要马上改 UI 和 DB。

风险：

- 需要处理多个 Agent 的 session 隔离。
- 需要处理 `TokenTrackingChatClient` 当前工厂级状态问题。
- 需要明确中间步骤默认只保存在内存；如需入库，必须保证工具调用链和消息归属完整。
- `AIAgentFactory` 需要暴露构建成员 Agent 的公共方法。

评估：**中改动，但可以控制在 AI 项目内部，不必立刻做架构级大重构。**

---

### 3.3 大改动方案：完整多智能体过程可视化和持久化

目标：完整支持每个智能体的中间发言、并行结果、讨论过程、重规划记录、可恢复编排状态。

会影响：

- `ChatHistoryDataProvider.cs`
- `AgentEntity.cs`
- `CortanaDbContext.cs`
- `ChatMessageEntity` 相关结构
- UI 消息气泡
- WebSocket 事件协议
- `IAiChatEngine`
- `AiChatHostedService`
- 会话恢复逻辑

需要新增或调整：

```text
OrchestrationSession
OrchestrationTurn
OrchestrationStep
OrchestrationMessage
SpeakerAgentId
ParentStepId
BranchId
ExecutionMode
```

优点：

- 体验完整。
- 可追踪、可审计、可恢复。
- 支持真正的“智能体会议”。

缺点：

- 改动面大。
- 很容易牵动 UI、历史、网络协议、记忆系统。
- 不适合作为第一阶段。

评估：**大改动，属于架构级修改，应放到后续阶段。**

---

## 4. 推荐总体架构

推荐采用“轻量接入 + 独立编排层 + 渐进增强”的路线。

```text
AiChatHostedService
  ↓
AgentExecutionRouter
  ├── 普通单智能体模式
  ├── 当前 @子智能体工具模式
  └── 多智能体编排模式
        ↓
      IAgentOrchestrator
        ├── MagenticOrchestrationRunner    ← 内部可选 Sequential / Concurrent 策略
        ├── GroupChatOrchestrationRunner   ← 内部可选 Sequential / Concurrent 策略
        └── HandoffOrchestrationRunner
```

> Concurrent 不是独立 Runner，而是 Magentic / GroupChat Runner 内部的执行策略（与 §1 / §5.2 一致）。第一阶段不必一次性创建所有 Runner，可以先用一个 `AgentOrchestrator` 承载策略分支。

需要注意：`IAgentOrchestrator` 不应接管整个聊天生命周期。  
它只负责“多智能体执行与汇总”，不负责替代 `AiChatHostedService` 的通用对话职责。

建议职责边界如下：

| 职责 | 归属 |
| --- | --- |
| 用户消息保存 | `AiChatHostedService` |
| 会话状态、turnId、traceId、messageId 生成 | `AiChatHostedService` |
| `OnAiStarted` / `OnAiCompleted` | `AiChatHostedService` |
| `OnConversationTurnStarted` / `OnConversationTurnCompleted` | `AiChatHostedService` |
| `IAiOutputChannel.OnTokenAsync(...)` / `OnDoneAsync(...)` | `AiChatHostedService` |
| 取消、部分响应保存、错误分发 | `AiChatHostedService` |
| 多智能体计划、分派、执行、汇总 | `IAgentOrchestrator` |
| 中间步骤、参与 Agent、警告和失败信息收集 | `IAgentOrchestrator` |

`AgentOrchestrationResult` 各字段在阶段 2 的去向如下：

| 字段 | UI 是否可见 | 是否入库 | 默认去向 |
| --- | --- | --- | --- |
| `FinalText` | ✅ 流式输出 | ✅ 与现有 assistant 消息一致 | 经 `IAiOutputChannel` 广播；由 `ChatHistoryDataProvider` 持久化 |
| `Steps` | ❌ 默认不可见 | ❌ 默认不入库 | 日志（`logger.LogDebug` 级别）+ 内存（同一编排会话内可供 Aggregator 引用） |
| `UsedAgentIds` | ✅ 作为"参与列表"附在 FinalText 尾部或单独事件 | ❌ 默认不入库 | 日志 + 内存；阶段 4 起入编排消息表 |
| `Warnings` / `Failures` | ✅ 必要时附在 FinalText 尾部 | ❌ 默认不入库 | 日志（`logger.LogWarning`）+ 内存 |
| `TokenUsage` | ✅ 累计后再回写 `AIAgentFactory.TokenUsageChanged` | ❌ 默认不入库 | 由 Orchestrator 聚合后再上报 |

也就是说，阶段 2 的 Orchestration 层应优先替代“模型执行部分”，而不是绕开现有 UI 输出、事件广播、历史保存和取消处理链路；非 `FinalText` 字段在阶段 2 全部停留在内存和日志，不进 UI 也不进 DB，到阶段 4 再单独评估持久化。

---

## 5. 编排模式定位

### 5.1 Magentic：主模式

定位：复杂任务主编排模式。

适用场景：

- 用户提出复杂任务。
- 任务需要拆分为多个步骤。
- 需要动态选择子智能体。
- 需要检测是否完成。
- 需要失败后重新规划。
- 需要最后总结。

在 Cortana 中的目标流程：

```text
用户请求
  ↓
Magentic Manager / Planner
  ↓
生成任务计划
  ↓
选择一个或多个子智能体执行
  ↓
收集结果
  ↓
检查是否完成
  ↓
必要时重规划
  ↓
最终总结
```

建议初期约束：

- 最大轮次：3 到 5。
- 最大子任务数：3 到 6。
- 默认不允许子智能体写入长期记忆。
- UI 默认只展示最终回答；历史层是否保留工具链由历史保存策略决定。
- 中间过程先写日志，不进 UI。

---

### 5.2 Concurrent：Magentic / GroupChat 内部的并行执行策略

定位：不是独立主模式，而是 Magentic 或 GroupChat 内部的执行策略之一。本文档的 `AgentOrchestrationMode` 枚举（详见 §7）也据此不包含 `Concurrent`。

因此从模型上拆成两个相互正交的维度：

```text
OrchestrationMode
  None
  ToolDelegation
  Magentic
  GroupChat
  Handoff

ExecutionStrategy
  Sequential
  Concurrent
```

> 与 §1 一致：Concurrent 永远以 `ExecutionStrategy` 维度落地，不会回退到 `OrchestrationMode` 枚举。后续如果遇到"独立并行分析"需求，应作为 Magentic 退化形式（仅一个分派步骤、跳过重规划）实现，而不是新增主模式。

适用场景：

- 多个子任务之间没有依赖。
- 多个专家可以同时分析。
- 需要快速收集多个角度。

例子：

```text
Magentic Planner 拆出三个任务：
  1. 代码结构分析
  2. 风险评估
  3. 测试影响分析

Concurrent 执行策略并行执行：
  Explorer Agent
  Reviewer Agent
  Tester Agent

Aggregator 汇总结果。
```

第一阶段可先不用真正 `Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.BuildConcurrent(...)`，而是保留“多个子智能体工具”模式。第二阶段再接入 `Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.BuildConcurrent(...)`，具体 API 形态以阶段 3 spike 结论为准。

---

### 5.3 GroupChat：讨论模式

定位：智能体开会，用于方案讨论和评审。

适用场景：

- 用户明确要求“讨论一下方案”。
- 需要不同角色提出意见。
- 需要 Reviewer / Architect / Implementer / Tester 互相补充。
- 不是立即执行，而是先形成设计结论。

建议角色：

```text
主持智能体 / Moderator
架构智能体 / Architect
实现智能体 / Implementer
测试智能体 / Tester
风险审查智能体 / Reviewer
总结智能体 / Summarizer
```

由于第一阶段不允许扩展 `AgentEntity`（详见 §2.5），小组成员声明暂用以下临时方案，按优先级使用：

1. **运行时 @ 提及**：用户 @ 多个智能体且 Coordinator instructions 识别为 "GroupChat" 模式时，参与成员就是 @ 列表本身。第一阶段优先采用。
2. **工作区配置文件**：`workspace/.cortana/groupchat.json` 声明命名小组，例如：

   ```json
   {
     "groups": [
       {
         "id": "design-review",
         "moderator": "<agentId>",
         "members": ["<agentId>", "<agentId>"]
       }
     ]
   }
   ```

   阶段 2 由 `IAgentOrchestrator` 解析此文件加载小组成员；不依赖数据库表。
3. **AgentEntity 扩展**：阶段 4 过程可视化时，再评估在 `AgentEntity` 上加 `GroupMembershipIds` 或独立的 `OrchestrationGroupEntity`，配合 UI 管理。

初期约束：

- 最大轮次 3。
- 每个智能体只发一轮或两轮。
- 最终只输出主持智能体总结。
- 不直接暴露所有中间消息到数据库。
- 讨论阶段默认不启用高风险写操作工具（如发布、删除、网络出站），避免讨论本身意外修改外部状态；具体由 §6.2 工具边界规则承担。

---

### 5.4 Handoff：备用智能体 / 专家转接

定位：辅助模式，不作为复杂任务拆解主模式。

适用场景：

- 当前智能体发现任务属于某个专家领域。
- 主智能体能力不足，转给备用智能体。
- 客服式分流。
- 工具能力隔离。

不建议用于：

- 多任务拆解。
- 并行执行。
- 统一汇总。
- 多智能体会议。

---

## 6. 关键边界与约束

### 6.1 历史保存策略

第一阶段需要区分“UI 展示”和“历史持久化”：

```text
UI 展示：只展示最终 assistant 回复
历史持久化：允许保留主 Agent 的完整工具调用链
```

原因：当前子智能体通过 `subAgent.AsAIFunction(...)` 暴露为工具，主智能体调用子智能体时可能产生以下消息链：

```text
assistant tool_calls
tool result
assistant final
```

`ChatHistoryDataProvider` 当前会保存 `ResponseMessages`，并且已经针对工具调用链做了：

- **时间戳归一化**：以"同 session 已存在的最大 `CreatedTimestamp` + 1ms"为基准，按 `ResponseMessages` 原始顺序强制单调递增（见 `StoreChatHistoryAsync` 中 `baseTs + index` 写法），保证 OpenAI 协议要求的 `assistant(tool_calls)` 与 `tool` 邻接关系不被排序破坏。
- **孤立 tool 消息告警**：本轮入库后若发现 `Role=tool` 但同批没有任何带 `functionCall` / `toolCall` 的 assistant 消息，会输出 `工具链条警告`。
- **每条消息独立 ID**：不再把 assistant 消息强行统一为同一 messageId。

如果第一阶段为了“只保存最终回复”而过滤 tool_calls 或 tool result，可能破坏历史恢复后的工具调用协议。

需要警惕的多智能体并发风险：上述时间戳归一化策略基于"同 session 顺序写入"假设。如果未来 Concurrent 让多个 Agent 同时写同一个 session，多线程同时计算 `baseTs` 会冲突，必须先解决并发写入问题再放开真正并行。

因此第一阶段建议：

- 不修改数据库结构。
- 不强行过滤中间工具消息。
- UI 仍只显示最终 assistant token。
- 中间工具链可以作为历史上下文的一部分保留。
- 后续如果要“只保存最终汇总”，必须由 Orchestration 层显式生成可恢复的简化消息，而不是在 `ChatHistoryDataProvider` 里粗暴过滤。
- `StoreChatHistoryAsync` 末尾的"段落压缩 `CompactAndReplaceAsync`"和"AI 摘要标题 `GenerateAndUpdateTitleAsync`"两个副作用，在多智能体编排场景下应保持只对主会话触发一次，不能因子智能体调用工具而被多次触发。

### 6.2 子智能体工具与技能边界

当前系统已经具备工具分配能力，不需要在多智能体编排层重复设计一套完整的工具绑定模型。

现有工具来源分为：

```text
全局插件
  ↓
所有智能体默认可用

智能体绑定插件
  ↓
仅 `AgentEntity.EnabledPluginIds` 中绑定的智能体可用

智能体绑定 MCP
  ↓
仅 `AgentEntity.EnabledMcpServerIds` 中绑定的智能体可用
```

这意味着：

- 文件读取、通用上下文、基础宿主能力等公共工具，可以作为全局插件或宿主公共能力，让所有智能体使用。
- 编码、测试、打包、发布、网络访问、外部系统访问等特殊工具，应绑定到对应专家智能体。
- 主智能体作为 Planner / Supervisor / Coordinator，不必直接拥有所有执行工具。
- 子智能体可以拥有与自身角色匹配的工具能力，否则无法真正完成编码、测试、构建、发布等执行任务。

因此，第一阶段工具边界应调整为：

```text
工具能力边界 = 现有全局插件 + 当前智能体绑定插件 + 当前智能体绑定 MCP
```

多智能体编排层只需要记录和遵守这个边界，而不是重新裁剪工具集合。

仍需保留的安全约束是：

- 高风险工具应优先绑定到专用智能体，而不是设为全局工具。
- 主智能体调用子智能体时，应在最终结果或后续 Step 记录中标记实际执行者。
- 后续如引入任务级授权，可以在现有工具绑定结果之上再做临时收窄。
- 发布、删除、批量写入、外部网络、命令执行等高风险行为仍建议由宿主侧确认或审计。

技能边界与工具不同。当前技能通过 `AgentSkillsProvider(skillDirs)` 从用户技能目录和工作区技能目录统一注入，暂时没有按智能体绑定技能的机制。  
也就是说，当前主智能体能够看到同一套技能目录，无法可靠约束“某个技能只能由某个智能体使用”。

技能约束暂时建议作为后续课题，而不是第一阶段目标：

```text
阶段 1：接受技能全局可见现状，通过 instructions 引导智能体选择合适技能
阶段 2：评估技能元数据增加 applyToAgent / allowedAgentIds / deniedAgentIds
阶段 3：再考虑 UI 管理、技能冲突、技能继承与默认技能集
```

### 6.3 附件与多模态输入传递策略

当前 `AiChatHostedService` 会把附件转换为 `AIContent` 发给主 Agent：

- 图片类附件会拷贝到工作区资源目录并作为 `DataContent` 发送。
- 文档、代码、音视频等附件会以原始路径文本形式发送。

多智能体编排中需要明确附件归属：

```text
阶段 1：附件只直接进入主 Agent
阶段 1：子智能体是否看到附件，由主 Agent 在调用工具时自行摘要或转述
阶段 2：OrchestrationRequest 显式携带 Attachments
阶段 2：由 Orchestrator 决定哪些子任务可访问哪些附件
阶段 3+：再考虑文件锁、并发写入冲突、附件权限和资源引用追踪
```

子智能体工具的参数 schema 必须显式包含 `attachmentPaths` / `attachmentDescriptions` 数组，理由：

- 主 Agent 看到的图片是 `DataContent`（二进制），如果只让模型"自行转述"，子 Agent 完全拿不到原图；
- 主 Agent 看到的文档是文本路径（如 `[report.docx](C:\\...\\report.docx)`），如果转述时省略了路径，子 Agent 无法再读原文件；
- 子 Agent 自身的工具（如读文件、读图片）需要绝对路径作为输入参数才能工作。

推荐参数约定（第一阶段 Coordinator instructions 显式要求模型按此结构调用子智能体工具）：

```text
attachmentPaths       : 原始绝对路径数组（图片用工作区拷贝后的资源路径）
attachmentDescriptions: 主 Agent 对每个附件的简短描述（可选）
```

不建议第一阶段让多个子智能体直接并发访问和修改同一个附件文件。

### 6.4 取消、超时和失败降级

多智能体编排必须内置失败边界，不能只依赖模型自行停止。

建议基础规则：

- 每轮编排必须有 `MaxRounds`。
- 每个子任务必须有 `Timeout`。
- 每次执行最多允许固定数量的子任务。
- 用户取消时必须取消所有未完成子任务。
- 单个子智能体失败不应默认导致全局失败，除非该步骤被标记为 required。
- 最终回复需要说明未完成项、失败原因和降级结果。
- Magentic 重规划次数必须受限，避免无限规划。
- 主模型未启用 `InteractionCapabilities.FunctionCall` 时，不应静默丢弃 @ 子智能体；应至少在最终回复中给出一段提示，或退回单 Agent 模式。

**编排参数承载位置（在 §2.5 不允许第一阶段改 `AgentEntity` 前提下）**：

| 参数 | 阶段 1 承载位置 | 阶段 2 承载位置 | 阶段 4+ |
| --- | --- | --- | --- |
| `MaxRounds` / `MaxSubTasks` / `Timeout` | 编排服务内置常量（`AgentOrchestrator` 默认值） | `SystemSettingsService` 系统配置项（`orchestration.maxRounds` 等） | `AgentEntity` 或独立 `OrchestrationProfileEntity` |
| 默认编排模式 | Coordinator instructions 内置规则 | `SystemSettingsService` + `OrchestrationRequest.Mode` 显式覆盖 | `AgentEntity.DefaultOrchestrationMode` |
| GroupChat 小组成员 | `@` 提及列表 | `workspace/.cortana/groupchat.json` | `AgentEntity` 或 `OrchestrationGroupEntity` |
| 高风险工具白名单 | 复用现有插件 / MCP 绑定 | `SystemSettingsService` + 每次任务运行期收窄 | 独立授权模型 |

这样可以在不动 `AgentEntity` 和数据库结构的前提下，保留 §6.4 的所有"必须"约束。所有阈值默认值由 `AgentOrchestrator` 在第一阶段以常量形式提供，第二阶段开始迁移到 `SystemSettingsService`，避免硬编码遗留。

### 6.5 参与智能体记录

第一阶段可以通过 Coordinator instructions 要求模型在最终回复中简短说明参与智能体。  
但长期不能依赖模型自述，因为模型可能：

- 没调用某个子智能体却声称调用了。
- 调用了工具但漏写参与者。
- 工具失败后仍声称成功。

因此从阶段 2 开始，应由 Orchestration 层根据实际执行步骤生成：

```text
UsedAgentIds
Steps
Warnings
Failures
```

最终回复中的“参与智能体”应来自宿主侧记录，而不是完全依赖模型文本。

---

## 7. 最小落地方案

### 阶段 1：增强现有 @子智能体工具模式

目标：不引入架构级重构，先让“策划规划 + 子智能体调用 + 总结”跑起来。

建议改动：

0. 统一 `AIAgentFactory.Build(...)`、`BuildWithSubAgents(...)`、`BuildSubAgent(...)` 的 Agent 元数据设置：三个路径都显式设置 `Id = agent.Id`、`Name = agent.Name`、`Description = agent.Description`，对应 §2.6 的差异表。
1. 在 `AIAgentFactory.BuildWithSubAgents(...)` 中增强主智能体 instructions。
2. **新增独立的 `OrchestrationInstructionsProvider`**，返回 `AIContext.Instructions`，注入编排规则。  
   不再在 `SubAgentContextProvider` 上挂指令，避免该 Provider 同时承担"注入工具函数"与"注入指令"两个职责。两个 Provider 在同一个 `providers` 列表里并列注册。
3. 当 `mentions.Count > 1` 时，默认把当前主智能体视为 Coordinator。
4. Coordinator 规则包括：
   - 先制定简短计划。
   - 根据任务需要调用一个或多个子智能体工具。
   - 不要把子智能体输出直接拼接。
   - 必须做冲突检查。
   - 调用子智能体工具时必须按 §6.3 约定传入 `attachmentPaths` / `attachmentDescriptions`，否则子 Agent 拿不到原始附件。
   - 最终输出总结。
5. `AiChatHostedService.SendMessageAsync(...)` 不改变流式输出主链路，但需要保留轻量分流和元数据处理。
6. 第一阶段 UI 只展示最终回复，但历史层允许保留工具调用链，不强行过滤中间 tool 消息。
7. 第一阶段 `MaxRounds` / `MaxSubTasks` / `Timeout` 等阈值以 `AgentOrchestrator` 内部常量形式提供（对应 §6.4 的承载位置表）。

此阶段改动点：

```text
Src/Netor.Cortana.AI/AIAgentFactory.cs
Src/Netor.Cortana.AI/Providers/OrchestrationInstructionsProvider.cs   ← 新增
```

`Src/Netor.Cortana.AI/Providers/SubAgentContextProvider.cs` 保持单一职责（只注入工具函数），第一阶段不做修改。

预计改动量：**小**。

---

### 阶段 2：新增 AI 编排服务

目标：开始引入真正的编排模式，但对外仍保持单一最终回复。

建议新增：

```text
Src/Netor.Cortana.AI/Orchestration/AgentOrchestrationMode.cs
Src/Netor.Cortana.AI/Orchestration/AgentOrchestrationRequest.cs
Src/Netor.Cortana.AI/Orchestration/AgentOrchestrationResult.cs
Src/Netor.Cortana.AI/Orchestration/IAgentOrchestrator.cs
Src/Netor.Cortana.AI/Orchestration/AgentOrchestrator.cs
```

`AgentOrchestrationMode` 建议：

```text
None
ToolDelegation
Magentic
GroupChat
Handoff
```

`AgentExecutionStrategy` 建议：

```text
Sequential
Concurrent
```

`AgentOrchestrationRequest` 建议包含：

```text
UserInput
Attachments
MainAgent
MainProvider
MainModel
MentionedAgents
SessionId
TurnId
TraceId
Mode
```

`AgentOrchestrationResult` 建议包含：

```text
FinalText
Mode
Steps
UsedAgentIds
Warnings
TokenUsage
Failures
ShouldPersistIntermediateMessages
```

`AiChatHostedService` 中新增轻量分流：

```text
if (ShouldUseOrchestration(...))
    await orchestrator.RunAsync(...)
else
    await _agent.RunStreamingAsync(...)
```

注意：阶段 2 仍不要求 UI 显示中间过程。`IAgentOrchestrator` 只负责执行和汇总，不负责绕开 `AiChatHostedService` 的用户消息保存、事件发布、取消处理、输出通道完成通知和部分响应保存。

预计改动量：**中**。

---

### 阶段 3：接入 Agent Framework Workflows

当前项目已经引用：

```xml
<PackageReference Include="Microsoft.Agents.AI.Workflows" Version="1.3.0" />
```

因此无需新增核心包。

建议逐个接入：

1. `Concurrent`
   - 最容易落地。
  - 先以执行策略形式落地，而不是直接作为完整主模式。
  - 输入相同任务或拆分后的独立子任务，多 Agent 非流式并行分析。
   - Aggregator 生成最终总结。
  - 中间结果先只保存在内存，不并发写入主会话历史。

2. `GroupChat`
   - 用于讨论模式。
   - 需要限制轮次。
   - 初期只输出最终纪要。
  - 初期不允许高风险写操作工具，避免讨论阶段直接修改外部状态。

3. `Magentic`
   - 作为复杂任务主模式。
   - 最好在 Concurrent 和 GroupChat 跑通后再接。
   - 需要更严密的任务限制和失败处理。

预计改动量：**中**。

---

### 阶段 4：完整过程可视化与持久化

目标：UI 展示每个智能体的发言、计划、执行结果、汇总过程。

需要新增：

- 编排会话表。
- 编排步骤表。
- 编排消息表。
- WebSocket 编排事件。
- UI 多 Agent 消息气泡。
- 会话恢复逻辑。

预计改动量：**大**。建议后置。

---

## 8. 是否会产生架构级修改

结论：**取决于落地深度。**

### 不会产生架构级修改的范围

如果只做：

- 多 @智能体工具调用。
- 主智能体策划、调用、总结。
- UI 最终只展示一个回复。
- 不对中间过程做独立消息模型和可视化。
- 不改 UI 气泡和 WebSocket 协议。
- 不做真正并行执行。

则属于**小到中改动**，不会导致架构级修改。

---

### 会产生架构级修改的范围

如果要做：

- 真正 Magentic 长任务状态机。
- Concurrent 中每个 Agent 独立流式输出或并发写同一个 Session。
- GroupChat 每轮发言可视化。
- 每个子智能体独立历史、记忆、会话。
- 编排过程可恢复。
- WebSocket 对外暴露编排事件。
- UI 支持多智能体消息树。

则会涉及：

- AI 执行层。
- 历史持久化层。
- UI 消息模型。
- WebSocket 协议。
- Agent 配置模型。
- 记忆注入模型。

这属于**架构级修改**。

---

## 9. 推荐实施顺序

### Step 1：文档与模式约束

- 明确三种主模式（Magentic / GroupChat / Handoff）+ Concurrent 执行策略的定位。
- 明确第一阶段不做过程可视化。
- 明确子智能体工具边界复用现有全局插件、智能体绑定插件和 MCP 绑定机制。
- 明确技能暂时是全局技能目录注入，第一阶段不做智能体级技能绑定。
- 落地一份模式约束页到 `Docs/未来版本策划/多智能体编排模式策划/`，作为后续步骤的执行依据，避免“明确”停留在口头约定。

### Step 2：轻量 Coordinator 模式

基于当前 `BuildWithSubAgents(...)` 实现：

```text
主智能体 = Coordinator
@提及智能体 = 子智能体工具
最终回复 = Coordinator 总结
```

同时保留主 Agent 工具调用链历史，不承诺第一阶段只保存最终 assistant 文本。
"参与智能体"暂由 Coordinator instructions 要求模型在最终回复尾部简短说明，作为过渡方案。

### Step 3：新增 Orchestration 层壳子

先只封装现有逻辑，不改变行为：

```text
IAgentOrchestrator
AgentOrchestrator
AgentOrchestrationRequest
AgentOrchestrationResult
```

**切换点（关键）**：从 Step 3 完成开始，"参与智能体来源"切换为宿主侧 `AgentOrchestrationResult.UsedAgentIds`，不再依赖 Step 2 的模型自述。  
最终回复 UI 文本仍由 Coordinator 模型生成，但底部的"参与列表"应由宿主侧记录补充或覆盖。

### Step 4：接入 Concurrent 执行策略

先支持多个智能体非流式并行分析，中间结果只保存在内存，再由主智能体或 Aggregator 汇总。Concurrent 作为 Magentic / GroupChat Runner 内部的执行策略选项之一（与 §4 / §5.2 一致），不新增独立 Runner。

**中间消息存储策略切换点**：

| 阶段 | 中间消息去向 | 入口 |
| --- | --- | --- |
| Step 2 | 主 Agent 工具调用链入库（`ChatHistoryDataProvider` 自动捕获） | `BuildWithSubAgents` 工具模式 |
| Step 3 | 与 Step 2 一致，仅搬壳子不改语义 | `IAgentOrchestrator` 内部仍走 `BuildWithSubAgents` 路径 |
| **Step 4 起** | 切换为 `Microsoft.Agents.AI.Workflows.AgentWorkflowBuilder.BuildConcurrent(...)` 执行路径，多 Agent 中间结果**只保存在内存**，不并发写主会话历史；最终汇总文本走原有 `IAiOutputChannel` | `IAgentOrchestrator` 内部分支判断 |

切换原因：阶段 4 一旦改用 `Microsoft.Agents.AI.Workflows` 真正并行运行多 Agent，多线程同时写同一 `Session` 会和 §6.1 描述的"基于时间戳归一化"策略冲突，必须将中间消息从入库转为内存暂存。

### Step 5：GroupChat Runner

支持讨论模式，先只输出会议纪要。
小组成员声明优先级：运行时 @ 提及 → `workspace/.cortana/groupchat.json` → 阶段 4 再考虑 `AgentEntity` 扩展（详见 §5.3）。

### Step 6：Magentic Runner

支持复杂任务：

```text
计划 → 分派 → 执行 → 检查 → 重规划 → 总结
```

### Step 7：过程可视化与持久化

最后再修改 UI、WebSocket、DB。

---

## 10. 风险与规避

### 风险 1：`AIAgentFactory.ChatClient` 是工厂级状态

当前：

```csharp
public TokenTrackingChatClient? ChatClient { get; private set; }
```

且工厂还把 `_lastInputTokens` / `_maxContextTokens` 抽到字段层，每次 `CreateTrackingClient` 都会 `Interlocked.Exchange` 覆盖。这意味着 token 覆盖问题分两种严重程度：

| 场景 | 当前是否会发生 | 影响 |
| --- | --- | --- |
| **串行多 Agent**（切换提供商/模型/@子智能体导致 ChatClient 重建） | 已经发生 | 上一个 Agent 的 token 用量会被新 Agent 覆盖；多智能体编排串联调用时 token 不可累加，UI 进度条只反映最近一次调用 |
| **真正并行多 Agent** | 阶段 4 之前不会发生 | 多线程同时 `Interlocked.Exchange` 同一字段，进度条数据完全乱序 |

规避：

- **串行覆盖**：第一阶段 UI 进度条接受"显示最近一次调用"的现状；阶段 2 起在 `AgentOrchestrationResult.TokenUsage` 中独立累加每个成员 Agent 的 token，不再让 UI 依赖工厂级单值。
- **并行覆盖**：第一阶段不做真正并发；阶段 3 开始的 Concurrent 路径必须先把 token 聚合从工厂层迁出（例如 `IAgentOrchestrator` 内置 `TokenAggregator`），再放开并行执行。
- 并发成员 Agent 不直接写工厂级 `ChatClient`，统一通过 Orchestrator 收集后再上报到 `AIAgentFactory.TokenUsageChanged`。

---

### 风险 2：`ChatHistoryDataProvider` 假设单主 Agent

当前历史保存依赖：

```text
context.Agent
context.Session.StateBag.agentid
```

多智能体中间消息如果直接入库，容易混淆归属。

规避：

- 第一阶段 UI 只显示最终回复，但历史层允许保留主 Agent 工具调用链。
- 第二阶段中间步骤默认只保存在内存，不作为独立聊天消息入库。
- 中间过程先作为日志或内存步骤。
- 后续新增编排消息表。

---

### 风险 3：长期记忆按主会话注入

`LongMemoryContextProvider` 使用 `StateBag` 中的：

```text
agentid
sessionid
turnid
currenttask
traceid
```

多 Agent 共用 session 时，子智能体可能拿到主智能体记忆。

规避：

- 第一阶段轻量子 Agent 不带 memory。
- 后续为子 Agent 创建独立 session 或显式设置 `agentid`。
- 需要定义是否允许子智能体读写长期记忆。

---

### 风险 4：UI 和用户预期不一致

如果内部已经多智能体讨论，但 UI 只显示最终回复，用户可能不知道发生了什么。

规避：

- 第一阶段在最终回复中附简短“参与智能体”。
- 阶段 2 开始由宿主侧 `UsedAgentIds` 记录真实参与智能体，不长期依赖模型自述。
- 后续增加折叠式“执行过程”。
- 不要一开始改消息气泡结构。

---

### 风险 5：误判工具边界导致重复设计

当前系统已经支持全局插件、智能体绑定插件和智能体绑定 MCP。  
如果多智能体编排层重新设计一套独立工具权限模型，容易与现有插件管理、系统设置和智能体绑定逻辑重复甚至冲突。

规避：

- 第一阶段直接复用现有工具绑定机制。
- 公共工具使用全局插件或宿主公共能力。
- 特殊工具绑定到专用智能体。
- Orchestration 层只记录实际使用者、步骤和风险，不重新管理插件/MCP 绑定。
- 后续如需任务级授权，只在现有绑定结果之上做临时收窄。

---

### 风险 6：技能无法按智能体约束

当前技能通过 `AgentSkillsProvider` 加载用户技能目录和工作区技能目录，暂时没有 `AgentEntity` 级别的技能绑定字段。  
这意味着不同主智能体会看到同一套技能集合，不能像插件和 MCP 一样精确限制到某个智能体。

需要特别注意：`AgentSkillsProvider` 是 **`Microsoft.Agents.AI` 1.3.0 NuGet 包提供的上游类型**（命名空间 `Microsoft.Agents.AI`），不是 Cortana 项目自有类。这带来一个直接约束：

- 项目侧无法直接在 `AgentSkillsProvider` 上加 `EnabledSkillIds` 这样的字段；
- 如果要按智能体约束技能，必须以"包一层 Cortana 自有 Provider（如 `AgentBoundSkillsProvider`）"或"在 `AgentSkillsProvider` 之外二次过滤"的方式实现，不能改 SDK 类型。

规避：

- 第一阶段接受技能全局可见现状。
- 通过 Agent instructions 约束智能体优先使用符合角色的技能。
- 后续如需技能绑定，先评估"Cortana 自有包装 Provider + 技能元数据 `allowedAgentIds` / `deniedAgentIds`" 方案，再决定是否引入 `AgentEntity.EnabledSkillIds`。
- 在没有技能绑定机制前，不把“技能隔离”作为多智能体编排第一阶段目标。

---

### 风险 7：附件和文件并发访问冲突

多智能体并发时，多个子智能体可能同时读取或修改同一个附件原始路径。

规避：

- 第一阶段附件只直接进入主 Agent。
- 子智能体通过主 Agent 摘要或明确任务描述间接获得附件信息。
- 真正并发阶段先禁止多个子智能体同时写同一文件。
- 后续引入文件锁、资源引用追踪或工作副本机制。

---

## 11. 结论

结合当前代码，推荐判断如下：

| 能力 | 当前基础 | 修改量 | 是否架构级 |
| --- | --- | --- | --- |
| @子智能体工具调用 | 已有 `BuildWithSubAgents` | 小 | 否 |
| Coordinator 策划/分派/总结 | 可基于 instructions 增强 | 小 | 否 |
| Concurrent 非流式并行分析 | 需新增编排层和执行隔离 | 中 | 否 |
| Concurrent 独立流式输出/并发写历史 | 当前不支持 | 大 | 是 |
| GroupChat 讨论模式 | 需新增编排层和轮次控制 | 中 | 否 |
| Magentic 完整流程（不持久化中间步骤） | 需编排状态机 | 中 | 否 |
| Magentic 完整流程（持久化中间步骤） | 同上 + 持久化 | 大 | 是 |
| 中间过程 UI 展示 | 当前不支持 | 大 | 是 |
| 多智能体历史/记忆独立 | 当前不支持 | 大 | 是 |

> 备注：上表中"是否架构级"列只有 `否` / `是` 两值。  
> Magentic 完整流程是否架构级取决于"是否持久化中间步骤"，已拆成两行分别评估，避免单元格出现复合判定。

最终建议：

```text
不要一开始做完整 Magentic + GroupChat + Concurrent + UI 可视化。

先基于现有 BuildWithSubAgents 做 Coordinator 轻量模式；
再新增 Orchestration 层；
然后依次接入 Concurrent 执行策略、GroupChat、Magentic；
最后再做过程可视化和持久化。
```

这样可以最大化复用当前架构，避免大面积重构。
