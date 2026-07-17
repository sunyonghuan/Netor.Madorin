# Cortana 工作模式方案策划

> 基于 Microsoft Agent Framework v1.6.0 的工作模式实施方案
>
> **产品定位**："一人公司"——用户是唯一的决策者和审批人，AI 是全部"员工"。用户给出目标，AI 自己把信息查清楚、把计划列出来、把活干完。
>
> **核心承诺**：支持视频剪辑、网站开发等长周期任务（6-30+ 小时）不中断；瞬时故障自动恢复；崩溃可恢复；不进入死循环。

---

## 📚 文档导航

### 基础理论（AF 框架知识库）

| 文档 | 说明 | 阅读优先级 |
|------|------|-----------|
| [01-AF框架总览.md](./01-AF框架总览.md) | Microsoft Agent Framework 包结构、能力矩阵、源码入口 | ⭐⭐⭐ |
| [02-核心架构.md](./02-核心架构.md) | AIAgent、AgentSession、Workflow、Executor 核心概念 | ⭐⭐⭐⭐ |
| [03-编排模式.md](./03-编排模式.md) | Sequential / Concurrent / GroupChat / Handoff / Magentic / WriterCritic | ⭐⭐⭐ |
| [04-长任务与持久化.md](./04-长任务与持久化.md) | Checkpoint、DurableAgent、恢复机制 | ⭐⭐⭐⭐ |
| [05-工具授权与沙盒.md](./05-工具授权与沙盒.md) | ApprovalRequiredAIFunction、Hyperlight 沙盒 | ⭐⭐⭐⭐ |
| [06-事件系统与通信.md](./06-事件系统与通信.md) | WorkflowEvent、IRunEventStream、StreamingRun | ⭐⭐⭐⭐⭐ |
| [07-业务模型映射设计.md](./07-业务模型映射设计.md) | "老板-总经理-部门-员工" 角色映射（背景知识） | ⭐⭐ |

### 实施方案（开发指南）

| 文档 | 说明 | 状态 |
|------|------|------|
| [09-界面交互设计.md](./09-界面交互设计.md) | UI/UX 规范、组件设计、视觉规格、Avalonia 实现（控件已完成） | ✅ 已实现 |
| [10-数据模型与持久化设计.md](./10-数据模型与持久化设计.md) | WorkTasks / WorkExecutionLogs / WorkflowCheckpoints / WorkPlanTemplates 表结构 | ✅ 已定稿 |
| [11-AF框架集成方案.md](./11-AF框架集成方案.md) | ChatClientAgent + 工具 + HITL（**不用 Magentic**）；动态提示词加载 | ✅ 已定稿 |
| [12-输入路由与状态机设计.md](./12-输入路由与状态机设计.md) | 路由分发、意图分类、AF Workflow 状态作为真相 | ✅ 已定稿 |
| [13-执行引擎与事件驱动.md](./13-执行引擎与事件驱动.md) | WorkflowExecutor / EventDispatcher / UI Controller（AOT 安全） | ✅ 已定稿 |
| [14-实施路线图与验收标准.md](./14-实施路线图与验收标准.md) | 7 个阶段、AOT 验收、风险与应对 | ✅ 已定稿 |
| [15-端到端示例与UI接入代码.md](./15-端到端示例与UI接入代码.md) | 完整代码示例：从用户输入到 UI 渲染、DI 注册、单元测试 | ✅ 已定稿 |
| [16-长任务可靠性与重试策略.md](./16-长任务可靠性与重试策略.md) | 长任务可靠性专题：5 层防护、重试策略、自评循环、崩溃恢复、死循环检测 | ✅ 已定稿 |
| [17-v1.0执行计划.md](./17-v1.0执行计划.md) | **可执行的 v1.0 开发计划**：基于实际代码探索后给出的 7 阶段任务清单、文件路径、复用点、验收标准 | ✅ 已批准 |
| [19-渐进式计划与动态专家问题记录.md](./19-渐进式计划与动态专家问题记录.md) | 记录“先调查再规划”、执行中展开子步骤、动态专家/临时子智能体等真实业务问题 | ✅ 已完成 |
| [20-工作模式上下文压缩设计.md](./20-工作模式上下文压缩设计.md) | 工作模式长任务上下文压缩方案：复用 `Compaction.*` 系统设置，按任务独立压缩运行上下文 | ✅ 已完成 |
| [08-Cortana工作模式实施路线图.md](./08-Cortana工作模式实施路线图.md) | 旧版路线图（已废弃） | ⚠️ 过期 |

