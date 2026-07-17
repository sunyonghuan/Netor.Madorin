# 模式间方案转交（start_work_task 工具）

> 实施稿 v1
> 当前状态：✅ 已接入专家模式与会议主持人，已完成基础验证

---

## 一、要解决的问题

专家模式和会议模式经常会讨论出**已经成型的执行方案**（步骤 + 验收标准 + 参与角色），但用户要把它落地，只能：

1. 复制 markdown 到工作模式输入框，重新开始一遍需求讨论
2. 或者在工作模式重述一遍目标，让总经理 Agent 再拆一次

两种路径都丢失了已经讨论清楚的语义，用户操作冗余。

---

## 二、核心设计

**给会议主持人 Agent 和专家模式 Agent 注入一个工具 `start_work_task`，由模型自行判断是否调用。**

不引入 UI 按钮，不引入 Draft 实体，不引入"导出"中间态。整个机制就是一个 `AIFunction`。

### 2.1 工具签名

```csharp
[Description("""
当用户表示要开始执行已经讨论清楚的方案时调用。
调用前自行判断三条:
1. 上下文是否包含明确目标和可执行步骤;
2. 步骤是否已经达成共识;
3. 用户最近是否表达了"开始做/去执行"的意图。
任一不满足则不要调用,继续在当前模式追问对齐。
""")]
async Task<string> StartWorkTaskAsync(
    [Description("一句话任务标题(不超过 30 字)")]
    string title,

    [Description("交给工作模式总经理的目标陈述。写清 WHAT/边界,不复述步骤")]
    string goal,

    [Description("""
    已讨论好的执行计划(可选)。提供时工作模式将跳过 set_plan 直接派发。
    格式: {"main_steps":[{"title":"...","sub_steps":[{"title":"...","acceptance_criteria":"..."}]}]}
    """)]
    string? planJson,

    [Description("建议的子智能体 ID 列表(可选)。会议中默认 = 参会者除主持人外")]
    string[]? mentionAgentIds,

    CancellationToken ct);
```

返回值约定：

- **成功**：`"已创建工作任务 <taskId>:<title>。已切换到工作模式。"`（模型看到这条就停说话）
- **校验失败**：返回错误描述（如"计划缺少 acceptance_criteria，无法转入执行"），模型继续在当前模式补全

### 2.2 三道闸防止滥用

