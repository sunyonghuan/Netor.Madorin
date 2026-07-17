# 10 - Cortana 工作模式逻辑实施方案

> 本文只描述工作模式的逻辑实现：永续对话、循环任务、步骤推进、工具执行、授权、暂停恢复和结果落地。
> 界面布局、卡片宽度、时间线形态、授权浮窗样式由 [09-界面交互设计.md](./09-界面交互设计.md) 负责，本文不重复 UI 规格。

## 一、文档定位

工作模式不是新增一个独立聊天系统，也不是重建一套页面结构。

它要在当前 Cortana 已有能力上完成一条任务闭环：

```text
同一个会话持续对话
→ 用户提出工作目标
→ AI 补齐需求
→ AI 生成计划
→ 用户自然语言修改或确认
→ 系统按计划执行步骤
→ 步骤过程实时记录
→ 需要高风险工具时请求授权
→ 执行失败时重试或回退
→ 任务完成后写入总结
→ 回到同一个会话继续等待下一句话
```

关键点：

- 不因为完成一个任务而新建会话。
- 不因为进入执行阶段而清空消息。
- 不把任务执行过程只停留在 Markdown 文本里。
- 计划中的步骤必须对应可持久化、可恢复、可验收的执行记录。
- 完成后的结果要进入当前会话上下文，让用户可以继续说“再做一份”“按刚才的方案改一下”。

## 二、当前代码基础

当前项目里已经存在这些真实承接点：

| 现有位置 | 当前职责 | 工作模式使用方式 |
|---------|----------|------------------|
| `Src/Netor.Cortana.UI/Models/WorkMode/WorkMode.cs` | 主窗口模式枚举，已有 `Chat / Workflow / GroupChat` | 工作模式继续使用 `Workflow` Tab |
| `Src/Netor.Cortana.UI/Controls/WorkMode/WorkModeView.axaml.cs` | 当前是演示数据预览，`LoadDemoConversation()` 直接填充 UI | 后续删除演示数据，改成订阅真实任务事件 |
| `Src/Netor.Cortana.Entitys/Interfaces/IAiChatEngine.cs` | 普通对话输入入口 | 工作模式不能简单复用 `SendMessageAsync` 直接聊天，需要在外层加任务状态路由 |
| `Src/Netor.Cortana.AI/AiChatHostedService.cs` | 维护 `AIAgent`、`AgentSession`、流式输出、工具过程事件 | 工作模式复用其会话、模型、工具、历史能力，但执行任务要有独立状态机 |
| `Src/Netor.Cortana.AI/AIAgentFactory.cs` | 根据当前配置构建 `AIAgent`，注入插件、MCP、技能、历史上下文 | 工作步骤执行时通过它构建实际执行用的 Agent |
| `Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs` | 给 Agent 提供历史上下文，支持把 Workflow 结果回写到 Chat | 工作任务完成后用现有回写能力进入当前会话 |
| `Src/Netor.Cortana.Entitys/Events.cs` | conversation 事件、session 事件、workflow 建议事件 | 需要补充工作任务生命周期事件 |
| `Src/Netor.Cortana.Entitys/CortanaDbContext.cs` | 已有 `ChatSessions / ChatMessages` 和 `OrchestrationTask / OrchestrationStep / OrchestrationMessage / WorkflowCheckpoints` 表 | 工作模式优先使用这些表落库，不另起一套任务存储 |
| `Src/Netor.Cortana.Networks/WebSockets/Serialization/WebSocketJsonContext.cs` | 已有 workflow 历史回放 DTO | 后续工作任务事件可以继续走 workflow topic |

因此 `10` 的重点不是定义 UI 控件，也不是画项目目录树，而是把这些已有能力串成可执行的任务运行逻辑。

## 三、数据边界

工作模式需要同时维护两类数据。

### 3.1 对话数据

自然语言对话继续归入当前 `ChatSession`：

- 用户提出需求。
- AI 追问需求。
- AI 给出计划说明。
- 用户修改计划。
- 任务完成后的总结消息。

这些内容进入 `ChatMessages`，保持和普通对话一致的上下文连续性。

### 3.2 任务数据

可执行任务进入已有 workflow 表：

- `OrchestrationTask`：一项完整任务。
- `OrchestrationStep`：主步骤和子步骤。
- `OrchestrationMessage`：步骤内部的思考、工具调用、验收、错误、重试记录。
- `WorkflowCheckpoints`：长任务暂停恢复用的执行检查点。

任务必须通过 `OrchestrationTask.SourceSessionId` 关联到当前聊天会话。

