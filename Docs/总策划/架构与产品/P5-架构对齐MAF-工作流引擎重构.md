# P5 — 架构对齐 MAF：工作流引擎重构方案

> **文档定位**：P4 功能已基本完成但未正式发布，趁此窗口期进行架构对齐。
> 将自研的 `TaskExecutionEngine` / `OrchestratorAgent` / `SubAgentRunner` 替换为
> MAF v1.5.0 内置的 `MagenticWorkflowBuilder` / `ChatClientAgent` / `ToolApprovalAgent` 体系。
>
> **核心原则**：
> - **界面完全保留** — 统一对话流、时间线、折叠卡片、工具授权内联卡片等 UI 不变
> - **底层引擎对齐 MAF** — 复用框架的编排、循环、工具管理能力，减少自研维护成本
> - **永续对话循环** — 任务执行完成后不终态，循环整个工作流直到用户确认满意
> - **LLM 动态创建子智能体** — 模型返回子智能体的提示词和描述，程序动态构建
>
> **创建日期**：2026-05-28
>
> **前置文档**：`04-P4方案设计`、`08-P4补充-交互模型重设计`

---

## 1. 当前架构 vs 目标架构

### 1.1 当前架构（P4 自研）

```
用户输入
  │
  ▼
TaskExecutionEngine（自研引擎，驱动四阶段流程）
  │
  ├─ 阶段1: OrchestratorAgent.RunRequirementsPhaseAsync()
  │         └─ SubAgentRunner.RunInteractiveAsync()
  │            └─ IChatClient.GetResponseAsync()（批量调用）
  │
  ├─ 阶段2: OrchestratorAgent.RunPlanningPhaseAsync()
  │         └─ SubAgentRunner.RunInteractiveAsync()
  │
  ├─ 阶段3: OrchestratorAgent.ExecuteStepAsync() × N
  │         └─ AIAgent.RunStreamingAsync()（工具调用循环由框架处理）
  │
  └─ 阶段4: OrchestratorAgent.RunValidationPhaseAsync()
             └─ SubAgentRunner.RunAsync()
```

**问题**：
- `SubAgentRunner` 手动管理多轮对话（`[CONTINUE]/[DONE]` 文本标记），脆弱
- 子智能体的 instructions 是静态模板占位符替换，非动态生成
- 编排逻辑硬编码在 `TaskExecutionEngine` 中，不可扩展
- 工具授权自研（`ToolRiskLevel` + 事件），未复用 MAF `ToolApprovalAgent`
- 任务完成后终态，永续循环靠意图识别回调勉强实现

### 1.2 目标架构（MAF 对齐）

```
用户输入
  │
  ▼
WorkflowSession（永续会话，不终态）
  │
  ▼
MagenticOrchestrator（MAF 内置编排器）
  │
  ├─ UpdatePlanAsync() → ManagerAgent（LLM 动态生成计划 + 子智能体定义）
  │                      ↓
  │                      返回: { plan, agents[] }
  │                      ↓
  │                      程序动态创建 ChatClientAgent 实例并注册
  │
  ├─ RequirePlanSignoff → 等待用户确认（对话式，非按钮）
  │
  ├─ RunCoordinationRoundAsync() → 循环委派子智能体执行
  │   ├─ UpdateProgressLedgerAsync() → 进度追踪（LLM 判断是否完成/停滞）
  │   ├─ 选择 NextSpeaker → 委派给子智能体
  │   ├─ 子智能体执行（ChatClientAgent + ToolApprovalAgent 管道）
  │   └─ 检测停滞 → 自动重规划
  │
  ├─ PrepareFinalAnswerAsync() → 生成最终答案
  │
  └─ 回到对话 → 等待用户输入 → 满意则结束 / 不满意则重新循环
```

---

## 2. 核心设计决策

### 2.1 永续对话循环

**P4 问题**：任务完成 = 终态。用户不满意只能新建任务。

**P5 方案**：

