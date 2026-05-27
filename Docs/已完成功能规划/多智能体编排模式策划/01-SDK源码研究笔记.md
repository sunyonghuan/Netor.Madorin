# 01 - Microsoft.Agents.AI / Workflows SDK 源码研究笔记

> **目的**：本文档记录对 `Microsoft.Agents.AI` 与 `Microsoft.Agents.AI.Workflows` 1.5.0 源码的研究结论，作为后续所有编排方案文档的事实基础。  
> **来源**：`E:\OpenSourse\agent-framework-main.v1.5.0`（与项目当前引用的 1.3.0 兼容，重大 API 一致；如使用 1.3.0，部分 API 仍处 Experimental 状态，行为相同）。  
> **结论原则**：所有 API 路径、默认值、签名、Experimental 状态以源码为准；与 §2 项目代码现状结合使用。

---

## 1. 核心命名空间与程序集

| 包 | 命名空间 | 作用 |
| --- | --- | --- |
| `Microsoft.Agents.AI.Abstractions` | `Microsoft.Agents.AI` | `AIAgent`、`AgentSession`、`AIContext`、`AIContextProvider`、`ChatHistoryProvider` 等抽象 |
| `Microsoft.Agents.AI` | `Microsoft.Agents.AI` | `ChatClientAgent`、`AgentSkillsProvider`、`Compaction.*`、`Memory.*`、`AIAgentBuilder` 等 |
| `Microsoft.Agents.AI.Workflows` | `Microsoft.Agents.AI.Workflows` | `Workflow`、`AgentWorkflowBuilder`、`MagenticWorkflowBuilder`、`GroupChatWorkflowBuilder`、`HandoffWorkflowBuilder`、`InProcessExecution` 等 |

**项目当前已引用**：`Microsoft.Agents.AI` 1.3.0、`Microsoft.Agents.AI.Workflows` 1.3.0（见 `Src/Netor.Cortana.AI/Netor.Cortana.AI.csproj`）。Agent-framework 1.5.0 与 1.3.0 在本方案涉及的 API 上保持一致；如有差异以 spike 验证为准。

---

## 2. AgentWorkflowBuilder（核心静态工厂）

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/AgentWorkflowBuilder.cs`

```csharp
public static partial class AgentWorkflowBuilder
{
    public static Workflow BuildSequential(params IEnumerable<AIAgent> agents);
    public static Workflow BuildSequential(string workflowName, params IEnumerable<AIAgent> agents);

    public static Workflow BuildConcurrent(
        IEnumerable<AIAgent> agents,
        Func<IList<List<ChatMessage>>, List<ChatMessage>>? aggregator = null);
    public static Workflow BuildConcurrent(
        string workflowName,
        IEnumerable<AIAgent> agents,
        Func<IList<List<ChatMessage>>, List<ChatMessage>>? aggregator = null);

    [Experimental("MAAIW001")]
    public static HandoffWorkflowBuilder CreateHandoffBuilderWith(AIAgent initialAgent);

    public static GroupChatWorkflowBuilder CreateGroupChatBuilderWith(
        Func<IReadOnlyList<AIAgent>, GroupChatManager> managerFactory);
}
```

**关键事实**：

- `AgentWorkflowBuilder` 是一个**静态类**，提供四个工厂：`BuildSequential`、`BuildConcurrent`、`CreateHandoffBuilderWith`、`CreateGroupChatBuilderWith`。
- **Magentic 不在这里**。Magentic 走独立的 `new MagenticWorkflowBuilder(managerAgent)`，需要 `using Microsoft.Agents.AI.Workflows;`。
- `BuildConcurrent` 默认 `aggregator` 行为：返回"每个 Agent 最后一条消息组成的列表"，不是字符串汇总。如果需要单条最终回复，必须再接一个 Summarizer Agent 或自定义 aggregator。
- `BuildConcurrent` 内部使用 `AIAgentHostOptions { ReassignOtherAgentsAsUsers = true, ForwardIncomingMessages = false }`，即子 Agent 看不到主 Agent 的指令，仅看到用户消息。

### 2.1 BuildConcurrent 内部结构

```text
Start (FanOut)
  ├── Agent A → 累加器 A
  ├── Agent B → 累加器 B
  └── Agent C → 累加器 C
                       ↓ (FanInBarrier)
                 ConcurrentEndExecutor (aggregator)
                       ↓
                  Workflow Output