用户说“再按上一个任务做一份”时，可以通过当前会话找到最近完成的 `OrchestrationTask`，把它作为新任务的参考来源。

### 3.3 计划存储

确认后的计划不要只存成一段 Markdown。

主步骤和子步骤都写入 `OrchestrationStep`：

| 字段 | 用法 |
|------|------|
| `TaskId` | 所属任务 |
| `ParentStepId` | 空值表示主步骤；非空表示子步骤 |
| `Sequence` | 当前层级内的顺序 |
| `Action` | 步骤标题 |
| `Status` | `pending / running / completed / failed / paused` |
| `SummaryJson` | 步骤目标、验收条件、产物、结构化结果 |

这样时间线可以从数据库恢复，而不是依赖 UI 内存对象。

### 3.4 步骤明细存储

步骤执行中的每条明细写入 `OrchestrationMessage`：

| Role | Content 示例 |
|------|--------------|
| `thinking` | 当前子步骤的分析、选择的执行策略 |
| `tool` | 工具名、参数摘要、执行结果 |
| `acceptance` | 验收结论、失败原因、重试建议 |
| `assistant` | 当前步骤输出给用户看的说明 |
| `error` | 异常、取消、超时、权限不足 |

UI 的卡片只负责展示这些记录，不能成为唯一数据源。

## 四、任务状态机

一项任务只允许处于一个明确状态：

| 状态 | 含义 |
|------|------|
| `clarifying` | 正在补齐需求 |
| `plan_drafting` | 正在生成计划 |
| `plan_review` | 等待用户用自然语言确认或修改计划 |
| `executing` | 正在按步骤执行 |
| `paused` | 用户打断、等待授权、等待继续或应用退出恢复 |
| `summarizing` | 正在生成最终总结 |
| `completed` | 任务完成 |
| `failed` | 任务失败，已无法自动继续 |
| `cancelled` | 用户明确取消 |

当前会话里最多只有一个活跃任务。

活跃任务定义：

```text
SourceSessionId = 当前 ChatSessionId
Status in (clarifying, plan_drafting, plan_review, executing, paused, summarizing)
按 LastActiveTimestamp 倒序取第一条
```

如果没有活跃任务，用户的新输入可以创建新任务，也可以被当成普通对话处理，具体由输入意图判断决定。

## 五、永续对话实现

永续对话的核心是：`ChatSession` 不随任务结束而结束。

### 5.1 会话不切断

工作模式启动后继续使用当前会话 ID。

禁止在以下时机自动调用新会话逻辑：

- 用户确认计划。
- 任务开始执行。
- 任务完成。
- 任务失败。
- 用户继续提出新任务。

只有用户主动点击新建会话，才创建新的 `ChatSession`。

### 5.2 每轮输入都先进入路由

用户每次输入后，先判断当前会话是否有活跃任务：

```text
有活跃任务
→ 交给任务状态机处理

没有活跃任务
→ 判断是否是新任务
   → 是：创建 OrchestrationTask
   → 否：按普通对话处理
```

这里的“判断”由 AI 或轻量规则完成，但结果必须落成明确的动作：

- 继续补充需求。
- 修改计划。
- 确认开始。
- 暂停当前执行。
- 继续执行。
- 取消任务。
- 创建新任务。
- 普通聊天回复。

### 5.3 完成后回写上下文

任务完成后要做两件事：

1. 更新 `OrchestrationTask.FinalReport`、`Status=completed`、`CompletedAt`。
2. 调用现有的聊天历史回写能力，把最终总结作为 assistant 消息写入当前 `ChatSession`。

这样用户继续说：

```text
再做一份 5 月的
把刚才那个报告改成给销售主管看的
这次不要发邮件，只导出 PDF
```

AI 能通过当前会话历史和最近任务记录理解上下文。

## 六、循环任务实现

工作模式是一个循环，不是一次性向导。

### 6.1 外层循环

外层循环按会话持续运行：

```text
等待用户输入
→ 判断当前会话活跃任务
→ 处理输入
→ 输出 AI 回复或任务事件
→ 如果任务完成，保留会话并回到等待用户输入
```

任务完成后不退出工作模式。

### 6.2 单任务循环

单个任务内部循环：

```text
创建任务
→ 需求补齐
→ 计划生成
→ 计划确认或修改
→ 执行主步骤
   → 执行子步骤
      → 记录思考
      → 调用工具
      → 验收结果
      → 失败则重试或请求用户介入
   → 主步骤完成
→ 汇总结果
→ 写回会话
→ 标记任务完成
```