---

## 🎯 快速开始

### 如果你是第一次接触这个方案

**推荐阅读顺序**：

1. **先看 UI 规范**：[09-界面交互设计.md](./09-界面交互设计.md) — 理解永续对话与卡片体系
2. **再看 AF 关键能力**：[02-核心架构.md](./02-核心架构.md) §一-五 + [04-长任务与持久化.md](./04-长任务与持久化.md) §三 + [06-事件系统与通信.md](./06-事件系统与通信.md) §二
3. **看实施方案**：按 10 → 11 → 12 → 13 → 15 → 14 的顺序阅读

### 如果你要开始开发

**必读**：

- [10-数据模型与持久化设计.md](./10-数据模型与持久化设计.md) — 先建表
- [11-AF框架集成方案.md](./11-AF框架集成方案.md) — Agent 与工具如何构建
- [13-执行引擎与事件驱动.md](./13-执行引擎与事件驱动.md) — 事件分发与 UI 接入
- [15-端到端示例与UI接入代码.md](./15-端到端示例与UI接入代码.md) — 完整代码示例
- [14-实施路线图与验收标准.md](./14-实施路线图与验收标准.md) — 按阶段推进

### 重要约束（开发前必读）

- 🏢 **一人公司定位**：用户是唯一决策者，没有团队/审批链/角色权限概念。AI 是全部"员工"，替用户把活干掉，**不**把决策都推给用户
- 🚫 **不使用 Magentic 模式**：工作模式是 HITL（人机协作），用普通 ChatClientAgent + 工具调用驱动，详见 [11 文档 §一](./11-AF框架集成方案.md#一架构决策为什么不用-magentic)
- 🚫 **AOT 安全是基础规范**：禁止反射、Binding、运行时表达式编译。详见 [13 文档 §一](./13-执行引擎与事件驱动.md#一设计原则) + [15 文档 §三](./15-端到端示例与UI接入代码.md#三aot-安全速查)
- ✅ **复用现有 UI 控件**：`TimelineBlock` / `FoldableCard` / `SubStepCard` / `AiContent` / `UserBubble` / `SummaryCard` / `ApprovalFloatPopup` 已就绪，按现有 API 接入
- ⏳ **演示代码晚清理**：当前 `WorkModeView.axaml.cs` 的 `LoadDemoConversation()` 及辅助方法是产品最满意的视觉基线，**阶段 1-5 不许删除**，只能注释调用入口；统一在阶段 6 由维护者评估清理。详见 [14 文档 §0.1](./14-实施路线图与验收标准.md#01-演示代码晚清理原则)
- 🔁 **长任务必须可靠**：视频剪辑 6-8 小时、网站开发 30+ 小时不中断、瞬时故障自动重试、崩溃可恢复、不进入死循环。这是 v1.0 的硬约束，不是 v1.1+ 才做。详见 [16 文档](./16-长任务可靠性与重试策略.md)
- 🧱 **插件自治边界**：插件已实现子进程隔离 + 后台会话 + 进度查询工具的完整能力。主软件**不为插件分配 jobId、不写插件状态到 DB、不调用插件内部状态接口**。`WorkBackgroundJobs` 表只服务子智能体等主软件托管的背景任务。详见 [16 文档 §八](./16-长任务可靠性与重试策略.md)
- 🧠 **工作模式上下文压缩复用系统设置**：工作任务运行上下文不能无限增长，必须复用 `Compaction.ModelId / SegmentSize / RawTailSize / MaxDisplaySegments`，但按 `TaskId` 独立存储压缩段落。详见 [20 文档](./20-工作模式上下文压缩设计.md)

---

## 🏗️ 架构概览

```text
┌─────────────────────────────────────────────────────────────┐
│ Cortana 主程序                                                 │
│                                                                │
│  ┌────────────────────────────────────────────────────┐      │
│  │ WorkModeView (UI) — 控件已就绪                       │      │
│  │  ├─ InputAreaView (复用专家模式)                     │      │
│  │  ├─ MessageList (永续消息流)                          │      │
│  │  │   ├─ UserBubble / AiContent / SummaryCard          │      │
│  │  │   └─ TimelineBlock (主/子步骤 / 子智能体卡片)       │      │
│  │  ├─ ApprovalFloatPopup (授权浮窗)                    │      │
│  │  └─ TaskResumePromptCard (崩溃恢复决策)               │      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕  EventHub                           │
│  ┌────────────────────────────────────────────────────┐      │
│  │ WorkModeViewController (UI 接入层，AOT 安全)         │      │
│  │  └─ 订阅 work.* 事件 → Dispatcher.UIThread.Post     │      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕                                     │
│  ┌────────────────────────────────────────────────────┐      │
│  │ WorkModeInputRouter（输入路由）                       │      │
│  │  ├─ 活跃任务 → ResumeAsync / ContinueAsync           │      │
│  │  └─ 无任务 → IntentClassifier → 新建任务 / 普通对话  │      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕                                     │
│  ┌────────────────────────────────────────────────────┐      │
│  │ WorkflowExecutor                                     │      │
│  │  ├─ AgentWorkflowBuilder.BuildSequential(manager)    │      │
│  │  ├─ InProcessExecution.RunStreamingAsync             │      │
│  │  └─ HITL: RequestInfoEvent 自然挂起 + Resume         │      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕                                     │
│  ┌────────────────────────────────────────────────────┐      │
│  │ Layer 3: 总经理 Agent (ChatClientAgent，动态构建)    │      │
│  │  ├─ Instructions ← IPromptProvider                  │      │
│  │  └─ 工具集（不区分"插件 vs 子智能体"，统一 [LONG] 约定）│      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕  AIFunction[]                       │
│  ┌────────────────────────────────────────────────────┐      │
│  │ Layer 2: 工具注入（AIAgentFactory + WorkModeToolset） │      │
│  │  ├─ 计划/验收/HITL 工具                              │      │
│  │  ├─ 普通插件工具    → PluginContextProvider         │      │
│  │  ├─ 长任务插件工具  → 同上（插件自管会话/查询）        │      │
│  │  ├─ 同步子智能体    → BuildWithSubAgents             │      │
│  │  └─ 背景子智能体    → IBackgroundJobExecutor 框架    │      │
│  └────────────────────────────────────────────────────┘      │
│                          ↕                                     │
│  ┌────────────────────────────┬───────────────────────┐      │
│  │ Layer 1A: 插件自治（已有）   │ Layer 1B: 主软件托管  │      │
│  │                            │ （v1.0 新增）         │      │
│  │ ExternalProcessPluginHost  │ IBackgroundJobExecutor│      │
│  │   ↳ stdin/stdout JSON      │   ↳ SubAgentJobExecutor│     │
│  │   ↳ PluginBus WS 推送       │   ↳ v1.1+ 扩展点      │      │
│  │                            │                       │      │
│  │ 状态：插件子进程内自管      │ 状态：WorkBackgroundJobs│     │
│  │ 主软件 DB 不参与            │                       │      │
│  └────────────────────────────┴───────────────────────┘      │
│                          ↕                                     │
│  ┌────────────────────────────────────────────────────┐      │
│  │ 持久化（CortanaDbContext，AOT 安全）                  │      │
│  │  ├─ ChatSessions / ChatMessages（对话数据，复用）    │      │
│  │  ├─ WorkTasks（任务元数据 + RunId + IsActive）       │      │
│  │  ├─ WorkExecutionLogs（工具/验收明细）               │      │
│  │  ├─ WorkflowCheckpoints（AF JsonCheckpointStore）    │      │
│  │  ├─ WorkPendingInputs（用户插话队列）                │      │
│  │  ├─ WorkPlanTemplates（计划模板，复用）              │      │
│  │  └─ WorkBackgroundJobs（仅服务子智能体，不含插件）   │      │
│  └────────────────────────────────────────────────────┘      │
└─────────────────────────────────────────────────────────────┘
```

### 长任务编排的职责边界（架构核心）

工作模式所有"耗时超过 60 秒的工具调用"必须按"start → poll → use"模式执行。两个执行后端：

| 维度 | 插件长任务（A 类） | 子智能体背景（B 类） |
|------|-------------------|------------------|
| 执行体 | 插件子进程（已有） | 主进程内 Task（v1.0 新增） |
| 状态存储 | 插件内部（如 `SessionRegistry`） | `WorkBackgroundJobs` 表 |
| 进度查询 | 插件提供查询工具 | `wait_for_subagent` 工具读 SQLite |
| 主软件 DB | **零写入**（边界！） | 写 `WorkBackgroundJobs` |
| AI 看到的接口 | `[LONG] sys_*` | `[LONG] start_subagent_*` / `wait_for_subagent` |
| 实施工作 | 无（已就绪） | v1.0 阶段 4 新建 |

**架构原则**：插件已经是自治的状态边界。主软件不要侵入这个边界（不为插件分配 jobId、不写插件状态到主 DB、不查询插件内部接口）。详见 [16 文档 §八](./16-长任务可靠性与重试策略.md)。

---

## 🔑 核心设计原则

### 1. AOT 安全（最高优先级）

- **禁止反射**：所有 JSON 用 `WorkModeJsonContext` 源生成
- **禁止 Binding**：UI 用 code-behind 直接更新控件（参考 `RealtimeProcessCard` / `TimelineBlock`）
- **禁止运行时编译**：工具用 `AIFunctionFactory.Create` + 强类型签名

### 2. 永续对话

- 工作模式 Tab 与对话模式 Tab **共享 ChatSession**
- 任务完成后总结写入 `ChatMessages`，下次对话能看到
- 用户能说"再做一份 5 月的"，新任务通过 `SourceTaskId` 关联到上一任务

### 3. 用 AF Workflow 状态作为真相

- **不**自定义复杂状态机（`Clarifying / Planning / Executing` 等）
- Cortana 端只用 `IsActive` 布尔 + `PendingRequestId` 表达任务存活与挂起
- 阶段切换由 AI 通过工具调用自然驱动

### 4. HITL 而非 CTS 取消

- 暂停 = `RequestInfoEvent` 让 Workflow 自然挂起
- 用户回应 = `run.SendResponseAsync(...)` 恢复
- **绝不** `cts.Cancel()` 暂停（会导致 Workflow Failed 无法 Resume）

### 5. 用户插话用输入队列

- 执行中用户输入 → `WorkPendingInputs` 队列
- 主 Agent 在每个主步骤前调用 `check_pending_user_input` 工具消费
- 不强行打断 Workflow

### 6. 提示词与计划可复用

- 提示词通过 `IPromptProvider` 加载（本地 / DB / 远程，未来可扩展）
- 计划可从模板加载、从专家对话提取、从会议讨论提取

---

## 📊 数据流图

```text
用户输入
  ↓
WorkModeView.OnUserSubmit → MessageList.Items.Add(UserBubble)
  ↓
WorkModeInputRouter.RouteAsync(text)
  │
  ├─ 当前会话有活跃任务（IsActive=1）?
  │    │
  │    ├─ 是 + PendingRequestId 非空 ─→ ResumeAsync(text)        ← HITL 响应
  │    │
  │    └─ 是 + PendingRequestId 空 ─→ ContinueAsync(text)        ← 多轮对话延续
  │
  └─ 无活跃任务 ─→ IntentClassifier.ClassifyAsync(text)
       │
       ├─ NewTask ─→ INSERT WorkTasks → ExecuteAsync(taskId, text)
       │
       └─ NormalChat ─→ IAiChatEngine.SendMessageAsync(text)     ← 走专家模式
  ↓
WorkflowExecutor: InProcessExecution.RunStreamingAsync(workflow, msg)
  ↓
run.WatchAsync() 事件流
  │
  ├─ AgentResponseUpdateEvent       → Publish work.assistant.delta → UI 流式追加
  ├─ FunctionCallContent (set_plan) → 工具内部 UPDATE WorkTasks.CurrentPlanJson
  │                                    + Publish work.plan.updated → UI 加计划 Markdown
  ├─ FunctionCallContent (dispatch) → Publish work.tool.called    → UI: TimelineBlock.AddMainStep
  ├─ FunctionCallContent (其他)     → Publish work.tool.called    → UI: SubStepCard.AppendDetail
  ├─ FunctionResultContent          → Publish work.tool.completed → UI: 更新卡片状态
  ├─ RequestInfoEvent (授权)        → UPDATE WorkTasks.PendingRequest*
  │                                    + Publish work.approval.requested → UI: ApprovalPopup.Show
  ├─ RequestInfoEvent (ask_user)    → 写 ChatMessages + Publish work.ask_user → UI 等输入
  └─ WorkflowOutputEvent            → IsActive=0 + Publish work.task.completed → UI: SummaryCard
  ↓
INSERT WorkExecutionLogs（每个工具调用 / 验收）
  ↓
所有事件可用同一份 EventHub 推给插件（v1.1+：WS 广播）
```

---

## 🛠️ 技术栈

| 层级 | 技术 |
|------|------|
| **UI 框架** | Avalonia 11 (.axaml)，code-behind，AOT 安全 |
| **AI 框架** | Microsoft Agent Framework v1.6.0 |
| **核心模式** | `ChatClientAgent` + 工具调用 + HITL（**不用 Magentic**） |
| **数据库** | SQLite (CortanaDbContext，纯 P/Invoke) |
| **事件总线** | Netor.EventHub (`IPublisher` / `ISubscriber`) |
| **模型接入** | IChatClient (Microsoft.Extensions.AI) |
| **Checkpoint** | 自定义 `SqliteCheckpointStore : JsonCheckpointStore` |
| **JSON 序列化** | `JsonSerializerContext` 源生成（`WorkModeJsonContext`） |

---

## 📝 术语表

| 术语 | 含义 |
|------|------|
| **工作模式** | Cortana 的工作 Tab（对应 `WorkMode.Workflow` 枚举） |
| **永续对话** | 任务完成后不结束会话，继续等待下一句话 |
| **总经理 Agent** | 工作模式主智能体，普通 `ChatClientAgent` + 工作模式工具集 |
| **部门 / 员工** | 通过 `@` 提及的子智能体，由 `BuildWithSubAgents` 自动转为 `agent_<id>` 工具 |
| **时间线** | `TimelineBlock`，由 `dispatch_step` 工具调用驱动添加主步骤 |
| **活跃任务** | `WorkTasks.IsActive = 1`，对应 AF Workflow 还未 Completed/Failed |
| **HITL 挂起** | `WorkTasks.PendingRequestId IS NOT NULL`，等待用户响应授权或 ask_user |
| **Checkpoint** | AF 框架在每个 superstep 自动保存的状态，存于 `WorkflowCheckpoints` 表 |
| **RunId** | `StreamingRun.RunId`，AF 恢复任务的关键标识，存于 `WorkTasks.RunId` |
| **输入队列** | `WorkPendingInputs` 表，执行中用户插话暂存的位置 |
| **计划模板** | `WorkPlanTemplates` 表，可复用的 `WorkPlan` JSON |

---

## 🚀 下一步

1. **历史归档**：本目录已作为工作模式完整方案归档。
2. **问题追溯**：按 10 → 11 → 12 → 13 → 14 → 17 → 18 的顺序查看设计与落地过程。
3. **后续演进**：新需求另起未来版本策划，已完成内容以本目录为基线。

---

## 📞 相关资源

- **AF 框架源码**：`E:\OpenSourse\agent-framework-main.v1.6.0\dotnet\src`
- **AF 示例代码**：`E:\OpenSourse\agent-framework-main.v1.6.0\dotnet\samples`
- **Cortana 代码库**：`E:\Netor.me\Cortana\Src`
- **UI 组件目录**：`Src\Netor.Cortana.UI\Controls\WorkMode\`

---

**最后更新**：2026-06-02