```
┌─────────────────────────────────────────────────────┐
│                  永续工作流循环                        │
│                                                     │
│  用户输入任务                                        │
│      ↓                                              │
│  ┌─→ LLM 规划（生成计划 + 子智能体定义）              │
│  │       ↓                                          │
│  │   用户确认计划（对话式）                           │
│  │       ↓                                          │
│  │   执行（子智能体循环协调）                         │
│  │       ↓                                          │
│  │   生成结果                                        │
│  │       ↓                                          │
│  │   AI: "执行完成，有什么需要调整的吗？"              │
│  │       ↓                                          │
│  │   用户输入                                        │
│  │       ↓                                          │
│  │   意图识别                                        │
│  │     ├─ "可以了" → 标记完成，退出循环               │
│  │     ├─ "第三步重做" → 重新循环（部分重做）  ───┐   │
│  │     ├─ "换个思路" → 重新循环（全部重做）  ────┐│   │
│  │     └─ "xxx怎么样" → 回答问题，继续等待      ││   │
│  │                                               ││   │
│  └───────────────────────────────────────────────┘│   │
│  └────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────┘
```

**实现方式**：MAF `MagenticOrchestrator` 的 `MaxRounds = null`（无限轮次）+ 外部 Session 永不终止。
`IsTerminated` 仅在用户明确说"满意/完成"时才置 true。

### 2.2 LLM 动态创建子智能体

**P4 问题**：`agentTypeDescription` 是纯文本标签，没有真正用来构建子智能体。
子智能体的 system prompt 是 `OrchestratorPrompts.StepExecutor` 模板占位符替换，千篇一律。

**P5 方案**：

ManagerAgent（编排器）在制定计划时，同时输出每个子智能体的完整定义：

```json
{
  "plan": "任务总结...",
  "steps": [
    {
      "title": "数据采集",
      "agentDefinition": {
        "name": "DataCollector",
        "description": "擅长从公开数据源采集结构化数据的专家",
        "systemPrompt": "你是一个数据采集专家。你的任务是...\n\n## 工作要求\n1. ...\n2. ...",
        "requiredTools": ["search_web", "read_file", "write_file"]
      }
    },
    {
      "title": "数据分析",
      "agentDefinition": {
        "name": "DataAnalyst",
        "description": "精通数据分析和可视化的专家",
        "systemPrompt": "你是一个数据分析专家。基于前置步骤采集的数据...",
        "requiredTools": ["execute_code", "write_file"]
      }
    }
  ]
}
```

程序收到后，动态创建 `ChatClientAgent`：

```csharp
// 伪代码
foreach (var stepDef in plan.Steps)
{
    var agent = new AIAgentBuilder(chatClient)
        .WithName(stepDef.AgentDefinition.Name)
        .WithDescription(stepDef.AgentDefinition.Description)
        .WithInstructions(stepDef.AgentDefinition.SystemPrompt)
        .WithTools(ResolveTools(stepDef.AgentDefinition.RequiredTools))
        .Build();

    workflow.AddParticipant(agent);
}
```

**关键点**：
- LLM 生成的 `systemPrompt` 包含完整的领域知识和工作指令
- `requiredTools` 从系统已注册工具列表中选择（LLM 知道可用工具列表）
- 每个子智能体是独立的 `ChatClientAgent`，有自己的会话和工具集
- 子智能体之间的数据传递通过 `AgentSession.StateBag` 或消息历史

### 2.3 ToolApprovalAgent 统一工具授权

**P4 问题**：自研 `ToolRiskLevel` 黑名单 + `OnToolAuthRequired` 事件，与 MAF 管道割裂。

**P5 方案**：

```csharp
// 每个子智能体构建时，插入 ToolApprovalAgent 中间件
var agent = new AIAgentBuilder(chatClient)
    .WithToolApproval(options =>
    {
        // 安全工具自动放行
        options.AllowList = ["search_web", "read_file", "calculate"];
        // 高危工具需人工确认
        options.RequireApproval = ["write_file", "execute_code", "send_email"];
        // 回调：暂停等待用户确认
        options.OnApprovalRequired = async (toolCall, ct) =>
        {
            // 发布事件到 UI → 内联授权卡片
            PublishToolAuthEvent(taskId, toolCall);
            // 等待用户响应
            return await WaitForUserApproval(taskId, toolCall.Id, ct);
        };
    })
    .Build();
```