其中“计划确认或修改”不是按钮流程，而是自然语言流程：

- 用户说“可以”“开始”“执行”时进入执行。
- 用户说“把第三步改成...”时重新生成完整计划。
- 用户补充新约束时回到需求补齐或计划生成。

## 七、输入处理流程

工作模式输入处理建议按以下顺序执行。

### 7.1 接收输入

1. 读取当前 `ChatSessionId`。
2. 保存用户输入到 `ChatMessages`。
3. 查找当前会话活跃任务。
4. 根据活跃任务状态选择处理分支。

### 7.2 没有活跃任务

如果用户输入是明确工作目标：

1. 创建 `OrchestrationTask`。
2. 写入 `InitialInput`、`SourceSessionId`、`WorkspaceId`、`Status=clarifying`。
3. 让 AI 判断需求是否足够。
4. 不足则追问。
5. 足够则进入计划生成。

如果用户输入不是工作目标，则走普通对话回复，不创建任务。

### 7.3 正在补齐需求

AI 每次只处理一个动作：

- 发现缺口：继续追问。
- 信息足够：输出需求摘要，并进入 `plan_drafting`。
- 用户推翻前面内容：更新任务需求快照，继续补齐。
- 用户取消：`Status=cancelled`。

需求快照可以写入 `OrchestrationTask.OverridesJson`，用于后续生成计划。

### 7.4 正在确认计划

计划确认阶段必须完整输出计划。

用户修改计划时：

1. 保留旧计划步骤记录。
2. 生成新的完整计划。
3. 用新的计划覆盖未执行的步骤集合。
4. 已执行步骤不得被删除，只能追加修正步骤或新任务。

用户确认开始后：

1. `OrchestrationTask.Status=executing`。
2. 所有计划步骤初始为 `pending`。
3. 开始执行第一个主步骤的第一个子步骤。

### 7.5 正在执行

执行中收到用户输入时，先暂停当前执行：

1. 取消当前执行令牌。
2. 保存当前步骤状态。
3. 将任务状态改为 `paused`。
4. 分析用户输入意图。

意图处理：

| 用户意图 | 处理方式 |
|---------|----------|
| “继续” | 从暂停点继续执行 |
| “停一下” | 保持 `paused` |
| “取消” | 标记 `cancelled` |
| “这一步重做” | 当前子步骤追加重试记录 |
| “改计划” | 回到 `plan_review`，重新展示完整计划 |
| “换需求” | 回到 `clarifying` |
| “另做一个” | 当前任务暂停或收尾后创建新任务 |

## 八、步骤执行落地

任务执行不能只让 AI 写“我完成了”。

每个子步骤都必须产生可检查的执行结果。

### 8.1 子步骤执行流程

```text
读取当前任务和步骤
→ 标记子步骤 running
→ 构造执行上下文
→ 调用 AI / 工具完成实际动作
→ 记录工具调用和输出
→ 验收结果
→ 通过则 completed
→ 不通过则追加重试或进入 paused
```

执行上下文至少包含：

- 用户原始目标。
- 当前确认后的需求快照。
- 完整计划。
- 当前主步骤。
- 当前子步骤。
- 已完成步骤的摘要。
- 可用工具和授权规则。
- 当前工作目录。

### 8.2 结果证据

子步骤完成时，`SummaryJson` 至少记录：

```json
{
  "summary": "本步骤完成了什么",
  "artifacts": [
    {
      "type": "file",
      "path": "reports/sales-2026-05.pdf",
      "description": "生成的销售月报"
    }
  ],
  "toolCalls": [
    {
      "name": "export_pdf",
      "status": "success"
    }
  ],
  "acceptance": {
    "verdict": "pass",
    "reason": "文件存在，页数和数据范围符合计划"
  }
}
```

如果任务目标是生成文件，就要记录文件路径。

如果任务目标是调用外部接口，就要记录接口结果、返回 ID 或失败原因。

如果任务目标是分析数据，就要记录数据来源、筛选条件、关键结果和校验方式。

### 8.3 验收规则

每个子步骤执行后都要验收。

验收不只看 AI 文本，还要尽量检查真实产物：

- 文件是否存在。
- 数据行数是否符合预期。
- 工具返回是否成功。
- 关键字段是否为空。
- 用户要求的格式是否满足。
- 高风险动作是否已经授权。

验收失败时：

1. 写入一条 `acceptance` 明细。
2. 不修改之前的工具记录。
3. 追加新的重试明细。
4. 超过重试次数后进入 `paused`，让用户选择继续、修改或取消。

## 九、主步骤与子步骤推进

