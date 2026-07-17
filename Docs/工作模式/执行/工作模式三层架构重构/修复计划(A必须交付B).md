# 修复计划：A 必须把执行交给 B（D1 决策的代码层落地）

> 状态：**已完成（端到端验证通过 / 25 步任务回归通过 / 已归档）**（5 commit 第 2 版修订）
> 日期：2026-06-08
> 父文档：[工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md) §十.D1
> 触发：测试时 25 步任务在第 13 步停止，根因详见 §一/§二

---

## 一、问题陈述

测试场景：用户让总经理（A）执行 25 步的电商平台调研任务。

**实际行为**：A 用一次 `RunStreamingAsync` 在 11 分钟内连续调用 67 次工具，跑完 13 步后模型流自然结束，任务停留在 `IsActive=1, OrchestratorState=NULL, IsOrphaned=1` 的僵尸态。

**关键事实**（基于数据库 `madorin.db` 任务 `9828...0037e` 的完整证据链）：

- A 一次 `RunStreamingAsync` 内做了 67 次工具调用 + 13 次 StepStart
- 模型最后一段 assistant 文本写到"继续步骤 13：抖音开放平台-抖店"后流结束（推测 `finish_reason=length`）
- B（ProjectLeadService）从未启动：`OrchestratorState=NULL, OrchestratorHeartbeatAt=NULL`
- A 走的是"自己执行"路径，B 走的是"读 plan.yaml 调度 C"路径——两套并存的代码

---

## 二、根本原因

代码核查（修复前）后明确：**A 拥有完整的"自执行"能力**，与三层架构方案 §2.2 边界规定（"只有 B 调度 C，A 不直接 dispatch"）冲突。

具体表现：