**UI 层不变**：内联 `tool_auth` 卡片仍然在对话流中展示，只是触发源从自研事件改为 MAF 中间件。

---

## 3. MAF 组件映射

### 3.1 核心类对应关系

| P4 自研 | P5 MAF 对齐 | 说明 |
|---|---|---|
| `TaskExecutionEngine` | `MagenticOrchestrator` + `WorkflowSession` | 引擎驱动 → 编排器驱动 |
| `OrchestratorAgent` | `MagenticManager` (ManagerAgent) | 编排决策 |
| `SubAgentRunner` | `ChatClientAgent.RunAsync/RunStreamingAsync` | 子智能体执行 |
| `IOrchestratorAgent.RunRequirementsPhaseAsync` | ManagerAgent 首轮对话 | 需求分析 |
| `IOrchestratorAgent.RunPlanningPhaseAsync` | `MagenticManager.UpdatePlanAsync` | 计划制定 |
| `IOrchestratorAgent.ExecuteStepAsync` | `RunCoordinationRoundAsync` 委派 | 步骤执行 |
| `IOrchestratorAgent.RunValidationPhaseAsync` | `ProgressLedger.IsRequestSatisfied` | 完成判断 |
| `IStepScheduler` | `MagenticOrchestrator` 内置调度 | 步骤调度 |
| `IPlanPersistence` | `CheckpointManager` | 断点恢复 |
| `ToolRiskLevel` + 事件 | `ToolApprovalAgent` | 工具授权 |
| `[CONTINUE]/[DONE]` 标记 | `ChatClientAgent` 内置工具循环 | 多轮控制 |
| `RunningTaskEngineContext` | `MagenticTaskContext` | 运行状态 |

### 3.2 事件映射（UI 层不变）

| UI 事件 | P4 触发源 | P5 触发源 |
|---|---|---|
| `OnTaskPhaseStarted` | `TaskExecutionEngine` 硬编码 | `MagenticOrchestratorEvent` 转换 |
| `OnTaskPhaseCompleted` | `TaskExecutionEngine` 硬编码 | `MagenticOrchestratorEvent` 转换 |
| `OnTaskPlanCreated` | `TaskExecutionEngine` | `MagenticPlanCreatedEvent` |
| `OnTaskPlanConfirmed` | `WaitForPlanConfirmationAsync` | `MagenticPlanReviewResponse.IsApproved` |
| `OnTaskStepSubAction` | `CreateSubActionCallback` | `AgentResponseUpdate` 流式事件 |
| `OnToolAuthRequired` | 自研黑名单 | `ToolApprovalAgent.OnApprovalRequired` |
| `OnWorkflowConversationMessage` | `CreateAiMessageCallback` | `context.YieldOutputAsync` |

### 3.3 新增概念

| 概念 | MAF 来源 | 作用 |
|---|---|---|
| `ProgressLedger` | `MagenticProgressLedger` | LLM 每轮评估进度（是否完成/停滞/下一个执行者） |
| `TaskLedger` | `TaskLedger` (Facts + Plan) | 任务事实 + 当前计划（LLM 可读格式） |
| `Stall Detection` | `IsInLoop` / `IsProgressBeingMade` | 自动检测执行停滞，触发重规划 |
| `Reset & Replan` | `ResetAndReplanAsync` | 停滞时清空历史，重新规划 |
| `AgentSession` | `AgentSession` + `StateBag` | 有状态会话，子智能体间数据共享 |

---

## 4. 编排过程完全可观测（P4 不具备的核心能力）

### 4.1 P4 的问题：编排是黑盒

当前 P4 的编排逻辑硬编码在 `TaskExecutionEngine` 的 C# 代码中：