主步骤不直接执行，实际执行单位是子步骤。

推进规则：

1. 找到第一个 `pending` 主步骤。
2. 在该主步骤下找到第一个 `pending` 子步骤。
3. 执行子步骤。
4. 子步骤完成后继续同一主步骤下一个子步骤。
5. 主步骤下所有子步骤完成后，主步骤标记完成。
6. 进入下一个主步骤。
7. 所有主步骤完成后进入总结。

如果执行中发现前置步骤逻辑错误：

- 不删除历史步骤。
- 在当前主步骤下追加修正子步骤，或把任务退回 `plan_review`。
- UI 继续展示历史记录，新的执行记录追加在后面。

## 十、工具授权逻辑

授权是执行链路的一部分，不是 UI 装饰。

### 10.1 高风险工具识别

工具执行前需要判断风险：

- 写文件、删文件、移动文件。
- 发送邮件、提交网络请求、调用外部业务接口。
- 执行 Shell 命令。
- 修改配置、安装依赖、启动或停止进程。
- 涉及用户隐私或凭据的动作。

高风险工具必须先检查授权规则。

### 10.2 授权规则范围

| 选择 | 生效范围 | 建议存储 |
|------|----------|----------|
| 本次允许 | 当前工具调用 | 内存中的当前请求 |
| 本次会话允许 | 当前 `ChatSession` | 运行时内存，可按需写入会话级设置 |
| 永远允许 | 当前用户配置 | `SystemSettings` |
| 拒绝 | 当前工具调用失败 | 写入步骤明细 |
| 替代建议 | 当前任务继续规划 | 写入 `OrchestrationMessage` 并重新决策 |

### 10.3 执行等待

当工具需要授权：

1. 当前子步骤保持 `running`。
2. 任务状态可临时标记为 `paused`。
3. 发布授权请求事件。
4. UI 显示授权浮窗。
5. 用户选择后回填执行链路。
6. 继续工具调用或走替代方案。

授权请求必须有请求 ID，避免用户响应错配到旧工具调用。

## 十一、暂停和恢复

工作模式必须把“暂停”当成正常流程。

### 11.1 暂停触发

- 用户执行中发送新输入。
- 用户点击停止。
- 工具等待授权。
- 应用关闭。
- 执行异常但可恢复。

### 11.2 暂停时保存

暂停前必须保存：

- 当前任务状态。
- 当前主步骤和子步骤。
- 已写入的步骤明细。
- 当前执行上下文摘要。
- 可恢复的 checkpoint。

如果使用 Microsoft Agent Framework 的 workflow checkpoint，就写入 `WorkflowCheckpoints`。

如果当前执行不是 workflow checkpoint 模式，也要至少保存到：

- `OrchestrationTask.Status`
- `OrchestrationStep.Status`
- `OrchestrationMessage`
- `OrchestrationTask.OverridesJson`

### 11.3 恢复流程

恢复时：

1. 查询当前会话未完成任务。
2. 重建任务状态。
3. 从 `OrchestrationStep` 找到第一个未完成子步骤。
4. 从 `OrchestrationMessage` 恢复已经展示过的明细。
5. 如果有 checkpoint，优先恢复 checkpoint。
6. 如果没有 checkpoint，从步骤边界继续执行。

应用重启后不要静默自动执行高风险步骤。

如果任务停在工具调用前或授权前，应先给用户一条说明，等待用户输入“继续”。

## 十二、事件驱动

UI 不应该直接读取执行器内部对象。

任务运行逻辑应按“先落库，再发事件”的顺序工作：

```text
更新 OrchestrationTask / Step / Message
→ 发布任务事件
→ UI 根据事件追加或更新展示
```

需要补充的任务事件类型：

| 事件 | 用途 |
|------|------|
| `task.created` | 新任务创建 |
| `task.stage.changed` | 需求、计划、执行、暂停、总结等状态变化 |
| `task.plan.updated` | 计划生成或重写 |
| `step.started` | 主步骤或子步骤开始 |
| `step.message.appended` | 思考、工具、验收、错误等明细追加 |
| `step.completed` | 步骤完成 |
| `approval.requested` | 工具授权请求 |
| `approval.resolved` | 授权已处理 |
| `task.completed` | 任务完成 |
| `task.failed` | 任务失败 |

如果 UI 中途错过事件，可以通过数据库重建当前任务显示。

## 十三、与普通对话的关系

工作模式不能破坏普通对话路径。

普通对话仍由现有 `IAiChatEngine.SendMessageAsync` 处理。

工作模式新增的是任务路由：

