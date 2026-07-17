# Cortana 会议模式方案策划

> 思维整理稿（待评审）—— 用户确认理解后再细化为完整实施方案
> 当前状态：📝 思路对齐 v2，待最终拍板

---

## 一、产品定位

**会议模式**是与"对话模式 / 工作模式"并列的第三种交互模式。

| 模式 | 比喻 | 角色 | 核心动作 |
|------|------|------|---------|
| 对话模式 | 找一个专家咨询 | 1 个 AI + 用户 | 问答 |
| 工作模式 | 雇一个员工干活 | 总经理 AI（一人公司）+ 用户 | 派活、执行、汇报 |
| **会议模式** | **召集团队开会** | **多个 AI + 主持人 + 老板（用户）** | **围绕主题讨论 → 总结** |

### 1.1 老板（用户）的角色边界（核心）

**用户在会议中扮演的是"老板"，遵循现实公司会议的权力结构**：

| 谁能做什么 | 主持人 | 智能体 | 老板（用户） |
|-----------|-------|--------|-----------|
| 主动发言 | ✓（控场 + 总结） | ✗（必须被点名） | **✓（随时插话）** |
| 被点名发言 | ✗ | ✓ | **✗（主持人不能点老板的名）** |
| 被征询意见 | ✗ | ✗ | **✓（主持人会礼貌地征求意见）** |
| 决定会议是否结束 | ✗（只能建议） | ✗ | **✓（最终决定权）** |

**关键不对称**：主持人**有义务征求**老板的意见，**没有权利点老板发言**。智能体之间可以被主持人安排来安排去，但老板的发言完全自主——要么主动插话，要么对主持人的征询作出回应。

### 1.2 整体交互原则