```
TaskExecutionEngine.ExecuteFullLifecycleAsync()
  ├─ RunRequirementsPhaseAsync()   ← C# 代码决定调用，用户看不到为什么
  ├─ RunPlanningPhaseAsync()       ← C# 代码决定调用，用户看不到为什么
  ├─ ExecuteStepAsync() × N       ← C# 代码按 dependsOn 顺序执行
  └─ RunValidationPhaseAsync()     ← C# 代码决定调用
```

用户只看到阶段的开始/结束，看不到：
- 编排器**为什么**这样分步骤
- 编排器**为什么**选择这个顺序
- 子智能体是怎么被创建的（用了什么提示词、分配了什么工具）
- 编排器对每步结果的判断过程

### 4.2 P5 的突破：编排器 = 带工具的 LLM Agent

MAF 的 ManagerAgent 本身就是一个 `ChatClientAgent`，它通过**思维过程 + 工具调用**来完成编排。
这意味着整个编排过程天然可观测——和专家模式的 AI 工具调用完全同构。

```
ManagerAgent（编排器 = ChatClientAgent，RunStreamingAsync）
  │
  │  💬 思维过程（流式文本）：
  │  "用户要在当前目录创建5个随机文本文件，每个文件作为独立步骤。
  │   这是一个简单的文件操作任务，需要5个并行的文件生成子智能体，
  │   每个都配备 write_file 工具..."
  │
  ├─ 🔧 调用工具: create_agent({
  │     name: "FileCreator_1",
  │     description: "创建随机文本文件的专家",
  │     systemPrompt: "你是文件生成专家。你的任务是...",
  │     tools: ["write_file"]
  │   })
  │   → UI 实时显示卡片：子智能体创建详情（提示词、工具列表）
  │
  ├─ 🔧 调用工具: create_agent({...FileCreator_2...})
  │   → UI 实时显示第二个子智能体卡片
  │
  │  💬 思维过程：
  │  "5个子智能体已创建，它们之间没有依赖关系，可以并行执行..."
  │
  ├─ 🔧 调用工具: delegate_task("FileCreator_1", "创建第1个随机文件...")
  │   → UI 显示任务委派卡片
  │   → FileCreator_1 开始执行（它的工具调用也实时可见）
  │
  │  💬 思维过程：
  │  "FileCreator_1 完成了，生成了 abc123.txt。
  │   检查进度：1/5 完成，继续等待其余..."
  │
  └─ ...
```

### 4.3 UI 对接：复用现有卡片机制

这个过程和专家模式已有的 `step_sub` 卡片机制**完美对接**：

| ManagerAgent 动作 | 对应 UI 卡片 | 已有基础 |
|---|---|---|
| 流式思维文本 | `💬 AI 分析` 折叠卡片 | ✅ 专家模式已实现 |
| 调用 `create_agent` 工具 | `🔧 创建子智能体: XXX` 折叠卡片 | ✅ 工具调用卡片已实现，展开可看提示词 |
| 调用 `delegate_task` 工具 | `🔧 委派任务给 XXX` 折叠卡片 | ✅ 同上 |
| 调用 `check_progress` 工具 | `🔧 检查进度` 折叠卡片 | ✅ 同上 |
| 子智能体的工具调用 | 嵌套在步骤卡片内的 `🔧` 子卡片 | ✅ 时间线步骤卡片已实现 |

**不需要新增任何 UI 组件**——ManagerAgent 的编排过程自然产生的事件流，
和现有的 `OnTaskStepSubAction`（`tool_start` / `tool_end` / `text_delta` / `text_completed`）
完全同构，直接复用已有的卡片渲染管线。

### 4.4 用户视角的体验升级