```text
WorkMode 输入
→ 任务状态路由
→ 需要普通回答：走现有对话引擎
→ 需要任务执行：走工作任务状态机
```

任务执行内部可以复用：

- 当前默认模型。
- 当前默认智能体配置。
- `AIAgentFactory` 的工具注入能力。
- `ChatHistoryDataProvider` 的历史上下文。
- `RealtimeProcessEvent` 的思考和工具过程表达。
- `ChatHistoryDataProvider.AppendAssistantMessageAsync` 的结果回写能力。

但工作任务状态不能只存在于 `AiChatHostedService` 的局部变量里，必须落到 workflow 任务表。

## 十四、实施顺序

### 阶段 1：接通真实输入和任务状态

- [ ] 移除 `WorkModeView.LoadDemoConversation()` 的自动演示填充。
- [ ] 工作模式输入提交后进入任务路由。
- [ ] 创建 `OrchestrationTask` 并关联当前 `ChatSession`。
- [ ] 实现 `clarifying / plan_drafting / plan_review` 状态流转。
- [ ] 用户修改计划时重新输出完整计划。
- [ ] 用户确认计划后写入 `OrchestrationStep`。

### 阶段 2：执行步骤落库

- [ ] 按主步骤、子步骤推进任务。
- [ ] 每个步骤开始、完成、失败都更新 `OrchestrationStep`。
- [ ] 思考、工具、验收、错误、重试写入 `OrchestrationMessage`。
- [ ] UI 从事件展示时间线，刷新后可从数据库重建。

### 阶段 3：真实工具执行

- [ ] 步骤执行通过 `AIAgentFactory` 构建 Agent。
- [ ] 注入当前可用插件、MCP、技能和工作目录上下文。
- [ ] 工具调用结果写入步骤明细。
- [ ] 子步骤输出必须有结果证据。
- [ ] 验收失败时追加重试，不覆盖历史。

### 阶段 4：授权闭环

- [ ] 为高风险工具加统一授权拦截。
- [ ] 发布授权请求事件。
- [ ] 接收授权浮窗返回结果。
- [ ] 本次允许、本次会话允许、永远允许、拒绝、替代建议分别处理。
- [ ] 授权结果写入任务明细。

### 阶段 5：暂停、恢复和完成回写

- [ ] 执行中用户输入会暂停当前任务。
- [ ] 保存任务状态和 checkpoint。
- [ ] 支持用户输入“继续”后恢复。
- [ ] 任务完成后写入 `FinalReport`。
- [ ] 使用现有聊天历史回写能力把总结写回当前会话。
- [ ] 当前会话继续等待下一句话。

### 阶段 6：历史和外部同步

- [ ] 左侧工作记录从 `OrchestrationTask` 查询。
- [ ] 任务详情从 `OrchestrationStep / OrchestrationMessage` 重建。
- [ ] workflow WebSocket topic 支持任务事件发布。
- [ ] 历史回放使用已有 workflow history replay 机制扩展。

## 十五、验收标准

- [ ] 工作模式任务完成后，当前会话不结束。
- [ ] 用户能继续说“再做一份”“按刚才方案改一下”，系统能找到上一个任务上下文。
- [ ] 一项任务至少能完整走通：需求补齐、计划生成、计划确认、步骤执行、总结回写。
- [ ] 确认后的计划写入 `OrchestrationStep`，不是只显示 Markdown。
- [ ] 步骤执行过程写入 `OrchestrationMessage`，刷新后可以恢复。
- [ ] 子步骤完成时有真实结果证据。
- [ ] 工具调用失败或验收失败时追加重试记录，不覆盖历史。
- [ ] 执行中用户插话会暂停任务，并能继续或修改。
- [ ] 高风险工具必须经过授权闭环。
- [ ] 任务完成后的总结进入 `ChatMessages`，成为后续对话上下文。
- [ ] UI 只消费任务事件和持久化记录，具体视觉仍以 `09` 为准。

## 十六、最终效果

最终工作模式应该表现为：

```text
用户：帮我做一份销售月报
AI：补齐必要需求
用户：确认
AI：输出完整计划
用户：开始
系统：按计划执行每个子步骤，记录工具、结果和验收
系统：遇到高风险工具时请求授权
系统：失败时重试，必要时暂停等用户修改
AI：任务完成后总结结果
用户：再做一份 5 月的
系统：基于同一个会话和上一个任务继续创建新任务
```

这才是工作模式的核心：不是一次对话，也不是一张时间线，而是在同一个会话里持续产生、执行、恢复和沉淀任务。