```

`ConcurrentEndExecutor` 等所有累加器完成后才触发，**不是流式输出**。

---

## 3. MagenticWorkflowBuilder

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/MagenticWorkflowBuilder.cs`

```csharp
[Experimental("MAAIW001")]
public class MagenticWorkflowBuilder(AIAgent managerAgent)
{
    public MagenticWorkflowBuilder AddParticipants(params IEnumerable<AIAgent> agents);
    public MagenticWorkflowBuilder WithName(string name);
    public MagenticWorkflowBuilder WithDescription(string description);
    public MagenticWorkflowBuilder WithMaxRounds(int? maxRounds = null);
    public MagenticWorkflowBuilder WithMaxResets(int? maxResets = null);
    public MagenticWorkflowBuilder WithMaxStalls(int maxStalls = TaskLimits.DefaultMaxStallCount);
    public MagenticWorkflowBuilder RequirePlanSignoff(bool requirePlanSignoff = true);
    public Workflow Build();
}
```

### 3.1 重大事实：默认要求人工批准

```csharp
private bool _requirePlanSignoff = true;
```

`RequirePlanSignoff` **默认 true**！这意味着：

- 不调用 `.RequirePlanSignoff(false)` 时，Magentic 会在初始计划生成后**等待 HITL 响应**（`MagenticPlanReviewRequest`/`MagenticPlanReviewResponse`），项目侧必须处理这条 RequestPort。
- 第一阶段不实现 HITL 时，**必须显式调用 `.RequirePlanSignoff(false)`**，否则 Magentic Workflow 卡死在等待响应。
- 阶段 5 想引入 HITL 时，需要在 UI 增加"计划批准"卡片，并通过 `Run.ResumeAsync(responses)` 提交 `ExternalResponse`。

### 3.2 内部协调流程

源码 `MagenticOrchestrator.cs` 揭示一次 Magentic 任务的 LLM 调用次数：

```text
1. UpdatePlanAsync():
   - ManagerAgent.RunAsync(facts prompt) ............ LLM 调用 ①
   - ManagerAgent.RunAsync(plan prompt) ............. LLM 调用 ②
2. 每个内层循环（RunCoordinationRoundAsync）：
   - UpdateProgressLedgerAsync()
     - ManagerAgent.RunAsync(progress prompt) ....... LLM 调用 ③ (每轮)
     - 失败重试最多 N 次（taskContext.TaskLimits.MaxProgressLedgerRetryCount）
   - 判断终止 / Stall / 选下一个 speaker
   - 选中的 participant.RunAsync(instruction) ....... LLM 调用 ④ (每轮)
3. PrepareFinalAnswerAsync():
   - ManagerAgent.RunAsync(final prompt) ............ LLM 调用 ⑤
```

**结论**：一次 5 轮 Magentic 大约 **2 + 5×2 + 1 = 13 次 LLM 调用**（最少）。token 成本显著高于普通对话，必须在 §6.4 阈值表里给 Magentic 单独的成本警告。

### 3.3 默认阈值

```csharp
// MagenticConstants.cs（推断自 TaskLimits.DefaultMaxStallCount 使用）
WithMaxStalls 默认 = 3
WithMaxRounds 默认 = null（不限）
WithMaxResets 默认 = null（不限）
```

第一阶段建议项目侧显式设置 `WithMaxRounds(5)` 和 `WithMaxResets(2)`，防止无限规划。

---

## 4. GroupChatWorkflowBuilder

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/GroupChatWorkflowBuilder.cs`、`GroupChatManager.cs`、`RoundRobinGroupChatManager.cs`

```csharp
public sealed class GroupChatWorkflowBuilder
{
    public GroupChatWorkflowBuilder AddParticipants(params IEnumerable<AIAgent> agents);
    public GroupChatWorkflowBuilder WithName(string name);
    public GroupChatWorkflowBuilder WithDescription(string description);
    public Workflow Build();
}

public abstract class GroupChatManager
{
    public int IterationCount { get; internal set; }
    public int MaximumIterationCount { get; set; } = 40;   // 默认 40