```
┌──────────────────────────────────────────────────────────────┐
│  ╔════════════════════════════════════════════════════════╗  │
│  ║ 帮我创建5个随机文本文件，每个文件一步                    ║  │
│  ╚════════════════════════════════════════════════════════╝  │
│                                                              │
│  ┌─────────────────────────────────────────────────────┐    │
│  │ ▸ 💬 编排分析                                  ✓    │    │
│  │   （折叠：用户要创建5个文件，文件名和内容随机...）    │    │
│  └─────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────┐    │
│  │ ▸ 🔧 创建子智能体: FileCreator_1              ✓    │    │
│  │   （折叠：提示词 / 工具: write_file）                │    │
│  └─────────────────────────────────────────────────────┘    │
│  ┌─────────────────────────────────────────────────────┐    │
│  │ ▸ 🔧 创建子智能体: FileCreator_2              ✓    │    │
│  └─────────────────────────────────────────────────────┘    │
│  ... (×5)                                                    │
│  ┌─────────────────────────────────────────────────────┐    │
│  │ ▸ 💬 编排决策                                  ✓    │    │
│  │   （折叠：5个子智能体已创建，无依赖，并行执行...）    │    │
│  └─────────────────────────────────────────────────────┘    │
│                                                              │
│  ●─ ▶ 开始执行                                              │
│  │                                                           │
│  ●─ ✓ 步骤 1: 创建随机文件 1                                │
│  │   ┌─────────────────────────────────────────────────┐    │
│  │   │ ▸ 🔧 调用 write_file                      ✓    │    │
│  │   │   （折叠：path=abc123.txt, content=...）        │    │
│  │   └─────────────────────────────────────────────────┘    │
│  │                                                           │
│  ●─ ✓ 步骤 2: 创建随机文件 2                                │
│  ... (×5)                                                    │
│  │                                                           │
│  ●─ ✓ 执行完成                                              │
│                                                              │
│  所有5个文件已创建完成，有什么需要调整的吗？                    │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐   │
│  │ 输入想法...                                          │   │
│  └──────────────────────────────────────────────────────┘   │
└──────────────────────────────────────────────────────────────┘
```

**核心价值**：用户不仅能看到"做了什么"，还能看到"为什么这么做"和"怎么做的"——
编排器的每一个决策、每一个子智能体的创建细节、每一次工具调用，全部透明可见。

---

## 5. 永续循环的详细设计

### 4.1 生命周期

```
WorkflowSession（从用户创建任务到用户说"满意"）
  │
  ├─ Round 1: 首次执行
  │   ├─ ManagerAgent 规划 + 创建子智能体
  │   ├─ 用户确认计划
  │   ├─ 协调执行
  │   ├─ 生成最终答案
  │   └─ AI: "执行完成，有什么需要调整的吗？"
  │
  ├─ Round 2: 用户不满意，继续
  │   ├─ 用户: "第三步的数据不对，用最新的数据源"
  │   ├─ ManagerAgent 重规划（考虑用户反馈）
  │   ├─ 可选: 用户确认新计划
  │   ├─ 协调执行（可复用已完成步骤）
  │   ├─ 生成最终答案
  │   └─ AI: "已更新，还有其他需要吗？"
  │
  ├─ Round N: ...
  │
  └─ 终止: 用户说"可以了" → IsTerminated = true
```

### 4.2 与意图识别的关系

P4 的 `ConversationIntentRecognizer` 仍然保留，但简化为 ManagerAgent 的内置能力：

```
用户输入 → ManagerAgent 判断：
  ├─ 这是新的执行指令 → 进入编排循环
  ├─ 这是对当前结果的修改意见 → 追加到上下文，触发重规划
  ├─ 这是一个问题 → 直接回答（不进入编排循环）
  └─ 这是满意确认 → 标记 IsTerminated
```

不再需要独立的意图识别子智能体，ManagerAgent 在理解上下文的过程中自然判断。

### 4.3 上下文管理

```
WorkflowSession.StateBag:
  ├─ "task_definition"     → 用户原始任务描述
  ├─ "current_plan"        → 当前执行计划（JSON）
  ├─ "step_results"        → 各步骤执行结果摘要
  ├─ "conversation_history"→ 对话历史（含用户反馈）
  ├─ "created_agents"      → 动态创建的子智能体定义
  └─ "round_count"         → 当前循环轮次
```