| 闸 | 实现 |
|---|---|
| Tool Description | 写死三条触发条件，模型自行筛选 |
| JSON Schema 必填 | `title`/`goal` 必填，模型编不出 = 上下文不够 |
| 服务端校验 | 复用 [PlanTools.ValidatePlan](../../../Src/Netor.Cortana.AI/WorkMode/Tools/PlanTools.cs#L58)；会议侧追加"必须存在最近一条 summary 消息" |

### 2.3 一个工具，两条路径

- **`planJson` 已填**：工作模式直接 `taskService.UpdatePlan` + 进入派发，跳过 `set_plan` 工具
- **`planJson` 留空**：工作模式按现有流程，总经理 Agent 自行拆步骤

正是你说的"如果没有执行步骤就到工作模式继续讨论"的 fallback。

---

## 三、实施改动点

### 3.1 新建：`Src/Netor.Cortana.AI/Handoff/WorkHandoffTools.cs`

会议和专家共用的工具工厂。核心方法：

```csharp
public AIFunction CreateStartWorkTaskTool(
    string sourceKind,                                // "meeting" | "chat"
    Func<HandoffRuntimeContext> contextFactory,        // 调用工具时读取当前 session/workspace/模型配置
    Func<IReadOnlyList<AgentMention>>? defaultMentions = null,
    string? sourceId = null);                         // meetingId；chat 路径可为空
```

服务端逻辑就 4 步：
1. 解析 `mentionAgentIds`（没填用 `defaultMentions`）
2. 校验 `planJson`（若有）
3. 创建 `WorkTaskEntity`，复用现有 `WorkTaskService.Create`
4. 后台调用 `WorkflowExecutor.ExecuteFromHandoffAsync`，并通过 `IWorkModeSwitcher` 切到工作模式

> 实现说明：专家模式新会话可能在 Agent 构建时还没有稳定的 `sessionId`，所以没有把 `sourceId` 作为硬必填参数提前捕获，而是在工具真正调用时通过 `contextFactory` 读取当前上下文。

### 3.2 新增：`WorkflowExecutor.ExecuteFromHandoffAsync`

和现有 `ExecuteAsync` 区别只两点（不复制粘贴整个方法，内部抽公共流程）：

- 若传入 `planJson`：先 `taskService.UpdatePlan(taskId, planJson)`，并发布 `OnWorkPlanUpdated`
- 在 `_chatHistories[taskId]` 预置一条 `ChatRole.System` 消息：

  ```
  以下方案已在{会议/专家讨论}中达成共识。直接进入派发执行,不要重新规划,
  除非验收标准不齐。
  目标: {goal}
  ```

  无 `planJson` 时 system 消息会明确要求总经理继续按工作模式流程调用 `set_plan`，不要直接派发。

`requireConfirmation` 不做 —— 让模型在 system 消息里自行决定是否要再确认。少一个参数，少一处复杂度。

### 3.3 工具注入

| 模式 | 改动位置 | 改动 |
|---|---|---|
| 会议主持人 | [MeetingAgentBuilder.cs:112](../../../Src/Netor.Cortana.AI/MeetingMode/MeetingAgentBuilder.cs#L112) `hostTools` | 加 `handoffTools.CreateStartWorkTaskTool("meeting", meetingId, () => 参会者→Mentions)` |
| 专家模式 | [AiChatHostedService.cs:262-278](../../../Src/Netor.Cortana.AI/AiChatHostedService.cs#L262-L278) | 给 `factory.Build` 传 `additionalTools = [createStartWorkTaskTool("chat", sessionId, null)]` |

会议参会者 Agent **不挂**（只读约束保留）。

会议主持人使用 `ToolFilterMode.ReadOnly`，`start_work_task` 已加入会议控制工具白名单，避免因为 `start_` 前缀被只读过滤器移除。

### 3.4 UI 切换

新接口 `IWorkModeSwitcher.SwitchToWorkMode(string taskId)`，UI 侧实现 —— 切换 Tab + 选中新任务。`Handoff` 项目通过 DI 拿到这个接口，工具内部调用一次即可。

---

## 四、范围边界（不做什么）

- ❌ 不做 `WorkTaskDraft` 实体（直接进 `WorkTaskEntity`）
- ❌ 不做"导出"按钮 / 右键菜单（纯 AI Native 工具）
- ❌ 不做来源溯源字段（v1.0 不加 `SourceKind`/`SourceId`，需要时再加）
- ❌ 不做 `requireConfirmation` 参数（模型自己用 ask_user 决定）
- ❌ 不做"会议结束自动起草"开关（手动调用足够，自动会出现误启动）

---

## 五、关键问题与决策

### 5.1 会议中调用的时机限制

会议必须先 `output_summary` 才允许调用 `start_work_task`。校验逻辑：服务端检查最近一条 `MeetingMessages` 中存在 `MessageRole=summary` 的记录，否则返回错误："请先用 `output_summary` 输出会议总结再转交工作。"

### 5.2 专家模式默认是否给所有自定义 Agent 注入

**v1.0 默认全注入**，不做 Agent 配置开关。理由：
- Agent 描述里加一句"如果用户讨论清楚后让你执行，调用 `start_work_task`"足够
- 真有干扰再加配置项，避免提前抽象

### 5.3 工具注入对话历史污染问题

`AiChatHostedService` 缓存 `_agent` 实例，如果工具集变了要重建。已经有 [InvalidateAgent](../../../Src/Netor.Cortana.AI/AiChatHostedService.cs#L828) 机制（插件变化时调用），同一机制可复用 —— 但实际上 `start_work_task` 是常驻工具，不会动态增删，所以这点不是问题。

### 5.4 跨会话/跨工作区

工具创建的 `WorkTaskEntity` 用**当前 `ChatSessions.Id`**（会议或专家会话），`WorkspaceId` 用 `AiChatHostedService.CurrentWorkspaceId`。也就是说：在专家模式发起的工作任务和当前对话同 session；在会议中发起的工作任务和会议关联 session 同 session。

这样用户在工作模式 Tab 看到任务，回头切到对话 Tab 还能看到原始讨论，天然溯源不需要额外字段。

---

## 六、与"输入框工具开关"的关系

`start_work_task` 是系统级工具（well-known name 前缀 `system_*` 或固定名），**不进入用户屏蔽列表**。详见 [输入框工具开关 README](../../未来版本策划/输入框工具开关/README.md) §3.4。

---

## 七、文档结构

| 文档 | 内容 |
|---|---|
| README.md（本文） | 问题、设计、改动点、边界、已实现路径 |
| 执行计划(模式间方案转交).md | 阶段进度、验证结果、修改文件清单 |

---

**最后更新**：2026-06-01（v1 已实现）