    protected internal abstract ValueTask<AIAgent> SelectNextAgentAsync(...);
    protected internal virtual ValueTask<IEnumerable<ChatMessage>> UpdateHistoryAsync(...);
    protected internal virtual ValueTask<bool> ShouldTerminateAsync(...);
    protected internal virtual void Reset();
}
```

**关键事实**：

- `GroupChatManager.MaximumIterationCount` **默认 = 40**，原文档"最大轮次 3"是期望值，第一阶段必须**显式设置**：

  ```csharp
  var manager = new RoundRobinGroupChatManager(agents)
  {
      MaximumIterationCount = 3   // 显式覆盖
  };
  ```

- `RoundRobinGroupChatManager` 是 SDK 自带的简单实现；项目侧可继承 `GroupChatManager` 实现"主持智能体选下一个发言者"逻辑（Moderator 模式）。
- `GroupChatHost` 内部用 `IResettableExecutor` 模式管理 manager 生命周期，每次 chat 结束自动 `Reset()`。

---

## 5. HandoffWorkflowBuilder

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/HandoffWorkflowBuilder.cs`

```csharp
[Experimental("MAAIW001")]
public sealed class HandoffWorkflowBuilder(AIAgent initialAgent)
    : HandoffWorkflowBuilderCore<HandoffWorkflowBuilder>(initialAgent) { }

public class HandoffWorkflowBuilderCore<TBuilder>
{
    public const string FunctionPrefix = "handoff_to_";
    public string? HandoffInstructions { get; private set; }  // 有默认值
    public TBuilder WithHandoffInstructions(string? instructions);
    public TBuilder EmitAgentResponseUpdateEvents(bool = true);
    public TBuilder EmitAgentResponseEvents(bool = true);
    public TBuilder WithToolCallFilteringBehavior(HandoffToolCallFilteringBehavior behavior);
    public TBuilder EnableReturnToPrevious();
    public TBuilder WithHandoffs(AIAgent from, params IEnumerable<AIAgent> to);
    public Workflow Build();
}
```

**关键事实**：

- Handoff 通过工具调用实现：SDK 会自动为每个候选目标 Agent 生成一个名为 `handoff_to_<agent_id>` 的 `AITool`，注入到当前 Agent 的 `ChatOptions.Tools` 中。
- **要求主模型支持 FunctionCall**，否则 Handoff 不会发生。这与项目当前 `AIAgentFactory.BuildWithSubAgents` 的"主模型不支持 FunctionCall → 静默丢弃"是同类问题。
- `WithHandoffInstructions` 默认值已经包含完整提示，通常无需自定义。

---

## 6. Workflow.AsAIAgent()（最重要的利好）

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/WorkflowHostingExtensions.cs`

```csharp
public static AIAgent AsAIAgent(
    this Workflow workflow,
    string? id = null,
    string? name = null,
    string? description = null,
    IWorkflowExecutionEnvironment? executionEnvironment = null,
    bool includeExceptionDetails = false,
    bool includeWorkflowOutputsInResponse = false)
{
    return new WorkflowHostAgent(workflow, id, name, ...);
}
```

**重大利好**：

- `WorkflowHostAgent : AIAgent` 直接实现 `AIAgent`，意味着 **Workflow 可以被当作普通 AIAgent 使用**。
- 项目侧 `AiChatHostedService.SendMessageAsync` 主链路 `_agent.RunStreamingAsync(...)` **完全不需要改**。
- `IAgentOrchestrator` 在阶段 2-3 只需返回 `AIAgent`：
  
  ```csharp
  AIAgent orchestrationAgent = magenticWorkflow.AsAIAgent(
      name: "MagenticOrchestrator",
      description: "...");
  // 后续走标准 AiChatHostedService 流式输出链路
  ```

- 这把原文档估计的"中改动"进一步压缩为"以包装为主"的小改动；UI/WebSocket/历史层都不需要为编排新增分支。

**配套事实**：

- `WorkflowHostAgent` 内部维护自己的 `WorkflowSession`，与外层 `AgentSession` 通过 `CreateSessionCoreAsync` / `SerializeSessionCoreAsync` 协作。
- `RunCoreStreamingAsync` 会把 Workflow 内部事件转换为 `AgentResponseUpdate` 流式输出。
- Workflow 内部 session 与主聊天 session **不共享 StateBag**，需要项目侧自行桥接（如把 `sessionid` / `traceid` 通过 Workflow 输入消息传递）。

---

## 7. AsAIFunction（子智能体作为工具）

源码：`dotnet/src/Microsoft.Agents.AI/AgentExtensions.cs`

```csharp
public static AIFunction AsAIFunction(
    this AIAgent agent,
    AIFunctionFactoryOptions? options = null,
    AgentSession? session = null)
{
    async Task<string> InvokeAgentAsync(
        [Description("Input query to invoke the agent.")] string query,
        CancellationToken cancellationToken) { ... }

    options ??= new();
    options.Name ??= SanitizeAgentName(agent.Name);
    options.Description ??= agent.Description;

    return AIFunctionFactory.Create(InvokeAgentAsync, options);
}
```

**关键事实**：

- 默认 `AsAIFunction` 产生的函数**只有一个 `query: string` 参数**。
- 想加 `attachmentPaths` / `attachmentDescriptions` 等参数**不能通过 options 实现**；必须用 `AIFunctionFactory.Create` + 自定义委托：

  ```csharp
  async Task<string> InvokeAgentWithAttachmentsAsync(
      [Description("...")] string query,
      [Description("...")] string[]? attachmentPaths,
      [Description("...")] string[]? attachmentDescriptions,
      CancellationToken cancellationToken) { ... }

  var fn = AIFunctionFactory.Create(InvokeAgentWithAttachmentsAsync, options);
  ```

- **官方警告**：`AsAIFunction` 结果是有状态的（绑定到 `agent` 和可选 `session`），"避免并发使用同一 session"。项目侧 Concurrent 路径需要为每次调用单独创建子 Agent session。

---

## 8. AIContextProvider 契约

源码：`dotnet/src/Microsoft.Agents.AI.Abstractions/AIContextProvider.cs`

```csharp
public abstract class AIContextProvider
{
    public virtual IReadOnlyList<string> StateKeys => [this.GetType().Name];