每轮循环结束后，步骤结果摘要追加到 `conversation_history`，
下一轮 ManagerAgent 可以看到完整历史（包含用户对上一轮结果的反馈）。

---

## 5. LLM 动态创建子智能体的详细设计

### 5.1 ManagerAgent 的输出格式

ManagerAgent 的 system prompt 要求它在规划时同时输出子智能体定义：

```
你是任务编排器。你的职责是：
1. 分析用户任务
2. 制定执行计划
3. 为每个步骤定义执行子智能体（包括提示词、工具需求）

## 输出格式

```json
{
  "taskSummary": "一句话总结",
  "plan": [
    {
      "stepId": "step_1",
      "title": "步骤标题",
      "dependsOn": [],
      "agent": {
        "name": "AgentName",
        "description": "一句话能力描述（用于编排器选择 NextSpeaker）",
        "systemPrompt": "完整的系统提示词...",
        "tools": ["tool_1", "tool_2"]
      }
    }
  ]
}
```

## 可用工具列表
{动态注入系统已注册工具列表}
```

### 5.2 动态创建流程

```csharp
// 1. ManagerAgent 输出计划 JSON
var planResponse = await managerAgent.RunAsync(messages, session, ct);
var plan = ParsePlan(planResponse);

// 2. 动态创建子智能体
var participants = new List<AIAgent>();
foreach (var step in plan.Steps)
{
    var agentDef = step.Agent;
    
    var agent = new AIAgentBuilder(ResolveChatClient())
        .WithName(agentDef.Name)
        .WithDescription(agentDef.Description)
        .WithInstructions(agentDef.SystemPrompt)
        .WithTools(ResolveTools(agentDef.Tools))
        .WithToolApproval(ConfigureApproval)  // 统一工具授权
        .Build();
    
    participants.Add(agent);
}

// 3. 注册到工作流
workflow.AddParticipants(participants);
```

### 5.3 工具解析

系统维护一个全局工具注册表（已有 `AIAgentFactory.GetAvailableToolNames()`），
LLM 从中选择工具名称，程序通过名称解析为 `AITool` 实例：

```csharp
private IEnumerable<AITool> ResolveTools(List<string> toolNames)
{
    foreach (var name in toolNames)
    {
        if (_toolRegistry.TryGet(name, out var tool))
            yield return tool;
        else
            _logger.LogWarning("未知工具: {ToolName}，跳过", name);
    }
}
```

### 5.4 MCP 工具集成

MAF 内置 `DefaultMcpToolHandler` + `InvokeMcpToolExecutor`，
MCP 服务器的工具也注册到全局工具表中，LLM 可以像使用本地工具一样使用 MCP 工具：

```
系统已注册工具列表:
  - search_web（本地）
  - read_file（本地）
  - write_file（本地）
  - browser_navigate（MCP: Browser-Mcp）
  - browser_click（MCP: Browser-Mcp）
  - execute_sql（MCP: Database-Mcp）
```

---

## 6. UI 层适配（最小改动）

### 6.1 不变的部分

- `ConversationMessageVm` 数据模型
- `P4TaskDetailVm` 的 FeedItems 订阅/渲染逻辑
- `WorkflowDetailView.axaml` XAML 布局
- 时间线节点、折叠卡片、工具授权卡片的渲染
- 输入框行为和永续对话交互

### 6.2 需要改造的部分

| UI 组件 | 改动 |
|---|---|
| `P4TaskDetailVm.SubscribeEvents()` | 事件源从 `TaskExecutionEngine` 改为 MAF 事件适配层 |
| 事件参数 | `TaskPhaseEventArgs` 等保留，由适配层从 MAF 事件转换 |
| 进度文本 | 从 `MagenticProgressLedgerUpdatedEvent` 提取 |

### 6.3 事件适配层

在 MAF `Workflow` 和 UI 事件总线之间加一个薄适配层：