- **全程对话流**：所有的问询、确认、总结都以**普通气泡**形式插入消息流，通过输入框作答。**禁止任何浮窗对话框、确认按钮、单选框**（与工作模式的"零交互按钮"原则一致，无例外）
- **类专家模式视觉**：每条发言都带头像 + 身份标识 + 时间戳 + Markdown 气泡（参考 [ChatView.AddMessageBubble](Src/Netor.Cortana.UI/Controls/ExpertMode/ChatView.axaml.cs#L190)）
- **群聊节奏**：左对齐为他人发言（智能体/主持人），右对齐为老板发言

---

## 二、需求要点（已和用户对齐）

1. 输入框下方多选智能体作为参会成员
2. 老板输入**会议主题** + 可选附件
3. 由**主持人智能体**安排发言顺序
4. 智能体逐条发言，**类似群聊**（每条带头像 + 身份）
5. 每条发言下方展示**思考过程 + 工具调用**
6. 智能体可看附件、调用工具收集信息
7. **会议必须有序对话**；**老板可插话但不被点名**
8. 主持人判定会议应结束时：
   - **主持人作为一条普通发言** → "我来总结一下今天的讨论：……"（输出总结）
   - **主持人作为下一条普通发言** → "老板您看，今天的议题是不是可以告一段落了？"（征询）
   - **老板在输入框作答** → "可以结束" / "再讨论 X" / 任何修订意见
   - 老板表态结束后会议才真正归档
   - **整个过程没有任何浮窗**
9. 持久化用**新表**，消息记录通过 **WS 分发**给外部客户端
10. 工具：**系统默认工具** + 各智能体已绑定的插件（`AIAgentFactory` 自动注入）
11. 整个功能**和 AF 框架对齐**

---

## 三、AF 框架映射

### 3.1 编排核心：GroupChat + 自定义 GroupChatManager

```csharp
// AF 提供的入口
AgentWorkflowBuilder.CreateGroupChatBuilderWith(agents =>
        new MeetingHostManager(orchestratorAgent, ...))
    .AddParticipants(participatingAgents)   // 仅智能体，不含"老板"
    .Build();
```

| AF 抽象 | 我们的角色 | 职责 |
|---------|-----------|------|
| `GroupChatManager.SelectNextAgentAsync` | 主持人选下一位发言者 | 只在智能体集合中选；**永远不能选老板** |
| `GroupChatManager.ShouldTerminateAsync` | 判断会议该不该结束 | 判定后不立即终止：先发"总结"消息 + "征询"消息，等老板的对话流回复 |
| `GroupChatManager.UpdateHistoryAsync` | 把老板的插话注入对话 | 从 `MeetingPendingInputs` 队列消费用户消息 |
| `MaximumIterationCount` | 死循环兜底 | 固定 40（当前实现） |

源码参考：
- 工程实际使用 `Microsoft.Agents.AI` / `.OpenAI` / `.Workflows` NuGet 1.7.0
- 本地源码参考仍为 v1.6.0（仅用于协议定位）：
- [E:/OpenSourse/agent-framework-main.v1.6.0/dotnet/src/Microsoft.Agents.AI.Workflows/GroupChatManager.cs](file:///E:/OpenSourse/agent-framework-main.v1.6.0/dotnet/src/Microsoft.Agents.AI.Workflows/GroupChatManager.cs)
- [E:/OpenSourse/agent-framework-main.v1.6.0/dotnet/src/Microsoft.Agents.AI.Workflows/GroupChatWorkflowBuilder.cs](file:///E:/OpenSourse/agent-framework-main.v1.6.0/dotnet/src/Microsoft.Agents.AI.Workflows/GroupChatWorkflowBuilder.cs)
- 示例：`samples/03-workflows/Agents/GroupChatToolApproval/`

### 3.2 主持人智能体

- **独立的系统主持人 Agent**，不占参会名额、不可被用户选中、不可被点名（它本身就是控场者）
- 提示词：`prompts/meeting/host.md`，明确以下硬约束：
  - 只能从智能体参会名单中选发言者，**不能要求老板发言**
  - 在合适的时机用**普通对话**主动征求老板意见（语气："老板您怎么看？""我们这个方向您是否认可？"）
  - 当判定议题已成熟时：先发**总结消息**（一段独立 markdown），紧跟一条**征询消息**（"我们今天的讨论是否可以告一段落了？"），然后**等待老板回复**
- 三项核心职责：
  1. **选发言**（只在智能体范围）
  2. **判进度**（议题是否充分、是否陷入打转、是否到收尾节点）
  3. **写总结 + 征询**（结束流程的两步对话）

### 3.3 智能体参会者

- 直接复用 [AIAgentFactory.Build(agent, provider, model, tools)](Src/Netor.Cortana.AI/AIAgentFactory.cs)
- 工具集 = **系统默认工具** + 该智能体绑定的插件（AF 自动注入，无需特殊处理）
- 附件通过 `ChatMessage` 的 `DataContent` 注入到首轮消息

### 3.4 老板（用户）的"非参会者"接入

老板**不在 `AddParticipants` 列表里**，主持人也不能选它。老板进入对话流的两个唯一入口：

#### A) 主动插话（任意时刻）

```
老板在输入框敲字 + 发送
  ↓
INSERT MeetingPendingInputs (Status=待消费)
  ↓
当前发言者完成本轮 → 主持人 SelectNextAgentAsync 之前
  ↓
MeetingHostManager.UpdateHistoryAsync 消费队列
  ↓
把老板消息以 ChatRole.User 注入 history
  ↓
主持人据此决定下一位（可能是回应老板的某位专家，可能是主持人自己再总结一下）
```

特点：**不打断当前发言者**，避免 history 不自洽；老板的输入立刻显示在 UI 上（右侧带头像气泡），但 AF 工作流要等当前 superstep 结束才接收。

#### B) 回应主持人的征询（HITL 挂起态）

主持人输出"征询消息"后，工作流进入 `RequestInfoEvent` 自然挂起：

```
主持人发言："老板您看是否可以结束？"
  ↓
publish meeting.message.completed (speakerKind=host, awaitingUser=true)
  ↓
RequestInfoEvent → workflow 挂起
  ↓
UI 状态：输入框走马灯亮（提示"主持人在等您回复"）
  ↓
老板敲字 + 发送
  ↓
run.SendResponseAsync(老板回复)
  ↓
主持人解析回复：
  ├─ 表示同意结束 → ShouldTerminateAsync 返回 true → WorkflowOutputEvent
  ├─ 表示继续讨论 → 注入到 history,继续 SelectNextAgentAsync
  └─ 表示要修改总结 → 主持人重写一版总结再次征询
```

老板**没有专门的"结束按钮"**，AI 通过自然语言理解他的回复意图。

#### 两种入口的统一

UI 层面老板**始终只有一个输入框**，不需要区分两种模式：

```csharp
// 伪代码
void OnUserSubmit(string text)
{
    if (currentMeeting.PendingRequestId != null)
    {
        // 主持人正在征询 → 当作 HITL 回复
        await executor.ResumeAsync(currentMeeting.Id, text);
    }
    else
    {
        // 普通插话 → 进队列
        await pendingInputs.EnqueueAsync(currentMeeting.Id, text);
    }
}
```

输入框上方一行小提示文字（不是浮窗，只是文字状态）：
- 普通态：`X 位智能体讨论中，可随时插话`
- 挂起态：`主持人在等您的回复`

### 3.5 事件流（Netor.EventHub + WS 分发）

参照 [Events.cs:135-192](Src/Netor.Cortana.Entitys/Events.cs#L135-L192) 工作模式事件命名风格，新增 `meeting.*` 事件族。**所有事件**都通过 EventHub 发布，自然支持 WS 分发：

| 事件 | 时机 | 关键载荷 |
|------|------|---------|
| `meeting.created` | 会议建立 | meetingId, sessionId, topic, participants[] |
| `meeting.speaker.changed` | 主持人选定下一发言者 | meetingId, speakerId, speakerKind(`agent`/`host`) |
| `meeting.message.delta` | 流式 token | meetingId, messageId, speakerId, deltaText |
| `meeting.message.completed` | 单条发言定稿 | meetingId, messageId, fullText, awaitingUser(bool) |
| `meeting.tool.call` | 工具调用 | meetingId, messageId, toolName, args |
| `meeting.tool.result` | 工具返回 | meetingId, messageId, toolName, result |
| `meeting.thinking.delta` | 思维链流式 | meetingId, messageId, thinkingText |
| `meeting.user.spoke` | 老板发言（插话或回复） | meetingId, messageId, text, kind(`interrupt`/`reply`) |
| `meeting.summary.draft` | 主持人产出总结草稿（一条特殊气泡） | meetingId, summaryMarkdown |
| `meeting.completed` | 老板确认结束 | meetingId, finalSummary |
| `meeting.cancelled` | 取消 | meetingId |

**取消了** `meeting.confirm.end` 浮窗事件——征询通过 `meeting.message.completed (awaitingUser=true)` 一条事件即可表达。

---

## 四、数据模型（新表）

设计原则：会议是封闭事件，元数据关联 `ChatSessions`；结束时把"最终总结"作为一条 assistant 消息写回 `ChatMessages`，方便对话模式回看。

### 4.1 `MeetingSessions`

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | TEXT PK | 会议 ID |
| SessionId | TEXT | 关联 ChatSession |
| Topic | TEXT | 会议主题 |
| HostAgentId | TEXT | 主持人 Agent ID（系统默认） |
| ParticipantsJson | TEXT | `[{agentId,name,joinOrder}, ...]`（仅智能体，不含老板） |
| Status | INT | 0=进行中 / 1=等待老板回复 / 2=已结束 / 3=已取消 |
| RunId | TEXT | AF StreamingRun.RunId |
| PendingRequestId | TEXT | HITL 挂起的 RequestId（征询时非空） |
| FinalSummaryMd | TEXT | 最终总结 Markdown |
| CreatedAt / UpdatedAt | INT | Unix ts |

### 4.2 `MeetingMessages`

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | TEXT PK | |
| MeetingId | TEXT FK | |
| Sequence | INT | 发言顺序（单调递增） |
| SpeakerKind | TEXT | `agent` / `host` / `user` |
| SpeakerId | TEXT | AgentId 或固定的 `boss` |
| SpeakerName | TEXT | 冗余存储显示名 |
| ContentMd | TEXT | 正文 Markdown |
| ThinkingMd | TEXT | 思维链（智能体用） |
| ToolCallsJson | TEXT | `[{name,args,result,durationMs}, ...]` |
| AttachmentsJson | TEXT | `[{name,path,mime}, ...]` |
| MessageRole | TEXT | `normal` / `summary` / `inquiry`（区分主持人的总结消息和征询消息，UI 可特殊渲染） |
| AwaitingUserReply | INT | 0/1，工作流是否在此条之后挂起等待老板 |
| CreatedAt | INT | |

### 4.3 `MeetingPendingInputs`

老板插话队列。仿 `WorkPendingInputs` 设计。

| 字段 | 类型 | 说明 |
|------|------|------|
| Id | TEXT PK | |
| MeetingId | TEXT | |
| Text | TEXT | |
| AttachmentsJson | TEXT | |
| Kind | TEXT | `interrupt`(主动插话) / `reply`(回应征询) |
| Status | INT | 0=待消费 / 1=已注入 |
| CreatedAt | INT | |

### 4.4 `MeetingAttachments`

会议级附件（老板在主题处一次上传，所有智能体共享）。

> AOT 安全：所有 JSON 列继续走 `JsonSerializerContext` 源生成，新增 `MeetingJsonContext`。

---

## 五、UI 接入（仿专家模式 + 新增多选）

### 5.1 输入区改造

[InputAreaView.axaml.cs:1023](Src/Netor.Cortana.UI/Controls/ExpertMode/InputAreaView.axaml.cs#L1023) 的 `InputMode` 枚举增补 `Meeting`：

| 元素 | Chat | Workflow | **Meeting** |
|------|------|---------|---------|
| 厂商/模型按钮 | ✓ | ✓ | ✓（兜底用——给未绑定模型的智能体使用） |
| 智能体按钮 | 单选 | 单选 | **多选 + 已选数量徽标** |
| 附件按钮 | ✓ | ✓ | ✓（会议级附件） |
| `@` 智能体引用 | ✓ | ✓ | ✓（点名某位补充） |
| `#` 文件引用 | ✓ | ✓ | ✓ |
| 走马灯颜色 | 蓝 | 橙 | **紫 #A06CFF** |
| 输入框上方提示文字 | 无 | 无 | **状态提示**（"X 位讨论中" / "主持人在等您回复"） |

输入框上方一行"成员条"：`会议成员: [财务张三×] [法务李四×] [技术老王×] + 添加`。点击多选弹窗用 CheckBox 取代单选 Button。

### 5.2 消息区控件（**完全仿专家模式**）

直接复用 [ChatView.AddMessageBubble](Src/Netor.Cortana.UI/Controls/ExpertMode/ChatView.axaml.cs#L190) 的视觉模式：

```
左对齐（agent / host）：              右对齐（user / 老板）：

[40x40 头像]  [名字 11px][时间 10px]                       [名字][时间]  [40x40 头像]
              ┌─────────────────┐              ┌─────────────────┐
              │ Markdown 气泡    │              │ Markdown 气泡    │
              │ ▾ 思考           │              └─────────────────┘
              │ ▾ 调用工具 xxx   │
              └─────────────────┘
```

| 角色 | 头像 | 名字 | 气泡颜色 | 对齐 |
|------|------|------|---------|------|
| 老板（用户） | UserAvatarBitmap（蓝色用户图标） | "老板" | UserBubbleBrush | **右** |
| 智能体 | 智能体绑定的头像 / AiAvatarBitmap | AgentEntity.Name | AiBubbleBrush | **左** |
| 主持人 | 专属主持人头像（小别针 / 议长锤等图标） | "主持人" | AiBubbleBrush + 左侧 2px 紫色色条标识 | **左** |
| 主持人总结消息 | 同上 | "主持人 · 会议总结" | AiBubbleBrush + **整体淡紫色背景** | **左** |
| 主持人征询消息 | 同上 | "主持人 · 征询您的意见" | AiBubbleBrush + 输入框走马灯亮起 | **左** |

**思考/工具卡片**直接放在气泡内部（嵌入 markdown 之上的折叠卡片）：复用 [RealtimeProcessCard](Src/Netor.Cortana.UI/Controls/Common/RealtimeProcessCard.axaml)。可以在新的 `MeetingBubbleBuilder` 里把现有 `AddMessageBubble` 加一个 `prependedCards` 参数版本。

### 5.3 视图控制器

新增 `MeetingViewController.cs`，结构对齐 [WorkModeViewController](Src/Netor.Cortana.UI/Controls/WorkMode/WorkModeViewController.cs)：

- 订阅 `meeting.*` 事件 → `Dispatcher.UIThread.Post` 更新控件
- 看到 `awaitingUser=true` 的消息 → 启动输入框走马灯 + 切换提示文字
- 老板提交输入 → 根据 `PendingRequestId` 状态分流：HITL Resume 或 入队插话
- AOT 安全：纯 code-behind，无 Binding，无反射

---

## 六、整体数据流

```
老板：选定参会者[A, B, C] + 输入主题 + 上传附件 + 点发送
  ↓
MeetingInputRouter.RouteAsync(topic, agentIds, attachments)
  ├─ INSERT MeetingSessions
  ├─ INSERT MeetingAttachments
  └─ MeetingExecutor.ExecuteAsync(meetingId)
       │
       ├─ 构建 host = AIAgentFactory.Build(系统主持人 Agent)
       ├─ 构建 participants = [Build(A), Build(B), Build(C)]
       ├─ workflow = AgentWorkflowBuilder
       │     .CreateGroupChatBuilderWith(_ => new MeetingHostManager(host, ...))
       │     .AddParticipants(participants)        // 不含老板
       │     .Build();
       │
       └─ InProcessExecution.RunStreamingAsync(workflow, openingMessage)
              │
              └─ 事件流分发（同时落库 + EventHub 广播 → WS）
                   ├─ AgentResponseUpdateEvent        → meeting.message.delta
                   ├─ FunctionCallContent             → meeting.tool.call
                   ├─ FunctionResultContent           → meeting.tool.result
                   ├─ AgentResponseEvent              → meeting.message.completed
                   │     + INSERT MeetingMessages
                   └─ 主持人判定收尾时
                         ├─ 主持人发言（总结）→ message.completed (role=summary)
                         ├─ 主持人发言（征询）→ message.completed (role=inquiry, awaitingUser=true)
                         └─ RequestInfoEvent → 工作流挂起，Status=1

  ↓ (老板敲字)
                                                           ┌─ 主动插话 → enqueue MeetingPendingInputs
   OnUserSubmit(text) ────────────────┐                    │
                                       ├─ 判断 PendingRequestId ─┤
                                       │                    └─ 回应征询 → run.SendResponseAsync(text)
                                       │                                       │
                                       │                                       ├─ "结束" → ShouldTerminateAsync=true
                                       │                                       │     → WorkflowOutputEvent
                                       │                                       │     → meeting.completed
                                       │                                       │     → UPDATE MeetingSessions (Status=2)
                                       │                                       │     → 写一条 assistant 消息进 ChatMessages
                                       │                                       │       (含主题 + 总结 + 参会者 + meetingId)
                                       │                                       │
                                       │                                       └─ "继续讨论 X" → 注入 history → 继续编排
                                       │
                                       └─ 主动插话队列 → UpdateHistoryAsync 在下一轮合并
```

---

## 七、已拍板的关键决策

### 7.1 征询机制：复用 ask_user 工具（程序层 user = user）

**重要澄清**：程序逻辑里 `user` 就是 `user`，不存在"老板"这个程序概念。"老板"只是会议**提示词语义层**对 user 的角色称呼，不影响代码命名。

- **工具名保持 `ask_user`**（不发明 `ask_boss`）
- 主持人提示词里按角色称呼："请用 `ask_user` 工具向老板征询是否结束"
- 主持人 LLM 调用时传的 `question` 参数自然就是 "老板您看……" 之类的话术

#### 现有 ask_user 复用边界

直接照搬不可行——[HitlTools](Src/Netor.Cortana.AI/WorkMode/Tools/HitlTools.cs#L17) 强绑定 `WorkTaskService` 和 `WorkTasks` 表；事件 `OnWorkAskUserRequested` 字段叫 `TaskId`，订阅链路（[WorkModeInputVm](Src/Netor.Cortana.UI/ViewModels/WorkMode/WorkModeInputVm.cs#L91) / [WorkModeViewController](Src/Netor.Cortana.UI/Controls/WorkMode/WorkModeViewController.cs#L350)）按工作任务语义处理。两套上下文混用一个事件流会让订阅者打架。

#### 复用方案：抽公共接口，工具实现共享

```csharp
// 1. 在 Netor.Cortana.AI.Hitl 抽公共接口
public interface IHitlContext
{
    string ContextId { get; }                      // taskId 或 meetingId
    void SetPendingRequest(string requestId, string kind, string snapshotJson);
    void ClearPendingRequest();
}

// 2. WorkTaskService 实现 IHitlContext（适配现有逻辑）
// 3. MeetingSessionService 实现 IHitlContext

// 4. HitlTools 改为面向接口
public sealed class HitlTools
{
    public HitlTools(IHitlContext context, IPublisher publisher, EventID<HitlAskUserArgs> askUserEvent) { ... }
    // CreateAskUserTool() 内部代码 1:1 不变
}

// 5. 事件载荷统一字段名
public record HitlAskUserArgs(string ContextId, string RequestId, string Question);

// 6. 事件分两个名字（避免订阅打架）
public static WorkAskUserEvent OnWorkAskUserRequested = new("work.askuser.requested");
public static MeetingAskUserEvent OnMeetingAskUserRequested = new("meeting.askuser.requested");
```

**改造工作量**：

- 在 `Netor.Cortana.AI.Hitl/` 新建公共目录，抽接口（约 50 行）
- `HitlTools.cs` 改构造参数（不动逻辑）
- `WorkTaskService.cs` 加 `IHitlContext` 实现（thin wrapper）
- 两套事件并存（向后兼容工作模式订阅者）

收益：会议模式直接 `new HitlTools(meetingContext, publisher, OnMeetingAskUserRequested).CreateAskUserTool()` 即用。

**这个抽取属于会议模式实施前置工作**，单独列入路线图。

#### 时序

征询语作为**真正的会议消息**写入 `MeetingMessages`（MessageRole=`inquiry`），是会议历史一部分；主持人调用 `ask_user` 触发挂起；UI 端不弹浮窗，只在消息流追加征询气泡 + 输入框上方提示行变 "主持人在等您回复"。老板敲字 → `run.SendResponseAsync(text)` 恢复 → 主持人解析回复意图（结束 / 继续 / 修改总结）。

### 7.2 老板未回复关软件 = 直接散会

- 软件关闭时，对所有 `Status=1 等待用户回复` 的会议直接 `UPDATE Status=3 已取消`
- 不写 checkpoint，不写最终总结到 ChatMessages
- 重开软件 → 历史可见但置灰，标记"未结束的会议（已自动散会）"
- v1.0 不做恢复

### 7.3 @提及不变工具调用（**核心澄清**）

**Handoff 模式**才有"`@` 变工具"机制，**GroupChat 模式**没有：

| 维度 | Handoff | **GroupChat（我们用）** |
|------|---------|------------------------|
| 决定下一发言者 | 当前 Agent LLM 自己 | **主持人 `SelectNextAgentAsync` 单点决策** |
| `@` / 转交触发 | 变工具 `handoff_to_X()` | 纯 markdown 文本 |
| AF 源码 | `HandoffWorkflowBuilder` + `HandoffAgentExecutor` | `GroupChatHost` + `GroupChatManager` |

工作模式 `BuildWithSubAgents` 把 `@提及` 转成 `agent_<id>` 工具是工作模式特有的子智能体调用机制（[WorkflowExecutor.cs:75](Src/Netor.Cortana.AI/WorkMode/WorkflowExecutor.cs#L75)），**不能照搬到会议模式**。

会议模式 `@` = 纯文本提示信号：

- 主持人写"请 @财务张三 谈谈" → 普通 markdown，AF 不做特殊处理
- 工作流推进 → `MeetingHostManager.SelectNextAgentAsync` 调主持人 LLM
- 主持人看到 history 里**自己刚说过的话**，自然延续意图返回 financeAgent
- **不需要 `@` 解析逻辑**，靠 LLM 上下文连贯性

老板的 `@` 同理（建议 ≠ 命令）。这才符合现实公司会议——老板说"小王你说"，主持人通常照办，但有判断权。

**v1.0 不开放智能体之间互相 `@` 点名**，保持主持人单点控场。

### 7.4 上下文压缩 + 历史回放（**核心**）

会议天然容易超长（多人发言 + 长议题 + 用户多次插话补充），必须做上下文压缩，否则 token 会爆炸。同时老板要看完整历史，不能只看摘要。

**两条独立路径**：

| 路径 | 用户 | 数据源 | 用途 |
|------|------|--------|------|
| UI 回放 | 老板回看历史会议 | `MeetingMessages` 全量 | 完整群聊记录（含思考/工具卡） |
| LLM 上下文 | `MeetingHostManager` 决策、智能体下一轮发言 | `MeetingCompactionSegments` 摘要 + 尾部原文 | 防 token 爆炸 |

**关键设计**：

- 直接复用对话模式已有的"段落式不可变摘要"机制（[CompactionSegments](../../../Src/Netor.Cortana.Entitys/CortanaDbContext.cs#L309) + [ChatHistoryDataProvider](../../../Src/Netor.Cortana.AI/Providers/ChatHistoryDataProvider.cs#L728)）
- 会议建独立表 `MeetingCompactionSegments`，不与对话模式压缩段共表（索引语义不一致）
- **`MeetingMessages` 永不删除**，压缩段只是 LLM 上下文的"叠加层"
- 压缩用 `IChatCompactionClientResolver`（已有的压缩专用模型解析器），新增会议特化的提示词 `prompts/meeting/compaction.md`
- 触发时机：未摘要消息超过 `SegmentSize + RawTailSize`（默认 30 + 20 = 50 条）时压缩最早 `SegmentSize` 条
- 详见 [03-数据模型与持久化.md §3.5 / §三-bis / §10](./03-数据模型与持久化.md)

### 7.6 架构关键约束（**v8 三轮推演累计 23 个硬约束**）

会议模式的 5 份策划文档经过三轮现实场景推演（基础设计 / 边界并发错误 / 多智能体认知与数据完整性），发现并修复了若干架构性逻辑缺陷。以下硬约束**实施时不得违反**：

#### v6 第一轮（基础设计层 8 个）

| 约束 | 缺陷源 | 落实位置 |
|------|--------|----------|
| `SelectNextAgentAsync` 入口必须用 `TaskCompletionSource<bool>` 真正阻塞 | 缺陷 1 | [02 §2.0-2.3](./02-AF编排集成.md) |
| 终止由主持人 LLM 调用 `confirm_meeting_end` 工具显式标记 | 缺陷 2 | [02 §三.5](./02-AF编排集成.md) |
| `UpdateHistoryAsync` 完全无视 AF 传入的 `history`，从 DB 重建 | 缺陷 3 | [02 §2.2](./02-AF编排集成.md) + [03 §三-bis](./03-数据模型与持久化.md) |
| 主持人调 `ask_user` 之前必须先调 `check_pending_user_input` | 缺陷 4 | [02 §三.6 + §4.2.D](./02-AF编排集成.md) |
| 附件不内联到 `openingMessage`，超 N=10 时截断 + `list_meeting_attachments` 兜底 | 缺陷 5+19 | [02 §1.4 + §三.7](./02-AF编排集成.md) |
| 主持人和参会者都用 `ToolFilterMode.ReadOnly` 过滤可变工具 | 缺陷 8 | [02 §四.1](./02-AF编排集成.md) |
| 启动时 `MeetingStartupService` 把孤儿会议置 Status=3 | 缺陷 7 | [03 §9.1](./03-数据模型与持久化.md) |
| 主题不明确用主持人提示词约束（不引入 Phase 状态机） | (主题对齐) | [02 §4.2.B](./02-AF编排集成.md) |

#### v7 第二轮（边界并发错误层 12 个）

| 约束 | 缺陷源 | 落实位置 |
|------|--------|----------|
| `ShouldTerminateAsync` 必须保留 `MaximumIterationCount` 兜底 + ForceFinalSummaryAsync | 缺陷 9 | [02 §2.4](./02-AF编排集成.md) |
| `confirm_meeting_end` 自动从 DB 取最近 summary 写入 `FinalSummaryMd` | 缺陷 10 | [02 §三.5](./02-AF编排集成.md) |
| `MeetingSessions` 加唯一索引 `idx_meeting_active_unique` 保证同 session 单活跃会议 | 缺陷 11 | [03 §3.1](./03-数据模型与持久化.md) |
| 主持人 LLM 输出非法 ID 时 `ResolveAgent` 三级回退 | 缺陷 12 | [02 §2.3](./02-AF编排集成.md) |
| 主持人输出总结**必须**调 `output_summary` 工具，不能用 markdown 流式输出 | 缺陷 13 | [02 §三.4 + §4.2.D](./02-AF编排集成.md) |
| 流式中途异常 `MeetingStreamProcessor` 必须 catch + `AppendPartialAsync` 落库 | 缺陷 14 | [03 §3.2](./03-数据模型与持久化.md) + [05 §3.1](./05-实施路线图.md) |
| `MeetingCancellationRegistry` 提供 CTS 注册 + 取消机制 | 缺陷 15 | [03 §4.5](./03-数据模型与持久化.md) |
| Tab 切换时 MeetingView 实例**保留**，事件订阅与可见性解耦 | 缺陷 16 | [04 §5.4](./04-界面交互设计.md) |
| `MeetingHostManager` 维护 `_lastSpeakerId` 防止主持人连续发言 | 缺陷 17 | [02 §2.3](./02-AF编排集成.md) |
| 主持人 LLM 处理 ask_user 回复前必须先回头看自己最近问的什么 | 缺陷 18 | [02 §4.2.E](./02-AF编排集成.md) |
| LLM 推理 / 流式 / 工具三层重试架构（缺陷 20） | 缺陷 20 | [02 §2.6](./02-AF编排集成.md) + [§7.7](#77-重试策略汇总修复缺陷-20) |

#### v8 第三轮（多智能体认知 / 数据完整性层 11 个）

| 约束 | 缺陷源 | 落实位置 |
|------|--------|----------|
| 参会者 `SystemPrompt` 前注入 `MeetingContextHeader` 强化新语境 + 工具被过滤的认知 | 缺陷 21+30+31 | [02 §4.1](./02-AF编排集成.md) + `prompts/meeting/participant_header.md` |
| `MeetingExecutor.ExecuteAsync` 入口校验参会者数量（最少 2 + 软上限 8） | 缺陷 22 | [02 §1.2](./02-AF编排集成.md) + [04 §3.2](./04-界面交互设计.md) |
| `TryCompactAsync` 用 `SemaphoreSlim` 互斥；`BuildLlmHistoryAsync` 用 `BEGIN DEFERRED` 事务读快照 | 缺陷 23 | [03 §三-bis](./03-数据模型与持久化.md) |
| `MeetingMessages` 加 `UNIQUE(MeetingId, Sequence)`；Append 用 `BEGIN IMMEDIATE` 事务 | 缺陷 24 | [03 §3.2 + §4.2](./03-数据模型与持久化.md) |
| `ResumeAsync` 用 `TryClearPending` 乐观锁防止重复点击导致重复消息 | 缺陷 25 | [02 §1.2](./02-AF编排集成.md) + [03 §4.1](./03-数据模型与持久化.md) |
| `MeetingSessions.Provider` / `Model` 字段会议建立时锁定，进行中不可修改 | 缺陷 26 | [03 §3.1](./03-数据模型与持久化.md) + [04 §3.5](./04-界面交互设计.md) |
| `BuildLlmHistoryAsync` 用 `ListCompleteByMeeting` 过滤 `IsPartial=1`；UI 回放仍显示 IsPartial | 缺陷 27 | [03 §三-bis](./03-数据模型与持久化.md) |
| `openingMessage` 暴露给 LLM 的附件路径必须是绝对路径；DB 存相对路径 | 缺陷 28 | [02 §1.4](./02-AF编排集成.md) |
| 主持人提示词分层：SystemPrompt 缓存 + DecisionPrompt 按需注入（v1.0 全量，v1.1+ 渐进） | 缺陷 29 | [02 §4.2 主持人提示词分层](./02-AF编排集成.md) |
| 参会者**没有 ask_user 工具**——需要老板决策时在发言中说明，主持人代为询问 | 缺陷 30 | [02 §4.1 + §4.2.F](./02-AF编排集成.md) + `prompts/meeting/participant_header.md` |
| `MeetingContextHeader` 强化"会议室所有人"语境，覆盖 `AgentEntity.SystemPrompt` 中的"用户提问"假设 | 缺陷 31 | [02 §4.1](./02-AF编排集成.md) |

### 7.7 重试策略汇总（修复缺陷 20）

会议模式的可靠性建立在**三层重试 + 一层兜底** 上：

| 层 | 触发位置 | 重试策略 |
|----|---------|---------|
| **A. LLM 推理层** | `MeetingHostManager.SelectNextAgentAsync` / `ForceFinalSummaryAsync` 调主持人 LLM | `InvokeWithLlmRetryAsync` 仅对 `FailureCategory.Network` 重试，最多 5 次，指数退避 |
| **B. 流式调用层** | 参会者 / 主持人 `RunStreamingAsync` | `RetryingAgent : DelegatingAIAgent` 包装，head-fail（建立连接失败）重试，stream 中途断开走部分消息保存路径 |
| **C. 工具调用层** | 任何 `AIFunction.InvokeAsync` | 复用现有 [RetryingFunctionWrapper](../../../Src/Netor.Cortana.AI/WorkMode/Tools/RetryingFunctionWrapper.cs)，按 [FailureClassifier](../../../Src/Netor.Cortana.AI/WorkMode/Reliability/FailureClassifier.cs) 分类重试 |
| **D. 部分消息保存（兜底）** | 流式中途异常 | `MeetingMessageService.AppendPartialAsync` 写 IsPartial=1 消息，UI 显示"未完成" chip |

每次重试发布 `meeting.llm.retrying` 事件，UI 实时显示"网络抖动,正在重试..."。详见 [02-AF编排集成.md §2.6](./02-AF编排集成.md)。

### 7.8 主题对齐机制（保持不变）

主题不明确 → 主持人引导对齐（用提示词约束，不做固定流程）：

- **不做固定流程**（不引入 `Phase = Clarifying / Discussing` 状态机）
- **用主持人提示词约束**：第一轮主持人被选中时，先做"主题清晰度判断"——明确 → 直接进入正式讨论；模糊 → 引导对齐 + `ask_user`
- 复用同一套 `ask_user` / inquiry 气泡 / PendingRequestId 挂起恢复路径
- 数据库层面无可见痕迹（与"征询会议结束"完全同构，都是 inquiry 消息）
- 详见 [02-AF编排集成.md §4.2 主持人提示词关键约束.B](./02-AF编排集成.md)

**与工作模式"需求不明确"处理思路一致**：见 [general_manager.md:33](../../Src/Netor.Cortana.AI/prompts/work_mode/general_manager.md#L33)，工作模式总经理也是用提示词约束自主判断 ask_user 时机，不做状态机。

### 7.9 其他细节

| # | 问题 | 决策 |
|---|------|------|
| 1 | 智能体最少/最多人数？ | 最少 **2** 个；最多软上限 **8** 个 |
| 2 | 主持人头像？ | 固定的"小议长锤"图标 + 紫色 #A06CFF 边框 |
| 3 | 状态提示文字何时更新？ | 每次 `meeting.speaker.changed` 时替换：`财务张三 正在发言…` / `主持人在等您回复` |

---

## 八、和 AF 框架对齐的硬约束

- ✅ **编排引擎**：`AgentWorkflowBuilder.CreateGroupChatBuilderWith` + 自定义 `MeetingHostManager`，**不自造**轮询
- ✅ **流式**：`InProcessExecution.RunStreamingAsync` + `AgentResponseUpdateEvent`，**不轮询**
- ✅ **HITL 真正阻塞**：在 `SelectNextAgentAsync` 入口用 `TaskCompletionSource<bool>` 阻塞，不依赖"AF 自然挂起"（AF GroupChat 没有此机制）
- ✅ **终止信号显式化**：`ShouldTerminateAsync` 仅返回 `_terminationFlag`，由主持人 LLM 调 `confirm_meeting_end` 工具置位
- ✅ **历史真相在 DB**：`UpdateHistoryAsync` 完全无视 AF buffer，从 `MeetingMessages` 重建
- ✅ **工具**：会议模式参会者**只装载只读工具**（`ToolFilterMode.ReadOnly`）；可变操作交给会议结论后续走工作模式
- ✅ **AOT 安全**：JSON 源生成、UI code-behind、无反射、无 Binding
- ✅ **零浮窗交互**：所有问询走对话流；视觉沿用专家模式头像气泡

---

## 九、文档大纲

| 文档 | 状态 | 内容 |
|------|------|------|
| README.md | 📝 思路对齐稿 v8 | 本文档：定位、需求要点、AF 映射总览、关键决策、23 条架构硬约束、重试策略汇总 |
| [02-AF编排集成.md](./02-AF编排集成.md) | ✅ 初稿 v4 | TCS 阻塞 + 5 个会议特有工具 + ResolveAgent + LLM 重试 + ToolFilterMode + 主题对齐 + MeetingContextHeader + 提示词分层 + 绝对路径附件 |
| [03-数据模型与持久化.md](./03-数据模型与持久化.md) | ✅ 初稿 v4 | 5 张表 DDL（含 UNIQUE 索引 + IsPartial）+ 压缩双路径（含并发互斥）+ Service 接口（含 TryClearPending 乐观锁 + AppendAsync 事务）+ WS 分发 + Cancellation + Startup + Provider/Model 锁定 |
| [04-界面交互设计.md](./04-界面交互设计.md) | ✅ 初稿 v3 | 仿专家模式气泡 + 多选输入（含数量校验）+ StatusHintBar + Tab 切换 + Model 锁定 toast + 重试与部分消息视觉 + 历史回放 |
| [05-实施路线图.md](./05-实施路线图.md) | ✅ 初稿 v4 | 6 阶段计划（含 HitlTools 公共化 + IPromptProvider + 31 个缺陷修复）+ 依赖关系 + 风险应对 + 验收里程碑 |

---

**最后更新**：2026-05-30（v8 — 第三轮现实场景多智能体认知与数据完整性推演，修复 11 个新增架构性逻辑缺陷：参会者工具过滤认知 / 参会者数量未强制 / 压缩+ResumeAsync 竞态 / Sequence 并发写冲突 / 重复 Resume 不幂等 / 中途切 Model 上下文丢 / IsPartial 消息污染 LLM 上下文 / 附件路径相对绝对未规约 / 主持人提示词膨胀 / 参会者无 ask_user 工具 / AgentEntity 提示词语境冲突）