    public ValueTask<AIContext> InvokingAsync(InvokingContext context, CancellationToken ct);

    protected virtual async ValueTask<AIContext> InvokingCoreAsync(InvokingContext context, ...);
    protected virtual ValueTask<AIContext> ProvideAIContextAsync(InvokingContext context, ...);
    // + Invoked / Store / Resume 系列
}

public sealed class AIContext
{
    public string? Instructions { get; set; }
    public IEnumerable<ChatMessage>? Messages { get; set; }
    public IEnumerable<AITool>? Tools { get; set; }
}
```

**关键事实**：

- `AIContext` 字段类型一定要清楚：`Instructions` 是字符串（合并时会 `a + "\n" + b`），`Messages` 是 `IEnumerable<ChatMessage>`，`Tools` 是 `IEnumerable<AITool>`。
- `InvokingCoreAsync` 默认行为：调用 `ProvideAIContextAsync` 得到补丁，再把补丁与输入合并；多个 Provider 是**串行合并**的，顺序敏感。
- **`StateKeys` 唯一性校验**：`ChatClientAgent` 构造时调用 `ValidateAndCollectStateKeys`，多个 Provider 的 StateKey 不能重名，否则抛异常。
  - 同类型多实例必须重写 `StateKeys`：

    ```csharp
    public override IReadOnlyList<string> StateKeys => [$"MySkillProvider_{_agentId}"];
    ```

- 项目侧 `SubAgentContextProvider` 和未来的 `OrchestrationInstructionsProvider`、`AgentBoundSkillsProvider` 等都受此约束。

---

## 9. AgentSkillsProvider（SDK 自带类型）

源码：`dotnet/src/Microsoft.Agents.AI/Skills/AgentSkillsProvider.cs`

```csharp
[Experimental("...")]
public sealed partial class AgentSkillsProvider : AIContextProvider
{
    public AgentSkillsProvider(string skillPath, ...);
    public AgentSkillsProvider(IEnumerable<string> skillPaths, ...);
    public AgentSkillsProvider(params AgentSkill[] skills);
    public AgentSkillsProvider(IEnumerable<AgentSkill> skills, ...);
    public AgentSkillsProvider(AgentSkillsSource source, ...);   // ← 可扩展点
}