```csharp
/// <summary>
/// MAF 工作流事件 → Cortana UI 事件总线适配器。
/// 将 MagenticOrchestratorEvent 转换为 P4TaskDetailVm 已订阅的事件格式。
/// </summary>
internal sealed class WorkflowEventAdapter
{
    private readonly IPublisher _publisher;

    public void OnWorkflowEvent(WorkflowEvent evt, string taskId)
    {
        switch (evt)
        {
            case MagenticPlanCreatedEvent plan:
                _publisher.Publish(Events.OnTaskPhaseCompleted,
                    new TaskPhaseEventArgs(taskId, DateTimeOffset.UtcNow, "planning"));
                _publisher.Publish(Events.OnTaskPlanCreated, ...);
                break;

            case MagenticProgressLedgerUpdatedEvent progress:
                // 提取 NextSpeaker → 发布步骤开始事件
                _publisher.Publish(Events.OnTaskStepStarted, ...);
                break;

            // ... 其他事件映射
        }
    }
}
```

---

## 7. 迁移路径

### Phase 1: 引擎替换（不改 UI）

1. 新建 `MagenticTaskEngine`（替代 `TaskExecutionEngine`）
2. 实现 `WorkflowEventAdapter`（MAF 事件 → UI 事件）
3. ManagerAgent 的 system prompt 设计（输出计划 + 子智能体定义）
4. 动态子智能体创建逻辑
5. `ToolApprovalAgent` 集成

**验证**：UI 行为与 P4 完全一致，但底层已是 MAF 驱动。

### Phase 2: 永续循环

1. `WorkflowSession` 不自动终态
2. 执行完成后 AI 自动追问
3. 用户反馈路由到 ManagerAgent → 触发重规划
4. `satisfied` 意图 → `IsTerminated = true`

**验证**：用户可以多轮循环修改直到满意。

### Phase 3: 增强能力

1. `ProgressLedger` 进度追踪（LLM 每轮评估是否停滞）
2. 自动 Stall Detection + Replan
3. `CheckpointManager` 断点恢复
4. MCP 工具统一注册

---

## 8. 与 P4 方案的差异总结

| 维度 | P4（自研） | P5（MAF 对齐） |
|---|---|---|
| 编排模式 | 预制四阶段流水线 | LLM 动态决策循环 |
| 子智能体 | 模板占位符，千篇一律 | LLM 生成独立 system prompt |
| 循环控制 | `[CONTINUE]/[DONE]` 文本标记 | `ChatClientAgent` 内置工具循环 |
| 进度判断 | 硬编码验证阶段 | `ProgressLedger` LLM 每轮评估 |
| 停滞处理 | 无 | 自动检测 + 重规划 |
| 工具授权 | 自研黑名单 + 事件 | `ToolApprovalAgent` 中间件 |
| 断点恢复 | 自研 `IPlanPersistence` | `CheckpointManager` |
| 任务生命周期 | 完成即终态 | 永续循环直到用户满意 |
| 计划确认 | 对话式（已实现） | `RequirePlanSignoff`（MAF 内置） |
| 子智能体工具 | 全局工具集 | 按需分配（LLM 决定每个 agent 用什么工具） |

---

## 9. 风险与注意事项

1. **MAF 版本锁定** — v1.5.0 的 `MagenticOrchestrator` 标记为 `[Experimental]`，
   后续版本可能有 breaking change。需要 pin 版本或 fork。

2. **流式输出** — MAF `RunStreamingAsync` 返回 `IAsyncEnumerable<AgentResponseUpdate>`，
   需要适配为 UI 层已有的 `StepSubAction` 事件格式。

3. **计划格式** — MAF 的 `TaskLedger` 是自然语言格式（LLM 输出的文本），
   不是结构化 JSON。需要额外要求 ManagerAgent 同时输出结构化 JSON 供程序解析。

4. **子智能体并行** — MAF Magentic-One 默认是串行（每轮选一个 Agent），
   不支持多 Agent 并行执行。并行需求需自行扩展或使用 `WorkflowBuilder.AddFanOutEdge`。

5. **对话历史膨胀** — 永续循环会累积大量对话历史。
   需要结合 MAF 的 `CompactionProvider`（上下文压缩）定期压缩。