1. **A 工具集含执行类工具**：`DispatchTools` / `VerifyTools` / `SelfEvaluationTools` / `dispatch_parallel` / `start_subagent_*` 等仍在 [WorkModeToolset.cs:112-126](../../../Src/Netor.Cortana.AI/WorkMode/Tools/WorkModeToolset.cs) 中注入给 A
2. **A 的 BoundPlugins/BoundMcp 携带业务工具**：[GeneralManagerAgentBuilder.cs:164-165](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs) 的 `CloneWithGmPromptAsync` 复制了原 agent 的 `BoundPlugins` 和 `BoundMcp`；**即使把 clone 上的两个数组清空也无效**——[AIAgentFactory.cs:851-859](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 的 `GetAgentBoundTools` 会按 `agent.Id` 反查文件版 manifest，把原 manifest 上的 `BoundPlugins/BoundMcp` 重新覆盖回来
3. **AIAgentFactory 仍会注入全局 AIContextProvider 写类工具**：即便能屏蔽 BoundPlugins，AIAgentFactory 仍会拼接全局 `AIContextProvider`（`WindowToolProvider` / `AiConfigToolProvider` / `PluginManagementProvider` 等含 `sys_set_*` / `sys_add_*` / `sys_delete_*` 写工具），A 仍可绕道做副作用
4. **A 通过 mentions 持有子智能体工具**：[GeneralManagerAgentBuilder.cs:141](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs) 把 `mentions` 透传给 [AIAgentFactory.cs:292-323](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 的 `BuildWithSubAgents`，后者按 mention 注入 `agent_xxx` AIFunction 工具。A 拿到这些 = 直接调 C，绕过 B
5. **set_plan 与 finalize_plan 用两套不同 schema**：set_plan 写 `WorkTasks.CurrentPlanJson`（main_steps/sub_steps），finalize_plan 接收 planJson 写 `plan.yaml`（steps/role/input），中间无桥接
6. **finalize_plan 不检查用户确认**：[ProjectLeadTools.cs:24-55](../../../Src/Netor.Cortana.AI/WorkMode/Tools/ProjectLeadTools.cs) 直接写文件 + 启动 B，绕过 set_plan 设置的 `PendingRequestKind="plan_confirmation"` 守门，也无幂等保护
7. **B 完成后未闭合 IsActive=0**：[ProjectLeadService.cs](../../../Src/Netor.Cortana.AI/WorkMode/ProjectLead/ProjectLeadService.cs) 完成路径有**两条**——while 循环兜底（行 72-80）+ RunStepAsync 内最后一步（行 184-191），均只设 `OrchestratorState=Done`，未调 `MarkCompleted`，导致任务永远是 `IsActive=1` 的僵尸状态；失败路径同样有两条（行 62-69 + 行 198-206），均缺 `MarkFailed`
8. **final_report 与 MarkCompleted 互冲**：修复前 `final_report` 工具实现可能改任务状态/写库，B 调 `MarkCompleted` 后 A 再调 `final_report` 会双写或反复闭合

**架构层结论**：D1 决策的边界（A 不持有执行能力）只在文档层确立，代码层并未强制。模型偏置自然选择"我有工具，那就自己干"——这就是 25 步跑 13 步停止的真正原因。

---

## 三、修复策略：让 A "无路可走"，只能交付 B

不靠提示词软劝告，不靠运行时守门，不靠软停止。**直接拔掉 A 的执行能力**——四道入口一起堵：

1. **WorkModeToolset 注册面**：删掉 dispatch_step / verify_step / self_evaluate / subagent_* 等执行类工具
2. **AgentEntity 的 BoundPlugins/BoundMcp 读取面**：在 GM build 路径告诉 `AIAgentFactory` 跳过文件 manifest 反查（否则 clone 清空无效）
3. **AIContextProvider 写工具注入面**：用新增的 `ToolFilterMode.WorkModeManager` 档在过滤链末尾做白名单过滤
4. **mentions → agent_xxx 注入面**：A 的 build 不再走 `BuildWithSubAgents`，改走纯 `Build`（不传 mentions）

A 的工具集化简后只剩：

- 规划类：`set_plan` / `update_plan` / `set_environment`
- 交付类：`finalize_plan` / `pause_orchestrator` / `resume_orchestrator` / `cancel_orchestrator`
- 模板类：`list_plan_templates` / `load_plan_from_template` / `save_current_plan_as_template` / `load_plan_from_chat_history` / `load_plan_from_groupchat` / `get_recent_completed_task_plan`
- 对话类：`ask_user` / `check_pending_user_input` / `final_report` / `cancel_task`
- 只读系统类：`sys_get_*` / `sys_list_*`（A 需要查询当前应用/插件状态才能回答用户问题）

A 没有任何写类副作用工具、没有 `agent_xxx` 子智能体调用工具、没有 BoundPlugins 业务工具。

加上 `finalize_plan` 四条守门 + B 两条完成路径都调 `MarkCompleted` + `final_report` 重定义为纯输出工具，把"漏洞"全部堵上。

---

## 四、能解决根本问题吗

把 25 步调研任务在修复后重新推演：

```text
1. 用户："帮我做 25 步电商平台调研"
2. A 与用户对话理解需求
3. A 调 set_plan（写 plan.yaml 草稿；同时把 main_steps/sub_steps 版本写入 CurrentPlanJson 作兼容缓存；
                  plan.Status=Planning，PendingRequestKind=plan_confirmation）
4. A 回复用户："计划如下：1...25。是否开始？"
5. 用户："开始" → 用户输入消化 PendingRequest，清空 PendingRequestKind
6. A 调 finalize_plan
   ├─ 守门 1：plan.yaml 存在 → 通过
   ├─ 守门 2：plan.Status == Planning → 通过
   ├─ 守门 3：PendingRequestKind != plan_confirmation → 通过（用户已确认）
   ├─ 守门 4：OrchestratorState 不是 Running/Done/Failed → 通过
   ├─ plan.Status = Running, OrchestratorState = Running
   ├─ StartProjectLeadInBackground（B 后台启动）
   └─ 返回 "计划已交付项目组长执行"
7. A 收到返回 → 想做下一步？
   - 没有 dispatch_step / agent_xxx 等执行工具（commit 1 + commit 2.4）
   - 没有 BoundPlugins 业务工具（commit 2.2）
   - 没有 sys_set_* / sys_add_* 等写类工具（commit 2.3 白名单过滤）
   → 模型只能输出文本
8. A 输出 "已交付项目组长执行" → 流自然结束
9. WorkflowExecutor.ExecuteAsync return（A 这一轮结束）
10. ↓ B 在后台跑：
    ├─ 找下一个 pending step → DispatchAsync(role, input) 调 C
    ├─ C 是文件版 Agent，带 BoundPlugins/BoundMcp（业务工具）执行
    ├─ C 完成返回 summary → B 写 plan.step.status=done
    ├─ B 心跳更新 OrchestratorHeartbeatAt
    └─ 发 OnWorkStepCompleted 事件 → UI 实时刷新
11. 25 步全部完成
12. plan.Status = Done, OrchestratorState = Done
13. B 在两条完成路径**任意一条** 都调 MarkCompleted(taskId)
    → IsActive=0, CompletedAt!=NULL, FinalReport=BuildSummary(plan)
14. 任务正常结束
15. （可选）下次用户激活 A 询问结果 → A 调 final_report（纯只读：返回 plan + summary 文本，不改任何状态）
```

**所有逃逸路径都被堵住**：

| 逃逸路径 | 是否被堵 |
| --- | --- |
| A 模型试图调 dispatch_step / verify_step | ✅ 工具不存在（commit 1） |
| A 模型试图调 sys_write_file 等 BoundPlugins 业务工具 | ✅ AgentBoundTools 反查被关掉（commit 2.1） |
| A 模型试图调 sys_set_window / sys_add_plugin 等全局写工具 | ✅ ToolFilterMode.WorkModeManager 白名单过滤（commit 2.3） |
| A 模型试图调 agent_xxx 子智能体（mention 注入） | ✅ A 走 Build 而非 BuildWithSubAgents（commit 2.4） |
| A 跳过 set_plan 直接调 finalize_plan | ✅ 守门 1 拒绝（plan.yaml 不存在） |
| A 调 set_plan 后用户没确认就调 finalize_plan | ✅ 守门 3 拒绝（PendingRequestKind 仍是 plan_confirmation） |
| A 调 finalize_plan 多次 | ✅ 守门 2 拒绝（plan.Status != Planning）+ 守门 4 拒绝（OrchestratorState 已是 Running/Done） |
| A 流结束后无人续跑 | ✅ B 已在后台跑，与 A 流解耦 |
| B 完成但 IsActive=1（路径 A：while 兜底） | ✅ MarkCompleted 在路径 A 闭合 |
| B 完成但 IsActive=1（路径 B：RunStepAsync 内最后一步） | ✅ MarkCompleted 在路径 B 也闭合 |
| A 完成后 final_report 二次写库导致状态错乱 | ✅ final_report 改为纯输出，不改状态（commit 5） |

---

## 五、实施步骤（5 个 commit）

每个 commit 独立可上线、独立可验证、独立可回滚。**commit 1 + commit 2 单独上线就能解决"25 步跑 13 步停"的核心问题**，commit 3-5 是把方案做完整。

> **顺序原则**：先动 A 的能力面（commit 1+2），再动协议（commit 3），再加守门（commit 4），最后闭合 B 与 A 的输出（commit 5）。
> prompt 重写必须与工具删除同 commit 上线（commit 1）——否则 prompt 教 A 调 dispatch_step 时工具不存在，模型会反复尝试不存在的工具。

### commit 1：WorkModeToolset 移除 A 的执行类工具 + 重写 A 的 prompt

**目标**：工具面与提示面同时收紧。从 A 的工具集移除所有让 A 能"自己执行"的工具；同时改 prompt 把语义边界对齐到新的工具集，避免模型尝试调用不存在的工具。

**改造点 1**：[WorkModeToolset.cs:112-126](../../../Src/Netor.Cortana.AI/WorkMode/Tools/WorkModeToolset.cs) 删除以下工具的注册：

- `DispatchTools.CreateDispatchStepTool()` 及相关 `dispatch_parallel` / `finish_parallel_block` / `dispatch_nested_workflow` / `finish_nested_workflow`
- `VerifyTools.CreateVerifyStepTool()` / `CreateSendBackTool()`
- `SelfEvaluationTools.CreateSelfEvaluateTool()`
- `SubAgentBackgroundTools` 相关注册（`start_subagent_*` / `wait_for_subagent` / `cancel_subagent`）

工具类（DispatchTools / VerifyTools 等 .cs 文件）本身**保留**——这次只动注册，不动类定义，避免 PR 太大。后续如果确认零引用，再单独 PR 删除类。

**改造点 2**：prompt 文件 [Src/Netor.Cortana.AI/prompts/work_mode/general_manager.md](../../../Src/Netor.Cortana.AI/prompts/work_mode/general_manager.md)（嵌入资源源文件）；如果 `.codex/ui-build/prompts/work_mode/general_manager.md` 存在（本地覆盖），同步更新。

prompt 关键改动：

```markdown
你是项目总经理。

## 你的职责（且仅限于此）
- 与用户对话，理解需求
- 制定计划：set_plan
- 调整计划：update_plan
- 设置执行环境：set_environment
- 把计划交给项目组长：finalize_plan
- 接受用户中途打断：pause_orchestrator / resume_orchestrator / cancel_orchestrator
- 任务完成后向用户汇报：final_report（只读取计划与各步骤 summary，不改状态）

## 你不做的事（重要）
- 你**不持有任何业务工具**——你的工具集里没有 sys_write_file、sys_make_directory、dispatch_step、verify_step、agent_xxx 等
- 你**不直接执行任何工作**——执行是项目组长（B）和专员（C）的事
- 你**不绕过用户确认**——set_plan 后必须等用户明确确认（"开始/同意/可以"）才能调 finalize_plan

## 标准工作流（强约束）
1. 用户提需求 → 你充分对话理解
2. 调 set_plan 制定完整计划 → 计划写入 plan.yaml 草稿
3. 用人话向用户展示计划要点，问"是否开始执行"
4. 等用户确认（"开始/同意/可以/执行"等明确肯定词）
5. 调 finalize_plan → 计划交付项目组长执行（finalize_plan 不再需要再传一遍计划内容）
6. finalize_plan 返回成功后，回复用户"已交付项目组长执行"，本轮你的工作就完成了
7. 等用户下次问进度 / 改需求时再激活你

## 任务预置场景（handoff / 模板加载后用户已确认）
进入任务时**先看是否已经存在 plan.yaml**：
- 如果 plan.yaml 已存在 且 PendingRequestKind ≠ plan_confirmation
  → 这表示计划已由 handoff（聊天/会议中用户已说"开始执行"）或模板路径预置好且用户已确认
  → **直接调 finalize_plan**，不要回头再要求用户确认
- 如果 plan.yaml 已存在 但 PendingRequestKind == plan_confirmation
  → 计划已草拟但用户还没确认（例如刚走完 load_plan_from_template）
  → 用人话展示计划要点，问用户"是否开始执行"，等确认后再 finalize_plan
- 如果 plan.yaml 不存在
  → 走标准工作流第 1-6 步

## 永远不要做
- 不要在 set_plan 之后直接调 finalize_plan——必须等用户确认（除非进入时 plan.yaml 已预置且 PendingRequestKind 为空）
- 不要在 finalize_plan 之后试图继续做什么——本轮你已无事可做
- 不要在一次回复里既制定计划又"自己执行 N 步"——你的工具集里没有执行工具
- 用户问简单问题（如"今天天气怎样"），直接回答即可，不必制定计划

## 用户中途打断
- 用户改需求 → 调 update_plan
- 用户暂停 → 调 pause_orchestrator
- 用户恢复 → 调 resume_orchestrator
- 用户取消 → 调 cancel_orchestrator
```

**验证**：

- 编译通过
- `dotnet test` 通过
- 启动应用 → 工作模式 → 看 A 的工具列表只剩规划/对话/编排控制 ~14 个
- A 在测试任务中**不应再出现** `dispatch_step` 工具调用日志
- A 的 prompt 模板替换生效，不再含"自己执行"类指令
- A 的 ContextMessages 中 assistant 消息**不再含**"步骤 1 完成"、"继续步骤 N"等执行性文字

### commit 2：GeneralManagerAgent 工具范围收缩（四个改造点合并上线）

**目标**：让 A 在工作模式下没有任何业务工具、没有写类系统工具、没有子智能体调用工具。这是最关键的一步，**必须四个改造点同 commit 上线**——任何单点漏过都会让 A 还有路可走。

#### 改造点 2.1：AIAgentFactory.Build 增加 `skipAgentBoundTools` 参数

[AIAgentFactory.cs:851-859](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 的 `GetAgentBoundTools` 默认按 `agent.Id` 反查文件版 manifest 拿 `BoundPlugins/BoundMcp`，所以光在 GM clone 上把 `BoundPlugins=[]` 设空不起作用。

`AIAgentFactory.Build`（以及 `BuildWithSubAgents`）增加可选参数：

```csharp
public AIAgent Build(
    AgentEntity agent,
    AiProviderEntity provider,
    AiModelEntity model,
    IReadOnlyList<AIFunction>? additionalTools = null,
    bool enableChatHistory = true,
    ToolFilterMode toolFilterMode = ToolFilterMode.Full,
    bool skipAgentBoundTools = false);   // 新增
```

`GetAgentBoundTools` 当 `skipAgentBoundTools=true` 时直接返回 `(agent.BoundPlugins, agent.BoundMcp)`（即跳过文件 manifest 反查；GM clone 已经把这两个清空，效果就是 A 完全没有 BoundPlugins/BoundMcp）：

```csharp
private (IReadOnlyList<string> Plugins, IReadOnlyList<string> McpServers) GetAgentBoundTools(
    AgentEntity agent, bool skipFileManifest = false)
{
    if (skipFileManifest)
    {
        return (agent.BoundPlugins, agent.BoundMcp);
    }
    var fileService = services.GetService<AgentFileService>();
    var record = !string.IsNullOrWhiteSpace(agent.Id) ? fileService?.GetByName(agent.Id) : null;
    return record is null
        ? (agent.BoundPlugins, agent.BoundMcp)
        : (record.Manifest.BoundPlugins, record.Manifest.BoundMcp);
}
```

#### 改造点 2.2：GeneralManagerAgentBuilder 清空 BoundPlugins/BoundMcp

[GeneralManagerAgentBuilder.cs:151-170](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs) 的 `CloneWithGmPromptAsync`：

```csharp
// 改造前
BoundPlugins = [.. source.BoundPlugins],
BoundMcp = [.. source.BoundMcp],

// 改造后
BoundPlugins = [],
BoundMcp = [],
```

这条单独不够（被文件 manifest 反查覆盖），必须配合 2.1 的 `skipAgentBoundTools=true`。

#### 改造点 2.3：新增 ToolFilterMode.WorkModeManager

不新增 `Func<string,bool>` 参数；复用 [ToolFilterMode.cs](../../../Src/Netor.Cortana.AI/ToolFilterMode.cs) + [ToolFilteringContextProvider.cs](../../../Src/Netor.Cortana.AI/Providers/ToolFilteringContextProvider.cs) 现有过滤链。

`ToolFilterMode` 新增一档：

```csharp
public enum ToolFilterMode
{
    Full,
    ReadOnly,
    None,
    WorkModeManager,   // 新增：工作模式 A 专用白名单
}
```

`ToolFilter.Apply` 增加对应分支。`WorkModeManager` 规则 = A 控制工具白名单 + 只读系统工具：

```csharp
private static readonly HashSet<string> WorkModeManagerToolNames = new(StringComparer.OrdinalIgnoreCase)
{
    // 规划
    "set_plan", "update_plan", "set_environment",
    // 交付
    "finalize_plan", "pause_orchestrator", "resume_orchestrator", "cancel_orchestrator",
    // 模板
    "list_plan_templates", "load_plan_from_template", "save_current_plan_as_template",
    "load_plan_from_chat_history", "load_plan_from_groupchat", "get_recent_completed_task_plan",
    // 对话
    "ask_user", "check_pending_user_input", "final_report", "cancel_task",
};

// 在 Apply 里
if (mode == ToolFilterMode.WorkModeManager)
{
    var filtered = new List<AITool>();
    foreach (var tool in tools)
    {
        var name = tool.Name ?? string.Empty;
        if (WorkModeManagerToolNames.Contains(name)
            || name.StartsWith("sys_get_", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("sys_list_", StringComparison.OrdinalIgnoreCase))
        {
            filtered.Add(tool);
        }
    }
    return filtered;
}
```

为什么不直接复用 `ReadOnly`：[ToolFilterMode.cs:35-41](../../../Src/Netor.Cortana.AI/ToolFilterMode.cs#L35-L41) 的 `MutableNamePrefixes` 含 `set_` / `cancel_` / `update_` 等，会把 A 必需的 `set_plan` / `set_environment` / `update_plan` / `cancel_orchestrator` / `cancel_task` 也屏蔽掉。

#### 改造点 2.4：GeneralManagerAgent 不走 BuildWithSubAgents + B dispatcher 优先匹配 MentionsJson

**两个动作必须同 commit 上线**，否则会出现"A 看不到 @ 专员、B 也不知道用户 @ 过谁"的真空。

**动作 1**：`BuildWithSubAgentsAsync` 内部不再调 `_factory.BuildWithSubAgents`；改调 `_factory.Build`（不传 mentions）。

[GeneralManagerAgentBuilder.cs:100-148](../../../Src/Netor.Cortana.AI/WorkMode/GeneralManagerAgentBuilder.cs) 当前 `BuildWithSubAgentsAsync` 把 mentions 透传给 `_factory.BuildWithSubAgents`，后者在 [AIAgentFactory.cs:286-323](../../../Src/Netor.Cortana.AI/AIAgentFactory.cs) 按每个 mention 注入一个 `agent_xxx` AIFunction 工具。A 拿到这些 = 直接调 C，绕过 B。

**动作 2**：`ProjectStepDispatcher.DispatchAsync` 在选 Agent 时**优先从 `WorkTasks.MentionsJson` 匹配**，匹配不到再全局查。

[ProjectStepDispatcher.cs:36](../../../Src/Netor.Cortana.AI/WorkMode/ProjectLead/ProjectStepDispatcher.cs#L36) 当前是 `agentService.FindByNameOrDisplayName(step.Role)` —— **完全不看 MentionsJson**。配合动作 1 之后，A 拿不到 `agent_xxx` 工具 = A 没法主动选 mention 里的特定专员，B 又不知道用户 @ 过谁，用户 @ 指定专员的语义就彻底丢了。`WorkTasks.MentionsJson` 字段已经存在（[WorkTaskEntity.cs:68](../../../Src/Netor.Cortana.Entitys/Entities/WorkTaskEntity.cs#L68)），由 [WorkHandoffTools.cs:176](../../../Src/Netor.Cortana.AI/Handoff/WorkHandoffTools.cs#L176) 和 [WorkModeInputVm.cs:390](../../../Src/Netor.Cortana.UI/ViewModels/WorkMode/WorkModeInputVm.cs#L390) 写入，B 只需要读它。

dispatcher 改造伪代码：

```csharp
public async Task<string> DispatchAsync(string taskId, WorkTaskPlanStepFile step, ...)
{
    ...
    var task = taskService.GetById(taskId);

    // 先在 MentionsJson 里按 id/name 匹配（用户 @ 指定的专员优先）
    AgentEntity? agent = null;
    if (!string.IsNullOrWhiteSpace(task.MentionsJson))
    {
        var mentions = JsonSerializer.Deserialize<List<AgentMentionDto>>(task.MentionsJson, ...);
        var hit = mentions?.FirstOrDefault(m =>
            string.Equals(m.AgentId, step.Role, StringComparison.OrdinalIgnoreCase)
            || string.Equals(m.AgentName, step.Role, StringComparison.OrdinalIgnoreCase));
        if (hit is not null)
        {
            agent = agentService.GetByName(hit.AgentId);
        }
    }

    // 匹配不到再全局查
    agent ??= agentService.FindByNameOrDisplayName(step.Role)
        ?? throw new InvalidOperationException($"未找到步骤角色对应的智能体：{step.Role}");
    ...
}
```

> **为什么不能延后**：原本想着"先把 A 的 agent_xxx 拔掉，mentions 存哪儿后续 PR 再说"。问题是 commit 2.4 上线后：
>
> - A 不再有 agent_xxx 工具 → A 无法主动调用 mention 里的特定专员
> - B 仍然 `FindByNameOrDisplayName` 全局查 → 同名多版本时随机命中
> - 用户在聊天里 @ 一个特定版本的"novel-writer-v2"，结果 B 派给"novel-writer-v1" → 业务掉链子
>
> 对"25 步调研"原 bug 不致命（plan.role 通常都是普通 role 名），但对用户用 @ 指定专员的业务必坏。所以**必须 commit 2.4 同步做掉**。

#### 改造点 2.5：调用点串起来

`GeneralManagerAgentBuilder.BuildAsync` / `BuildWithSubAgentsAsync` 调 `_factory.Build` 时一律传：

```csharp
_factory.Build(
    gmAgent, provider, model, workModeTools,
    enableChatHistory: false,
    toolFilterMode: ToolFilterMode.WorkModeManager,
    skipAgentBoundTools: true);
```

#### 影响范围确认

四个改造点只影响**工作模式 A 的 build 路径**：

- `ToolFilterMode.WorkModeManager` 是新枚举值，不影响 Full/ReadOnly/None
- `skipAgentBoundTools` 是新参数，默认 false，不影响现有调用
- `_factory.Build` vs `_factory.BuildWithSubAgents` 选择只在 `GeneralManagerAgentBuilder` 内部切换
- 聊天模式、会议模式、C 层文件 Agent 全部不受影响

**验证**：

- 编译通过
- `dotnet test` 通过
- 启动应用 → 工作模式 → 重跑 25 步调研任务
- 期望：A 在制定计划后**无业务工具可调**，模型流在 set_plan + finalize_plan 后短促结束
- 数据库观察：`OrchestratorState=Running`，B 在后台跑 → `OrchestratorHeartbeatAt` 持续更新
- 对比：之前 A 的一轮里 67 次工具调用，修复后 A 的一轮里 ≤5 次工具调用
- 在 A 的工具列表里**搜不到** `sys_write_file` / `sys_set_window_*` / `sys_add_plugin` / `dispatch_step` / `agent_xxx` 等
- A 的工具列表里**仍能看到** `sys_list_windows` / `sys_get_ai_config` 等只读工具（A 需要这些回答用户问题）

### commit 3：set_plan 双写（plan.yaml canonical + CurrentPlanJson 兼容缓存）；finalize_plan 不再接收 planJson

**目标**：消除两套 schema 并存的混乱。set_plan 写 plan.yaml 作为 canonical 数据源，同时把老 schema 同步写到 `CurrentPlanJson` 保持兼容；finalize_plan 退化为"启动开关"，不再接 planJson 参数。

> **关键设计选择**：**不删除 `CurrentPlanJson` 字段**。grep 确认还有 6 处非废弃读取方在用：UI 历史展示（[WorkModeView.axaml.cs:135](../../../Src/Netor.Cortana.UI/Controls/WorkMode/WorkModeView.axaml.cs#L135)）、模板保存（[PlanTemplateTools.cs:162-170](../../../Src/Netor.Cortana.AI/WorkMode/Tools/PlanTemplateTools.cs)）、最近任务复用（[RecentTaskTools.cs:42-50](../../../Src/Netor.Cortana.AI/WorkMode/Tools/RecentTaskTools.cs)）、handoff（[WorkHandoffTools.cs:74](../../../Src/Netor.Cortana.AI/Handoff/WorkHandoffTools.cs)）、标题生成（[WorkTaskTitleService.cs:120](../../../Src/Netor.Cortana.AI/WorkMode/WorkTaskTitleService.cs)）、set_plan 自身（[PlanTools.cs:133-137](../../../Src/Netor.Cortana.AI/WorkMode/Tools/PlanTools.cs)）。本 commit 删字段会带 6 处编译断裂 + UI 空白。**字段删除单列在 §七 后续清理**。

#### 改造点 3.1：set_plan 改写 plan.yaml + 同步写 CurrentPlanJson

[PlanTools.cs](../../../Src/Netor.Cortana.AI/WorkMode/Tools/PlanTools.cs) 的 `set_plan` 工具：

- 工具入参 schema 改为新 schema（`steps/id/title/role/input/...`，与 plan.yaml 一致；由 [WorkTaskPlanStepJsonParser](../../../Src/Netor.Cortana.AI/WorkMode/Files/WorkTaskPlanStepJsonParser.cs) 解析）
- 内部行为：
  1. 调 [WorkTaskFileService](../../../Src/Netor.Cortana.AI/WorkMode/Files/WorkTaskFileService.cs) 写 `plan.yaml`，`plan.Status = Planning`（canonical 数据源）
  2. 同步把 plan.yaml 转成老 schema（main_steps/sub_steps）写到 `WorkTasks.CurrentPlanJson`（兼容缓存）
  3. 仍设置 `PendingRequestKind = "plan_confirmation"`

新增一个 `WorkPlanLegacyConverter` 帮助类，专司"plan.yaml ↔ CurrentPlanJson 老 schema"双向转换。读端继续读 CurrentPlanJson，等下个 PR 逐个迁到 plan.yaml 后再删字段。

#### 改造点 3.2：finalize_plan 去掉 planJson 参数

[ProjectLeadTools.cs](../../../Src/Netor.Cortana.AI/WorkMode/Tools/ProjectLeadTools.cs) 的 `FinalizePlan`：

- **签名变更**：移除 `planJson` / `plan` 等接收计划内容的参数；只保留 `taskId`（如果当前依赖隐式上下文则也保留隐式获取）
- 内部不再做"解析 planJson → 写 plan.yaml"步骤——plan.yaml 已由 set_plan 写好
- 只做：守门检查（commit 4） → plan.Status: Planning → Running → OrchestratorState=Running → 启动 B → 返回"已交付项目组长执行"

这样 A 的提示词也对齐："set_plan 写计划，finalize_plan 只是启动开关"，模型不会再纠结要不要在 finalize_plan 里再传一遍计划。

#### 改造点 3.3：update_plan 同步双写

`update_plan`：草稿态时改 plan.yaml 草稿 + 同步更新 CurrentPlanJson；运行态时只能改未完成 step + 同步 CurrentPlanJson。

#### 改造点 3.4：handoff 老 schema 路径桥接

[WorkHandoffTools.cs:74](../../../Src/Netor.Cortana.AI/Handoff/WorkHandoffTools.cs) 接收的 `planJson`（main_steps/sub_steps）这条路径**不动**——它走的是"从聊天/会议 handoff 到工作模式时跳过 set_plan 直接派发"。本 commit 让它写 CurrentPlanJson 之后，**也同步生成一份 plan.yaml**（用同一个 `WorkPlanLegacyConverter` 反向转）。这样 A 进入任务后无论是否经过 set_plan，plan.yaml 都存在，commit 4 的"plan.yaml 存在"守门才不会误伤 handoff 任务。

> handoff 不设 `PendingRequestKind=plan_confirmation`（用户在聊天/会议已说"开始执行"），所以 commit 4 守门 3 自然通过。配合 commit 1 prompt 的"任务预置场景"放行口，A 进任务后会直接 finalize_plan。

#### 改造点 3.5：load_plan_from_template 同步写 plan.yaml

[PlanTemplateTools.cs:126-129](../../../Src/Netor.Cortana.AI/WorkMode/Tools/PlanTemplateTools.cs#L126-L129) 当前 `LoadPlanFromTemplateAsync` 只写 `CurrentPlanJson` + 设 `PendingRequestKind=plan_confirmation`，没写 plan.yaml。用户走"加载模板 → 确认 → finalize_plan"路径会被 commit 4 守门 1（plan.yaml 不存在）拒绝。

改造：

```csharp
// 改造前
_taskService.UpdatePlan(_taskId, planJson);
_templateService.IncrementUseCount(template.Id);
await _publisher.PublishAsync(Events.OnWorkPlanUpdated, new WorkPlanUpdatedArgs(_taskId, planJson));
await PlanTools.RequestPlanConfirmationAsync(_taskService, _publisher, _taskId);

// 改造后（写 CurrentPlanJson 保留兼容，同时反向转生成 plan.yaml）
_taskService.UpdatePlan(_taskId, planJson);   // 保留 CurrentPlanJson 兼容路径
var planYaml = WorkPlanLegacyConverter.LegacyJsonToPlanFile(_taskId, planJson);
_fileService.SavePlan(planYaml);              // 新增：同步写 plan.yaml
_templateService.IncrementUseCount(template.Id);
await _publisher.PublishAsync(Events.OnWorkPlanUpdated, new WorkPlanUpdatedArgs(_taskId, planJson));
await PlanTools.RequestPlanConfirmationAsync(_taskService, _publisher, _taskId);
```

`PlanTemplateTools` 构造函数需要注入 `WorkTaskFileService`（DI 中已注册，加构造参数即可）。

> **其他同类入口已核查过不漏**：`load_plan_from_chat_history` / `load_plan_from_groupchat`（[ChatHistoryPlanTools.cs](../../../Src/Netor.Cortana.AI/WorkMode/Tools/ChatHistoryPlanTools.cs)）和 `get_recent_completed_task_plan`（[RecentTaskTools.cs](../../../Src/Netor.Cortana.AI/WorkMode/Tools/RecentTaskTools.cs)）只返回参考材料字符串，A 之后还要自己调 set_plan 才落计划——计划落地由 set_plan 统一双写（改造点 3.1）。`save_current_plan_as_template` 只读 CurrentPlanJson 存模板，也不需要改。

**验证**：

- 编译通过
- `dotnet test` 通过
- set_plan 调用后：plan.yaml 文件存在，`plan.Status=Planning`，`PendingRequestKind=plan_confirmation`，`CurrentPlanJson` 也同步有值
- 数据库 `WorkTasks.CurrentPlanJson` 字段仍存在（不删）
- finalize_plan 工具 schema 中**不再有** planJson 参数（模型看到的工具描述里也对齐）
- A 的工作流跑通：set_plan → 用户确认 → finalize_plan（无 planJson 参数）→ B 启动
- handoff 从聊天进入工作模式：plan.yaml + CurrentPlanJson 都存在
- UI 历史展示、模板保存、最近任务复用、标题生成都**不受影响**（继续读 CurrentPlanJson）

### commit 4：finalize_plan 四条守门

**目标**：finalize_plan 不再"无守门直接启动 B"，必须满足四条状态契约才能放行。

**前置改造**：[WorkTaskFileService.cs](../../../Src/Netor.Cortana.AI/WorkMode/Files/WorkTaskFileService.cs) 当前没有 `GetPlanPath`。补一个公开方法，顺道把 `LoadPlan` 行 35 的内联 `Path.Combine` 也替换掉：

```csharp
public string GetPlanPath(string taskId) => Path.Combine(GetTaskDirectory(taskId), PlanFileName);

public WorkTaskPlanFile? LoadPlan(string taskId)
{
    var path = GetPlanPath(taskId);
    return File.Exists(path) ? WorkTaskPlanFileYaml.Deserialize(File.ReadAllText(path)) : null;
}
```

环境文件同理可加 `GetEnvironmentPath`（顺手项，可选）。

**改造点**：[ProjectLeadTools.cs](../../../Src/Netor.Cortana.AI/WorkMode/Tools/ProjectLeadTools.cs) 的 `FinalizePlan` 方法入口加四条守门，全部通过才允许启动 B：

```csharp
// 守门 1：plan.yaml 必须存在（防止跳过 set_plan / handoff 直接 finalize）
if (!File.Exists(fileService.GetPlanPath(taskId)))
    return "错误：尚未制定计划。请先调用 set_plan 写入计划草稿。";

var plan = fileService.LoadPlan(taskId);

// 守门 2：plan.Status 必须是 Planning（防止重复 finalize；运行/完成/失败态不允许再次启动）
if (!string.Equals(plan.Status, WorkTaskPlanStatuses.Planning, StringComparison.Ordinal))
    return $"错误：计划当前状态是 {plan.Status}，不能 finalize。" +
           $" 如需修改计划请用 update_plan；如需重新启动请新建任务。";

// 守门 3：PendingRequestKind 必须不是 plan_confirmation（即用户已确认；
//        用户确认通过用户输入流程自然清掉 PendingRequest）
var task = taskService?.GetById(taskId);
if (task is null)
    return "错误：任务不存在";

if (string.Equals(task.PendingRequestKind, "plan_confirmation", StringComparison.Ordinal))
    return "错误：计划尚未经过用户确认。请先与用户确认计划，确认后再调用 finalize_plan。";

// 守门 4：OrchestratorState 必须不是 Running/Done/Failed（幂等保护，避免并发或重复启动 B）
if (string.Equals(task.OrchestratorState, WorkTaskOrchestratorStates.Running, StringComparison.Ordinal))
    return "错误：项目组长已在执行中。如需修改计划请用 update_plan，如需停止请用 cancel_orchestrator。";

if (string.Equals(task.OrchestratorState, WorkTaskOrchestratorStates.Done, StringComparison.Ordinal))
    return "错误：项目组长已完成。如需重新执行请新建任务。";

if (string.Equals(task.OrchestratorState, WorkTaskOrchestratorStates.Failed, StringComparison.Ordinal))
    return "错误：项目组长上次执行失败。如需重新执行请新建任务。";

// 四条守门全部通过 → 切状态 + 启动 B
plan.Status = WorkTaskPlanStatuses.Running;
fileService.SavePlan(plan);
SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
StartProjectLeadInBackground(taskId);
return "计划已交付项目组长执行";
```

> 守门 3 的语义：**用户已确认 = PendingRequest 已被用户输入流程消化（PendingRequestKind 已清空或被改写）**。set_plan 设置 plan_confirmation 后，用户的下一条文本被系统识别为对该 PendingRequest 的回答，由现有用户输入处理逻辑（非 finalize_plan）清空。finalize_plan 只是消费这个状态，不主动清。

**验证**：

- 编译通过
- `dotnet test` 通过
- 跳过 set_plan 直接调 finalize_plan → 返回"尚未制定计划"
- 用户没说"确认"前 A 调 finalize_plan → 返回"计划尚未经过用户确认"，B 不启动
- 用户确认后 A 调 finalize_plan → 通过，B 启动
- A 重复调 finalize_plan 第二次（B 正在跑）→ 返回"已在执行中"
- B 完成后 A 再调 finalize_plan → 返回"已完成"
- handoff 直接派发的任务（plan.yaml 已由 commit 3.4 同步生成）→ 守门 1 通过

### commit 5：ProjectLeadService 两条完成 + 两条失败路径都调 MarkCompleted/MarkFailed；final_report 重定义为纯输出工具

**目标**：闭合 B 的状态机，B 完成后 `IsActive=0`，避免僵尸任务。同时把 `final_report` 重新定义为**只读纯输出工具**——读取 plan 与步骤摘要生成汇报内容返回给 A，不改任何任务状态；在展示层可以按需要返回缩略版内容，避免和 B 的 MarkCompleted 双写。

#### 改造点 5.1：ProjectLeadService 两条完成路径都闭合

[ProjectLeadService.cs](../../../Src/Netor.Cortana.AI/WorkMode/ProjectLead/ProjectLeadService.cs) 有**两条**完成路径，**都要加 MarkCompleted**，否则实际走哪条不闭合就会僵尸：

**路径 A**：while 循环里 `FindNextExecutableStep` 返空 → 行 72-80：

```csharp
// 改造前
plan.Status = WorkTaskPlanStatuses.Done;
fileService.SavePlan(plan);
SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Done, touchHeartbeat: true);
Touch(taskId);
await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
await publisher.PublishAsync(Events.OnWorkTaskCompleted,
    new WorkTaskCompletedArgs(taskId, "计划已全部完成。")).ConfigureAwait(false);
return;

// 改造后（在 SetOrchestratorState 后加一行）
plan.Status = WorkTaskPlanStatuses.Done;
fileService.SavePlan(plan);
SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Done, touchHeartbeat: true);
taskService?.MarkCompleted(taskId, finalReport: BuildSummary(plan));   // ← 新增
Touch(taskId);
await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
await publisher.PublishAsync(Events.OnWorkTaskCompleted,
    new WorkTaskCompletedArgs(taskId, "计划已全部完成。")).ConfigureAwait(false);
return;
```

**路径 B**：`RunStepAsync` 内（行 184-191）完成最后一步后检测到 `plan.Status==Done`：

```csharp
// 改造前
if (string.Equals(plan.Status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal))
{
    SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Done, touchHeartbeat: true);
    await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
    await publisher.PublishAsync(Events.OnWorkTaskCompleted,
        new WorkTaskCompletedArgs(taskId, "计划已全部完成。")).ConfigureAwait(false);
}

// 改造后
if (string.Equals(plan.Status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal))
{
    SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Done, touchHeartbeat: true);
    taskService?.MarkCompleted(taskId, finalReport: BuildSummary(plan));   // ← 新增
    await CreateTaskEventAsync(taskId, WorkTaskEventKinds.Completion, "计划已全部完成。").ConfigureAwait(false);
    await publisher.PublishAsync(Events.OnWorkTaskCompleted,
        new WorkTaskCompletedArgs(taskId, "计划已全部完成。")).ConfigureAwait(false);
}
```

> 实际执行通常走**路径 B**（最后一步在 RunStepAsync 里就把 plan.Status 改成 Done 并发完成事件），路径 A 是"上一次循环还有 pending、这一次循环全 done 了"的兜底（罕见但存在）。只在路径 A 加 → 大概率不闭合。

`MarkCompleted` 自身需要幂等：内部对已 `IsActive=0` 的任务再次调用应直接返回不二次写库（用 `WHERE IsActive=1` 条件 UPDATE 即可）。

#### 改造点 5.2：失败路径也对称两处

**失败路径 A**：while 循环里检测到 `plan.Steps.Any(... Failed)` → 行 62-69 加 `taskService?.MarkFailed(taskId, errorMessage)`

**失败路径 B**：`RunStepAsync` 内 catch 块 → 行 198-206 加 `taskService?.MarkFailed(taskId, ex.Message)`

`ExpandStepAsync` 内的失败路径（行 138-144）也加。

#### 改造点 5.3：`BuildSummary` 帮助方法

新增私有 `BuildSummary(WorkTaskPlanFile plan)`，把 plan 中所有 step 的 summary 串成一个总结字符串（每个 step 一行 + 标题 + summary），写到 `WorkTasks.FinalReport` 字段。MVP 阶段简单实现即可。

#### 改造点 5.4：final_report 重定义为纯输出工具

`final_report` 工具：

- 调用时只做：`LoadPlan(taskId)` → 生成人类可读汇报内容 → 返回给 A
- 默认可以基于 plan 与步骤摘要生成适合界面展示的缩略版结果；底层归档全文仍由 B 写入 `WorkTasks.FinalReport`
- **不调用** `MarkCompleted` / `MarkFailed` / `SetOrchestratorState`
- **不改** plan.Status / IsActive / FinalReport 字段
- 幂等：调多次都只读；调用时机不影响任务状态
- 适用场景：用户问"现在做完了吗 / 总结一下"，A 可以随时调，包括 B 还在 running 时调（返回的是截至当前的进度快照或对应的展示摘要）

> 这样职责清晰：
>
> - **B 闭合状态**（MarkCompleted / MarkFailed）
> - **A 读取结果**（final_report 纯输出）
>
> 两者不会互冲，也不会双写。

清理 `final_report` 工具实现中残留的状态变更代码（如有调用 `MarkCompleted` 等）。

**验证**：

- 编译通过
- `dotnet test` 通过
- 完整跑一个 25 步任务到结束 → 数据库观察：`IsActive=0, CompletedAt!=NULL, FinalReport!=NULL`（由 B 的 MarkCompleted 写入）
- **覆盖率验证**：构造一个单元测试模拟"RunStepAsync 完成最后一步"路径（路径 B），验证 IsActive=0；再构造一个模拟"while 循环兜底"路径（路径 A），验证 IsActive=0（已由 `ProjectLeadService_CompletesActiveTaskWhenNoExecutableStepsRemain` 覆盖）
- 重启应用 → 该任务**不会被孤儿检测标记**（因为 `IsActive=0`）
- B 已 MarkCompleted 后 A 调 final_report → 返回只读汇报结果（可为展示缩略版）；数据库观察 `IsActive`、`CompletedAt`、`FinalReport` 字段**值不变**（final_report 不再覆写）
- B 还在 running 时 A 调 final_report → 返回"已完成 N/M 步"的进度快照或对应展示摘要；任务状态不受影响（已由 `FinalReport_ReturnsRunningSnapshotWithoutMutatingTaskState` 覆盖）
- 失败路径：故意让一个 step 失败 → `IsActive=0, ErrorMessage!=NULL`

---

## 六、验证清单（端到端）

按 commit 顺序验证之外，全部完成后执行以下端到端测试：

### 测试用例 1：长任务正常流转（核心回归）

**输入**：原失败的 25 步电商平台调研任务

**期望**：

- A 调 set_plan → plan.yaml 草稿生成，CurrentPlanJson 同步有值，`plan_confirmation` 标记
- A 询问用户"计划如下，是否开始"
- 用户回"开始" → PendingRequestKind 被用户输入流程清掉
- A 调 finalize_plan（无 planJson 参数）→ 四条守门全过 → 启动 B
- A 流在 ≤2 秒内自然结束
- `WorkTasks.OrchestratorState=Running`
- `WorkTasks.OrchestratorHeartbeatAt` 持续更新（每 N 秒）
- WorkTaskEvents 表持续追加 Milestone / Completion 事件
- plan.yaml 中 step.status 逐个变为 done
- **25 步全部完成**——这是核心
- 任务结束时 `OrchestratorState=Done, IsActive=0, CompletedAt!=NULL, FinalReport!=NULL`（无论走路径 A 或 B 都闭合）

> 自动化端到端式覆盖：`LongTask_SetPlanConfirmFinalizeRunsTwentyFiveStepsAndClosesTask` 已覆盖 `set_plan → 清除 plan_confirmation → finalize_plan → B 后台推进 25 步 → WorkTaskEvents 追加 Milestone/Completion → IsActive=0 / FinalReport 写入`。该用例使用确定性 fake dispatcher，不等同于真实 UI + LLM 原任务复跑。

### 测试用例 2：四条守门覆盖

| 子用例 | 输入 | 期望返回 |
| --- | --- | --- |
| 2.1 | 没调 set_plan 直接 finalize_plan | "尚未制定计划" |
| 2.2 | plan.Status=Running 时再调 finalize_plan | "计划当前状态是 Running，不能 finalize" |
| 2.3 | 用户没确认就 finalize_plan | "计划尚未经过用户确认" |
| 2.4 | OrchestratorState=Running 时调 finalize_plan | "项目组长已在执行中" |
| 2.5 | OrchestratorState=Done 时调 finalize_plan | "项目组长已完成" |
| 2.6 | OrchestratorState=Failed 时调 finalize_plan | "项目组长上次执行失败" |
| 2.7 | handoff 直接派发的任务 finalize | 通过（plan.yaml 由 commit 3.4 同步生成，PendingRequestKind 为空 → A 走 prompt 中"任务预置场景"分支直接 finalize） |
| 2.8 | 走 load_plan_from_template → 用户确认 → finalize | 通过（plan.yaml 由 commit 3.5 同步生成） |

### 测试用例 3：A 越权防御

**场景**：手工修改 prompt 让 A 试图调 dispatch_step / sys_write_file / sys_set_window_topmost / agent_xxx

**期望**：

- `dispatch_step` 不在工具集里（commit 1）→ 模型尝试调用时报"工具不存在"
- `sys_write_file` 不在工具集里——AgentBoundTools 反查被关掉 + BoundPlugins 清空（commit 2.1 + 2.2）→ 同上
- `sys_set_window_topmost` 等写类工具被 `ToolFilterMode.WorkModeManager` 屏蔽（commit 2.3）→ 同上
- `agent_xxx` 子智能体工具不再注入——A 走 Build 不走 BuildWithSubAgents（commit 2.4）→ 同上
- `sys_list_windows` / `sys_get_ai_config` 等只读工具**仍可调**（A 需要回答用户）
- A 没有任何写类业务工具可调，最终只能输出文本

### 测试用例 4：用户中途打断

**场景**：B 正在执行第 5 步，用户发"暂停"

**期望**：

- A 调 pause_orchestrator → plan.yaml.Status = Paused
- B 当前 step 跑完后下一轮 while 循环检测到 Paused → 退出（已由 `RunAsync_StopsAfterCurrentStepWhenPlanIsPaused` 覆盖）
- 用户说"继续" → A 调 resume_orchestrator → B 重启继续推进

> 注：当前 pause 是"当前 step 完成后停下"，不是"立即中断"——这是已知限制，不在本修复范围内。

### 测试用例 5：B 完成状态闭合（路径 A + 路径 B）

**场景 5.1（路径 B：常见）**：跑一个简单的 3 步任务到全部完成，最后一步完成时 RunStepAsync 内检测到 plan.Status==Done

**期望**：

- 数据库 `WorkTasks.IsActive=0, CompletedAt!=NULL, FinalReport!=NULL`
- 重启应用 → 该任务**不被** `WorkModeStartupService` 标记为孤儿

**场景 5.2（路径 A：兜底）**：构造一个单元测试或调试场景，让 RunStepAsync 完成时 plan.Status 还不是 Done（例如 mock UpdateStepStatus 不切 plan.Status），下一轮 while 循环走 `FindNextExecutableStep=null` 兜底路径

**期望**：同样 `IsActive=0, CompletedAt!=NULL, FinalReport!=NULL`

**场景 5.3**：故意让 step 2 失败 → 走失败路径 B（RunStepAsync catch）

**期望**：`IsActive=0, ErrorMessage!=NULL`，无僵尸态

### 测试用例 6：final_report 纯输出（不污染状态）

**场景 6.1**：B 完成后 A 调 final_report

**期望**：

- 返回包含全部 step.summary 的人类可读文本
- 数据库观察：`IsActive`、`CompletedAt`、`FinalReport` **三个字段值前后不变**（都由 B 的 MarkCompleted 写入，final_report 不覆写）

**场景 6.2**：B 还在 Running 时 A 调 final_report

**期望**：

- 返回"已完成 N/M 步"的进度快照
- 任务状态**完全不变**：`OrchestratorState 仍为 Running`，`IsActive 仍为 1`，`CompletedAt 仍为 NULL`

**场景 6.3**：A 连续调 final_report 三次

**期望**：

- 三次返回内容一致（幂等）
- 任务状态不变

### 测试用例 7：CurrentPlanJson 兼容路径不破

**场景**：set_plan 后 → 验证以下使用 CurrentPlanJson 的功能仍正常

**期望**：

- UI 历史展示 plan 内容不空白（[WorkModeView.axaml.cs:135](../../../Src/Netor.Cortana.UI/Controls/WorkMode/WorkModeView.axaml.cs#L135)）
- `save_current_plan_as_template` 能保存模板
- `get_recent_completed_task_plan` 能取到最近任务计划
- `WorkTaskTitleService` 标题生成不报错

### 测试用例 8：@ 专员被正确路由到 MentionsJson 命中

**场景**：用户在聊天里 @ 一个特定 ID 的专员（比如同名多版本中的"novel-writer-v2"，ID 为 `abc123`），handoff 进入工作模式，plan.steps[0].role = "novel-writer-v2"

**期望**：

- `WorkTasks.MentionsJson` 含 `{"AgentId":"abc123","AgentName":"novel-writer-v2"}`
- B 的 `ProjectStepDispatcher.DispatchAsync` 先在 MentionsJson 里命中 `abc123`，**不走全局 `FindByNameOrDisplayName`**
- 实际被调起的 Agent ID 是 `abc123`，不是同名的其他版本

**反向验证**：plan.steps[1].role = "code-reviewer"（不在 MentionsJson 里）→ 走全局 fallback，能找到唯一的 `code-reviewer` Agent

---

## 七、不在本次范围内的事项

明确标出以避免范围蔓延：

| 事项 | 为什么不修 |
| --- | --- |
| pause_orchestrator 立即打断 C | 当前是"下一 step 前停下"，不是 bug，是 UX 缺陷。后续 PR |
| 软停止机制（RequestSoftStop） | A 工具集清空 + WorkModeManager 过滤后，A 即使想停不下也无事可做，没必要 |
| dispatch_step 运行时守门 | commit 1 后工具直接被删，不存在守门必要 |
| DispatchTools / VerifyTools / SelfEvaluationTools 类文件删除 | 等确认零引用后单独 PR 清理 |
| **`WorkTasks.CurrentPlanJson` 字段彻底从代码移除** | **本次不做**。grep 还有 6 处非废弃读端在用（UI/模板/最近任务/handoff/标题生成）。commit 3 让 plan.yaml 成为 canonical，CurrentPlanJson 作兼容缓存继续维护；删字段单列后续 PR，按读端逐一迁移：①UI 直接读 plan.yaml；②模板/最近任务工具读 plan.yaml + 兼容历史 CurrentPlanJson 字段；③handoff 直接调 set_plan；④标题生成读 plan.yaml；最后 ALTER 删字段 |
| `WorkPlan` DTO 删除 | 同上，CurrentPlanJson 字段还在，DTO 还有用 |
| `WorkHandoffTools` 入参 schema 升级到新 schema | 同上，commit 3.4 只做"老 schema 进来时同步生成 plan.yaml"，不动入参 schema |
| 给 plan.yaml 加 `available_roles` 头部字段做更严格的 role 白名单 | commit 2.4 已让 dispatcher 优先匹配 MentionsJson + 全局 fallback，对 @ 专员场景已闭环；更严格的白名单（"plan.role 不在 mentions 内禁止 dispatch"）等出现实际越权需求再加 |
| WorkExecutionLogs 路径调整 | B 路径用事件，A 路径已无 dispatch_step 不再写日志，自然解决 |
| AIContextProvider 自身重构（按 mode 区分注入集） | WorkModeManager 过滤档已足够；架构级重构等真有第二个使用场景再做 |

---

## 八、commit 拆分依赖与回滚

```text
commit 1（删工具注册 + 重写 prompt）— 独立，必须捆绑（否则 prompt 教调不存在的工具）
commit 2（GM 工具范围收缩：skipAgentBoundTools + 清空 BoundPlugins + WorkModeManager 过滤 + 不传 mentions）— 独立
  ↓
  commit 1 + 2 = 解决核心问题（25 步跑 13 步停）

commit 3（set_plan 双写 + finalize_plan 去 planJson + handoff 同步生成 plan.yaml）— 依赖 commit 1+2 已上线
commit 4（finalize_plan 四条守门）— 依赖 commit 3（守门要读 plan.Status，前提是 set_plan 已写 plan.yaml）
commit 5（B 两条完成 + 两条失败路径都闭合 + final_report 纯输出化）— 独立，与其他一起验证更顺畅
```

每个 commit 独立 `git revert` 可回滚。如果 commit 3 改造发现兼容性问题，可单独回滚到"两套 schema 并存"状态，不影响 commit 1+2 的核心修复。

> **如果先回滚 commit 3**：commit 4 守门 2（plan.Status==Planning）就读不到——因为 plan.yaml 可能不存在。所以 commit 4 必须紧跟在 commit 3 之后；如果要回滚，按 5 → 4 → 3 的逆序回滚最安全。
>
> **commit 2 内部四个改造点必须一起上线**：2.1（skipAgentBoundTools 开关）、2.2（清空 BoundPlugins）、2.3（WorkModeManager 过滤档）、2.4（不传 mentions）。任何一个漏过 A 都还有路可走：
>
> - 缺 2.1 + 2.2 任一 → BoundPlugins 业务工具还在
> - 缺 2.3 → 全局 AIContextProvider 写工具还在
> - 缺 2.4 → agent_xxx 子智能体工具还在

---

## 九、对其他文档的影响

- [工作模式三层组织架构重构方案.md](工作模式三层组织架构重构方案.md) §十.D1 的"B 不挂 AF Workflow"决策已在代码中实现；本次修复落地"A 不持有执行能力"的契约
- [执行计划(三层架构骨架).md](执行计划(三层架构骨架).md) 阶段 1 的 set_plan / finalize_plan 工具实现已完成，本次修复对二者做职责切分（set_plan 写 plan.yaml + 兼容缓存 / finalize_plan 只启动）
- [执行计划(可靠性兜底与看门狗).md](执行计划(可靠性兜底与看门狗).md) 阶段 4 的 OrchestratorState 状态机已落地，本次修复在 B 的两条完成/失败路径都闭合 IsActive

代码修复、自动化验证、端到端清单与原 25 步任务回归全部通过，本文档已归档至 `Docs/已完成功能规划/工作模式三层架构重构/`。

### 当前自动化验证记录

- `dotnet build Src\Netor.Cortana.AI\Netor.Cortana.AI.csproj`：通过
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj`：60 通过 / 0 失败
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter WorkModeToolsetTests`：1 通过 / 0 失败，覆盖 A 工具集注册面不含执行类工具
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter WorkPlanDoubleWriteTests`：5 通过 / 0 失败，覆盖 `set_plan` / `load_plan_from_template` 双写 `CurrentPlanJson` 与 `plan.yaml`、handoff 预置计划生成 `plan.yaml`、模板保存读取 CurrentPlanJson、最近完成任务计划复用
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter WorkTaskReliabilityTests`：14 通过 / 0 失败，覆盖 `finalize_plan` 守门、B 完成/失败闭合、while 兜底完成闭合、pause/cancel 状态映射、25 步自动化端到端式链路与 `final_report` 完成 / running 只读
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter "ProjectLeadServiceTests|WorkTaskReliabilityTests"`：17 通过 / 0 失败，覆盖 B 暂停后退出与任务可靠性路径
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter "WorkTaskReliabilityTests|WorkPlanDoubleWriteTests"`：17 通过 / 0 失败
- `dotnet test Tests\Netor.Cortana.AI.Tests\Netor.Cortana.AI.Tests.csproj --filter ProjectStepDispatcherPromptTests`：3 通过 / 0 失败，覆盖 `MentionsJson` 专员路由
- `dotnet test Tests\Netor.Cortana.MeetingMode.Tests\Netor.Cortana.MeetingMode.Tests.csproj --filter ToolFilterTests`：5 通过 / 0 失败，覆盖 `ToolFilterMode.WorkModeManager` 白名单
- `dotnet build Netor.Cortana.slnx`：0 错误；当前输出含 57 个既有插件/分析器警告

---

## 十、待办

- [x] commit 1：WorkModeToolset 移除执行类工具注册 + 重写 A 的 prompt（含"任务预置场景"放行口）
- [x] commit 2：GM 工具范围收缩（5 个改造点合并）
  - [x] 2.1 AIAgentFactory.Build 新增 `skipAgentBoundTools` 参数
  - [x] 2.2 GeneralManagerAgentBuilder 清空 BoundPlugins/BoundMcp
  - [x] 2.3 新增 `ToolFilterMode.WorkModeManager` 档（白名单 + sys_get_*/sys_list_*）
  - [x] 2.4 GM 不再走 BuildWithSubAgents（不传 mentions）+ ProjectStepDispatcher 优先匹配 MentionsJson
  - [x] 2.5 调用点串起来传 `toolFilterMode: WorkModeManager, skipAgentBoundTools: true`
- [x] commit 3：set_plan/update_plan/handoff/load_plan_from_template 全部双写 plan.yaml + CurrentPlanJson；finalize_plan 去 planJson；新增 `WorkPlanLegacyConverter`
  - [x] 3.1 set_plan 双写
  - [x] 3.2 finalize_plan 去 planJson 参数
  - [x] 3.3 update_plan 双写
  - [x] 3.4 handoff 同步生成 plan.yaml
  - [x] 3.5 load_plan_from_template 同步生成 plan.yaml（PlanTemplateTools 注入 WorkTaskFileService）
- [x] commit 4：WorkTaskFileService 新增 `GetPlanPath` + finalize_plan 四条守门
- [x] commit 5：ProjectLeadService 两条完成 + 两条失败路径都调 MarkCompleted/MarkFailed；final_report 重定义为纯输出
- [x] 端到端测试用例 1-8 全部通过
- [x] 重跑原失败的 25 步调研任务，确认全部完成
- [x] 修复完成后归档到 `Docs/已完成功能规划/工作模式三层架构重构/`

---

## 附：根因数据快照

测试任务 `9828078661ad4f7789e258628862037e`（来自 `madorin.db`）：

```text
WorkTasks 字段：
  IsActive=1, OrchestratorState=NULL, OrchestratorHeartbeatAt=NULL
  HeartbeatAt=2026-06-08 05:17:11（最后心跳，与最后 ToolResult 同步）
  IsOrphaned=1, OrphanedDetectedAt=2026-06-08 05:26:05（重启时间）

WorkExecutionLogs：
  171 条，全部产生于 05:05:48 → 05:17:11 这 11 分钟
  ToolCall 67 + ToolResult 67 + StepStart 13 + Acceptance 12 + StepComplete 12
  最后日志：dispatch_step("13.1 抖店") ToolResult success

WorkTaskContextMessages：
  仅 6 条（user 3 + assistant 3）——意味着只发生了 3 次 RunStreamingAsync
  最后 assistant 消息长达数千字，把 1-13 步全部塞进去
  内容尾部停在"继续步骤 13：抖音开放平台-抖店。"后无续

WorkflowCheckpoints / WorkBackgroundJobs / WorkPendingInputs / WorkTaskEvents:
  全部空

→ 结论：B 从未启动；A 自己一次推理跑完 13 步后流自然结束
```