public abstract class AgentSkillsSource { ... }   // 可继承
```

**关键事实**：

- `AgentSkillsProvider` 是 `sealed`，不能继承。
- 但 `AgentSkillsSource` 是 `abstract`，可以继承实现"按 Agent 过滤"的自定义 Source。
- 原文档 §10 风险 6 说"自定义 Provider 包一层" → **改为"自定义 `AgentSkillsSource` 子类，在 `GetSkillsAsync` 里做按 AgentId 过滤"**。

实现示例：

```csharp
internal sealed class AgentBoundSkillsSource(
    AgentSkillsSource inner,
    Func<AgentSkill, string?, bool> allowFor,
    Func<string?> currentAgentId)
    : AgentSkillsSource
{
    public override async IAsyncEnumerable<AgentSkill> GetSkillsAsync(...)
    {
        var agentId = currentAgentId();
        await foreach (var skill in inner.GetSkillsAsync(...))
        {
            if (allowFor(skill, agentId)) yield return skill;
        }
    }
}
```

---

## 10. ChatClientAgent 关键字段

源码：`dotnet/src/Microsoft.Agents.AI/ChatClient/ChatClientAgent.cs`、`ChatClientAgentOptions.cs`

```csharp
public sealed class ChatClientAgentOptions
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public string? Description { get; set; }
    public ChatOptions? ChatOptions { get; set; }
    public ChatHistoryProvider? ChatHistoryProvider { get; set; }
    public IEnumerable<AIContextProvider>? AIContextProviders { get; set; }
    public bool UseProvidedChatClientAsIs { get; set; }
    public bool ClearOnChatHistoryProviderConflict { get; set; } = true;
    public bool WarnOnChatHistoryProviderConflict { get; set; } = true;
    public bool ThrowOnChatHistoryProviderConflict { get; set; } = true;   // ← 默认 true
    public bool RequirePerServiceCallChatHistoryPersistence { get; set; }
    public bool EnableMessageInjection { get; set; }
}
```

**关键事实**：

- `ThrowOnChatHistoryProviderConflict` 默认 **true**：当底层服务自管历史（如 OpenAI Responses API 返回 `conversation_id`）时，仍配置 `ChatHistoryProvider` 会**直接抛异常**。
- 项目当前使用 ChatCompletions 路径，不受影响；但未来接入 Responses API 时必须显式设为 `false` 或移除 Provider。
- `Id`、`Name`、`Description` 三个字段都在 `ChatClientAgentOptions` 上，对应项目 §2.6 元数据约束修复点。

---

## 11. AIAgentHostExecutor.IdFor（重要约束）

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/Specialized/AIAgentHostExecutor.cs` + `AIAgentExtensions.cs`

```csharp
public static string IdFor(AIAgent agent) => agent.GetDescriptiveId();

public static string GetDescriptiveId(this AIAgent agent)
{
    string id = string.IsNullOrEmpty(agent.Name) ? agent.Id : $"{agent.Name}_{agent.Id}";
    return InvalidNameCharsRegex().Replace(id, "_");   // 非字母数字 → "_"
}
```

**关键事实**：

- Workflow 内每个 Agent 对应一个 Executor，Executor.Id = `{Name}_{Id}`（非法字符替换为 `_`）。
- **Agent.Name 和 Agent.Id 在 Workflow 生命周期内必须稳定**，否则 Magentic 内部 `team.FirstOrDefault(a => a.Name == nextSpeaker)` 会找不到目标 Agent。
- 项目侧 §2.6 的元数据统一规则（三个 Build 路径都设 Id/Name/Description）正是为此服务。

---

## 12. 并发执行环境

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/InProcessExecution.cs`

```csharp
public static class InProcessExecution
{
    public static InProcessExecutionEnvironment Default => OffThread;
    public static InProcessExecutionEnvironment OffThread { get; }
    public static InProcessExecutionEnvironment Concurrent { get; }  // ← 真正并发
    public static InProcessExecutionEnvironment Lockstep { get; }
}
```

**关键事实**：

- 默认 `Default = OffThread`：SuperSteps 在后台线程顺序执行，事件流式发出，但同一时刻只跑一个 SuperStep。
- `Concurrent`：允许多个 run 真正并发执行（用于"多个用户会话同时跑同一个 Workflow 模板"，不是 BuildConcurrent 内的并行 Agent）。
- 项目侧 §10 风险 1 中"真正并行 token 覆盖"对应的是 `InProcessExecution.Concurrent`；如果只用 `BuildConcurrent + OffThread`，多 Agent 并行是 Workflow 内部的 fan-out，调度由 SDK 保证。

---

## 13. AgentSession 跨 Agent 不可复用

源码：`dotnet/src/Microsoft.Agents.AI.Abstractions/AgentSession.cs`

> Because of these behaviors, an `AgentSession` may not be reusable across different agents, since each agent may add different behaviors to the `AgentSession` it creates.

**关键事实**：

- 这是 SDK **强约束**，不是项目限制。
- 推论：多智能体编排中绝对不能把主 Agent 的 session 直接传给子 Agent，必须为每个子 Agent 通过 `subAgent.CreateSessionAsync()` 创建独立 session。
- 项目当前 `BuildSubAgent(...)` 没有传递 session，符合此约束。

---

## 14. Run 与 ExternalResponse（HITL 入口）

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/Run.cs`

```csharp
public sealed class Run : CheckpointableRunBase, IAsyncDisposable
{
    public ValueTask<bool> ResumeAsync(IEnumerable<ExternalResponse> responses, CancellationToken ct);
    public ValueTask<bool> ResumeAsync<T>(CancellationToken ct, params IEnumerable<T> messages);
}
```

**关键事实**：

- Workflow 在等待外部输入（如 Magentic 计划批准、ToolApproval）时会发出 `RequestInfoEvent` 然后挂起。
- 项目侧需要订阅事件流，转 UI 提问，拿到用户答复后调用 `Run.ResumeAsync(responses)` 继续。
- 阶段 1-4 都**不实现 HITL**，因此 Magentic 必须 `.RequirePlanSignoff(false)`。

---

## 15. Experimental 诊断码与编译开关

源码：`dotnet/src/Microsoft.Agents.AI.Workflows/HandoffWorkflowBuilder.cs`

```csharp
internal static class DiagnosticConstants
{
    public const string ExperimentalFeatureDiagnostic = "MAAIW001";
}
```

涉及 `[Experimental("MAAIW001")]` 的关键类型：

- `MagenticWorkflowBuilder` 及其相关类
- `HandoffWorkflowBuilder` / `HandoffWorkflowBuilderCore<TBuilder>`
- 上述工厂方法 `AgentWorkflowBuilder.CreateHandoffBuilderWith`

项目侧引用时需要在 csproj 添加：

```xml
<NoWarn>$(NoWarn);MAAIW001;MAAI001</NoWarn>
```

或在使用处加 `#pragma warning disable MAAIW001`。`MAAI001` 是 `Microsoft.Agents.AI` 自身的 Experimental 诊断码（如 `AgentSkillsProvider`、`AIContextProvider.InvokingContext` 等）。

---

## 16. Compaction 体系（参考，不在第一阶段范围）

源码：`dotnet/src/Microsoft.Agents.AI/Compaction/`

可用策略：

| 类型 | 作用 |
| --- | --- |
| `ContextWindowCompactionStrategy` | 基于模型上下文窗口大小，自动两阶段压缩（tool 结果驱逐 + 截断） |
| `ToolResultCompactionStrategy` | 折叠旧 tool 调用组为摘要 |
| `TruncationCompactionStrategy` | 直接截断旧消息组 |
| `SummarizationCompactionStrategy` | 调用 LLM 总结旧消息 |
| `SlidingWindowCompactionStrategy` | 滑动窗口 |
| `PipelineCompactionStrategy` | 串联多个策略 |

**项目现状**：`ChatHistoryDataProvider.CompactAndReplaceAsync` 是自研压缩，未走 SDK 的 Compaction 管道。多智能体编排阶段不引入 SDK Compaction，避免与现有压缩冲突。

---

## 17. 对项目方案的关键结论

| 结论 | 出处 | 对实施的影响 |
| --- | --- | --- |
| Workflow 可直接 `.AsAIAgent()` | §6 | 阶段 2-3 改动量降为 S 级（壳子 + 包装） |
| Magentic 默认要 HITL | §3.1 | 第一阶段必须 `.RequirePlanSignoff(false)` |
| GroupChat 默认 40 轮 | §4 | 必须显式设置 `MaximumIterationCount = 3` |
| Magentic 单任务 ≥13 次 LLM | §3.2 | 阶段 5 接入前给出成本警告 |
| AsAIFunction 默认单参数 | §7 | 附件传递走自定义委托，不走默认 |
| AgentSkillsProvider sealed | §9 | 按 Agent 隔离技能走自定义 Source |
| AgentSession 跨 Agent 不可复用 | §13 | 子 Agent 必须独立 session |
| StateKeys 唯一性强约束 | §8 | 同类型多实例必须重写 StateKeys |
| Experimental 诊断 MAAIW001/MAAI001 | §15 | csproj NoWarn 需要配置 |
| Workflow 内部 session 与主 session 不共享 StateBag | §6 | 需要项目侧通过输入消息传递 traceid 等 |

---

## 18. 推荐阅读顺序

1. 先读本文档 §6（Workflow.AsAIAgent），它决定方案改动量。
2. 再读 [02-当前代码现状.md](./02-当前代码现状.md) 了解项目侧底座。
3. 然后读 [03-编排模式与边界约束.md](./03-编排模式与边界约束.md) 了解模式定位。
4. 实施时按 [04-实施阶段.md](./04-实施阶段.md) 逐步落地。
5. 风险参考 [05-风险与规避.md](./05-风险与规避.md)。
