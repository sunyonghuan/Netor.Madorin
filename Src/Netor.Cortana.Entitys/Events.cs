using System.ComponentModel.DataAnnotations;

using Netor.EventHub;

using EventArgs = Netor.EventHub.EventArgs;

namespace Netor.Cortana.Entitys;

/// <summary>
/// 全局事件定义。所有模块间通过 EventHub 事件通信，此处定义事件 ID 和参数类型。
/// </summary>
public static class Events
{
    // ──────── AI 配置变更事件 ────────

    public static AiProviderChange OnAiProviderChange = new("ai.provider.change");
    public static AiModelChange OnAiModelChange = new("ai.model.change");
    public static AgentChange OnAgentChange = new("ai.agent.change");

    // ──────── 语音流程骨干事件 ────────

    /// <summary>唤醒词被检测到。</summary>
    public static WakeWordDetectedEvent OnWakeWordDetected = new("voice.wakeword.detected");

    /// <summary>STT 中间识别结果（字幕更新）。</summary>
    public static VoiceTextEvent OnSttPartial = new("voice.stt.partial");

    /// <summary>STT 最终结果（一句话说完）。</summary>
    public static VoiceTextEvent OnSttFinal = new("voice.stt.final");

    /// <summary>STT 会话结束（超时/无内容）。</summary>
    public static VoiceSignalEvent OnSttStopped = new("voice.stt.stopped");

    /// <summary>TTS 开始播放。</summary>
    public static VoiceSignalEvent OnTtsStarted = new("voice.tts.started");

    /// <summary>TTS 正在播放的句子（字幕更新）。</summary>
    public static VoiceTextEvent OnTtsSubtitle = new("voice.tts.subtitle");

    /// <summary>TTS 全部播放完成。</summary>
    public static VoiceSignalEvent OnTtsCompleted = new("voice.tts.completed");

    /// <summary>AI 对话 + TTS 全部完成。</summary>
    public static VoiceSignalEvent OnChatCompleted = new("voice.chat.completed");

    // ──────── AI → Voice 事件驱动 ────────

    /// <summary>AI 断句完成，请求 TTS 合成并播放该句文本。</summary>
    public static TtsEnqueueEvent OnTtsEnqueue = new("voice.tts.enqueue");

    /// <summary>AI 推理完成，没有后续文本了。Voice 合成完剩余队列即可。</summary>
    public static VoiceSignalEvent OnTtsFinish = new("voice.tts.finish");

    // ──────── AI → Networks 事件驱动（语音模式 WebSocket 广播） ────────

    /// <summary>语音模式下用户语音输入文本，广播到前端显示。</summary>
    public static VoiceTextEvent OnVoiceUser = new("voice.ws.user");

    /// <summary>语音模式下 AI 流式 token，广播到前端显示。</summary>
    public static VoiceTextEvent OnAiToken = new("voice.ws.aitoken");

    /// <summary>语音模式下 AI 对话完成，携带 sessionId 广播到前端。</summary>
    public static VoiceTextEvent OnVoiceDone = new("voice.ws.done");

    // ──────── 插件系统事件 ────────

    /// <summary>插件列表发生变化（加载/卸载/重载）。</summary>
    public static VoiceSignalEvent OnPluginsChanged = new("plugin.changed");

    // ──────── 网络连接事件 ────────

    /// <summary>WebSocket 客户端连接状态发生变化。</summary>
    public static WebSocketClientConnectionChangedEvent OnWebSocketClientConnectionChanged = new("network.websocket.client.connection.changed");

    /// <summary>WebSocket 输入通道收到用户消息，供主界面即时显示。</summary>
    public static WebSocketUserMessageReceivedEvent OnWebSocketUserMessageReceived = new("network.websocket.user.message.received");

    /// <summary>收到临时系统提示，仅用于当前界面显示，不进入长期历史。</summary>
    public static SystemNoticeEvent OnSystemNotice = new("system.notice");

    /// <summary>MCP 服务器连接状态发生变化（断线/重连成功/重连中）。</summary>
    public static McpConnectionStateChangedEvent OnMcpConnectionStateChanged = new("network.mcp.connection.changed");

    // ──────── UI 生命周期事件 ────────

    /// <summary>AI 推理开始。</summary>
    public static VoiceSignalEvent OnAiStarted = new("ai.started");

    /// <summary>AI 推理完成（无论成功/失败/取消，always 触发）。</summary>
    public static VoiceSignalEvent OnAiCompleted = new("ai.completed");

    /// <summary>主窗口被显示（用户手动或 AI/插件调用）。</summary>
    public static VoiceSignalEvent OnMainWindowShown = new("ui.mainwindow.shown");

    // ──────── 工作区事件 ────────

    /// <summary>工作目录发生变更。</summary>
    public static WorkspaceChangedEvent OnWorkspaceChanged = new("workspace.changed");

    // ──────── 会话事件 ────────

    /// <summary>新会话已创建（已写入数据库）。</summary>
    public static SessionCreatedEvent OnSessionCreated = new("session.created");

    /// <summary>会话标题已由 AI 生成更新。</summary>
    public static SessionTitleUpdatedEvent OnSessionTitleUpdated = new("session.title.updated");

    // ──────── 对话事实事件 ────────

    /// <summary>一轮对话已开始，宿主已分配 turnId / traceId。</summary>
    public static ConversationTurnStartedEvent OnConversationTurnStarted = new("conversation.turn.started");

    /// <summary>本轮用户消息已进入宿主 AI 对话流程。</summary>
    public static ConversationUserMessageEvent OnConversationUserMessage = new("conversation.user.message");

    /// <summary>本轮 AI 流式输出了一个增量片段。</summary>
    public static ConversationAssistantDeltaEvent OnConversationAssistantDelta = new("conversation.assistant.delta");

    /// <summary>本轮对话已结束，状态可能是成功、取消或失败。</summary>
    public static ConversationTurnCompletedEvent OnConversationTurnCompleted = new("conversation.turn.completed");

    // ──────── Workflow 任务事件（阶段 2B 起） ────────

    /// <summary>Workflow 任务已启动，进入 running 状态。</summary>
    public static WorkflowTaskStartedEvent OnWorkflowTaskStarted = new("workflow.task.started");

    /// <summary>Workflow 任务的某个步骤已完成。</summary>
    public static WorkflowStepCompletedEvent OnWorkflowStepCompleted = new("workflow.step.completed");

    /// <summary>Workflow 任务已成功完成，FinalReport 可用。</summary>
    public static WorkflowTaskCompletedEvent OnWorkflowTaskCompleted = new("workflow.task.completed");

    /// <summary>Workflow 任务失败（含宿主重启孤儿清理场景）。</summary>
    public static WorkflowTaskFailedEvent OnWorkflowTaskFailed = new("workflow.task.failed");

    /// <summary>Workflow 任务标题已由 LLM 兜底生成（决策 6-A）。</summary>
    public static WorkflowTaskTitleUpdatedEvent OnWorkflowTaskTitleUpdated = new("workflow.task.title.updated");

    // ──────── 阶段 5B 新增：HITL 暂停 / 恢复 ────────

    /// <summary>Workflow 任务因 HITL（如 Magentic 计划批准）暂停，等待用户响应。</summary>
    public static WorkflowTaskPausedEvent OnWorkflowTaskPaused = new("workflow.task.paused");

    /// <summary>Workflow 任务从 HITL 暂停状态恢复执行。</summary>
    public static WorkflowTaskResumedEvent OnWorkflowTaskResumed = new("workflow.task.resumed");

    // ──────── 阶段 5B Phase 3：Chat↔Workflow 桥接 ────────

    /// <summary>
    /// Chat 端基于启发式检测到当前 user input 像是复杂任务，建议切到 Workflow 工作模式。
    /// 由 <c>AiChatHostedService.SendMessageAsync</c> 在 <c>mentions.Count==0</c> 分支发布；
    /// UI 端订阅后在 Chat 输入框上方展示 banner，用户点击 [切到工作模式] 跳转工作台 Tab。
    /// 走 conversation topic（不是 workflow topic），因为这是 chat 端的提示。
    /// </summary>
    public static WorkflowSuggestionEvent OnWorkflowSuggestion = new("conversation.workflow.suggestion");

    // ──────── P2-4 新增：动态子智能体创建审批 ────────

    /// <summary>
    /// Magentic Manager 通过 <c>create_subagent</c> 工具发起动态子智能体创建请求，等待用户审批。
    /// 由 <c>CreateSubAgentTool.CreateSubAgentAsync</c> 发布，由 <c>DynamicAgentCreationApprovalVm</c> 订阅展示卡片。
    /// 用户响应通过 <c>DynamicAgentCreationGate.ResolveDecision</c> 解锁工具的 await。
    /// </summary>
    public static DynamicAgentCreationRequestedEvent OnDynamicAgentCreationRequested
        = new("workflow.dynamic.agent.creation.requested");

    /// <summary>
    /// 动态子智能体创建审批已被用户决策（Approved / ApprovedAll / Rejected）。
    /// UI 可订阅此事件清空审批卡片或记录审计日志。
    /// </summary>
    public static DynamicAgentCreationResolvedEvent OnDynamicAgentCreationResolved
        = new("workflow.dynamic.agent.creation.resolved");

    // ──────── P4 任务执行引擎事件 ────────

    /// <summary>P4 阶段开始（需求分析/计划制定/执行/验证）。</summary>
    public static TaskPhaseEvent OnTaskPhaseStarted = new("task.phase.started");

    /// <summary>P4 阶段完成。</summary>
    public static TaskPhaseEvent OnTaskPhaseCompleted = new("task.phase.completed");

    /// <summary>P4 执行计划已创建。</summary>
    public static TaskPlanEvent OnTaskPlanCreated = new("task.plan.created");

    /// <summary>P4 执行计划已更新（用户修改后）。</summary>
    public static TaskPlanEvent OnTaskPlanUpdated = new("task.plan.updated");

    /// <summary>P4 执行计划已被用户确认。</summary>
    public static TaskPlanEvent OnTaskPlanConfirmed = new("task.plan.confirmed");

    /// <summary>P4 步骤开始执行。</summary>
    public static TaskStepEvent OnTaskStepStarted = new("task.step.started");

    /// <summary>P4 步骤中途进度更新。</summary>
    public static TaskStepProgressEvent OnTaskStepProgress = new("task.step.progress");

    /// <summary>P4 步骤执行完成。</summary>
    public static TaskStepEvent OnTaskStepCompleted = new("task.step.completed");

    /// <summary>P4 步骤执行失败。</summary>
    public static TaskStepEvent OnTaskStepFailed = new("task.step.failed");

    /// <summary>P4 步骤重试中。</summary>
    public static TaskStepRetryEvent OnTaskStepRetrying = new("task.step.retrying");

    /// <summary>P4 步骤等待用户确认。</summary>
    public static TaskStepEvent OnTaskStepWaitingUser = new("task.step.waiting_user");

    /// <summary>P4 步骤被跳过。</summary>
    public static TaskStepEvent OnTaskStepSkipped = new("task.step.skipped");

    /// <summary>P4 子智能体已创建。</summary>
    public static TaskSubAgentEvent OnTaskSubAgentCreated = new("task.agent.created");

    /// <summary>P4 子智能体已完成。</summary>
    public static TaskSubAgentEvent OnTaskSubAgentCompleted = new("task.agent.completed");

    /// <summary>P4 任务引擎暂停。</summary>
    public static TaskLifecycleEvent OnTaskEnginePaused = new("task.engine.paused");

    /// <summary>P4 任务引擎恢复。</summary>
    public static TaskLifecycleEvent OnTaskEngineResumed = new("task.engine.resumed");

    /// <summary>P4 任务引擎完成。</summary>
    public static TaskLifecycleEvent OnTaskEngineCompleted = new("task.engine.completed");

    /// <summary>P4 任务引擎失败。</summary>
    public static TaskLifecycleEvent OnTaskEngineFailed = new("task.engine.failed");

    /// <summary>P4-6 执行模板已保存。</summary>
    public static TaskLifecycleEvent OnTaskTemplateSaved = new("task.template.saved");
}

// ──────── AI 配置变更事件类型 ────────

public record AiProviderChange(string Eventid) : EventID<DataChangeArgs>(Eventid);
public record AiModelChange(string Eventid) : EventID<DataChangeArgs>(Eventid);
public record AgentChange(string Eventid) : EventID<DataChangeArgs>(Eventid);

// ──────── 语音流程事件类型 ────────

/// <summary>唤醒词检测事件</summary>
public record WakeWordDetectedEvent(string Eventid) : EventID<VoiceSignalArgs>(Eventid);

/// <summary>语音文本事件（STT 部分/最终结果）</summary>
public record VoiceTextEvent(string Eventid) : EventID<VoiceTextArgs>(Eventid);

/// <summary>语音信号事件（无载荷的生命周期信号）</summary>
public record VoiceSignalEvent(string Eventid) : EventID<VoiceSignalArgs>(Eventid);

/// <summary>TTS 入队事件（携带句子文本）</summary>
public record TtsEnqueueEvent(string Eventid) : EventID<TtsEnqueueArgs>(Eventid);

/// <summary>工作目录变更事件</summary>
public record WorkspaceChangedEvent(string Eventid) : EventID<WorkspaceChangedArgs>(Eventid);

/// <summary>会话创建事件</summary>
public record SessionCreatedEvent(string Eventid) : EventID<SessionCreatedArgs>(Eventid);

/// <summary>会话标题更新事件</summary>
public record SessionTitleUpdatedEvent(string Eventid) : EventID<SessionTitleUpdatedArgs>(Eventid);

/// <summary>对话轮次开始事件</summary>
public record ConversationTurnStartedEvent(string Eventid) : EventID<ConversationTurnStartedArgs>(Eventid);

/// <summary>用户消息事件</summary>
public record ConversationUserMessageEvent(string Eventid) : EventID<ConversationUserMessageArgs>(Eventid);

/// <summary>助手流式增量事件</summary>
public record ConversationAssistantDeltaEvent(string Eventid) : EventID<ConversationAssistantDeltaArgs>(Eventid);

/// <summary>对话轮次结束事件</summary>
public record ConversationTurnCompletedEvent(string Eventid) : EventID<ConversationTurnCompletedArgs>(Eventid);

/// <summary>WebSocket 客户端连接状态变更事件</summary>
public record WebSocketClientConnectionChangedEvent(string Eventid) : EventID<WebSocketClientConnectionChangedArgs>(Eventid);

/// <summary>WebSocket 用户消息接收事件</summary>
public record WebSocketUserMessageReceivedEvent(string Eventid) : EventID<WebSocketUserMessageReceivedArgs>(Eventid);

/// <summary>临时系统提示事件</summary>
public record SystemNoticeEvent(string Eventid) : EventID<SystemNoticeArgs>(Eventid);

/// <summary>MCP 服务器连接状态变更事件</summary>
public record McpConnectionStateChangedEvent(string Eventid) : EventID<McpConnectionStateChangedArgs>(Eventid);

// ──────── Workflow 任务事件类型（阶段 2B 起） ────────

/// <summary>Workflow 任务启动事件（task.started）</summary>
public record WorkflowTaskStartedEvent(string Eventid) : EventID<WorkflowTaskStartedArgs>(Eventid);

/// <summary>Workflow 任务步骤完成事件（step.completed）</summary>
public record WorkflowStepCompletedEvent(string Eventid) : EventID<WorkflowStepCompletedArgs>(Eventid);

/// <summary>Workflow 任务完成事件（task.completed）</summary>
public record WorkflowTaskCompletedEvent(string Eventid) : EventID<WorkflowTaskCompletedArgs>(Eventid);

/// <summary>Workflow 任务失败事件（task.failed，含宿主重启孤儿清理）</summary>
public record WorkflowTaskFailedEvent(string Eventid) : EventID<WorkflowTaskFailedArgs>(Eventid);

/// <summary>Workflow 任务标题更新事件（task.title.updated，决策 6-A）</summary>
public record WorkflowTaskTitleUpdatedEvent(string Eventid) : EventID<WorkflowTaskTitleUpdatedArgs>(Eventid);

/// <summary>Workflow 任务 HITL 暂停事件（task.paused，阶段 5B 新增）</summary>
public record WorkflowTaskPausedEvent(string Eventid) : EventID<WorkflowTaskPausedArgs>(Eventid);

/// <summary>Workflow 任务 HITL 恢复事件（task.resumed，阶段 5B 新增）</summary>
public record WorkflowTaskResumedEvent(string Eventid) : EventID<WorkflowTaskResumedArgs>(Eventid);

/// <summary>
/// Chat→Workflow 启发式建议事件（conversation.workflow.suggestion，阶段 5B Phase 3 新增）。
/// 由 <c>AiChatHostedService</c> 在 user input 命中"复杂任务"启发式时发布，UI 端弹 banner 引导切到工作模式。
/// </summary>
public record WorkflowSuggestionEvent(string Eventid) : EventID<WorkflowSuggestionArgs>(Eventid);

/// <summary>P2-4：动态子智能体创建请求事件类型（workflow.dynamic.agent.creation.requested）。</summary>
public record DynamicAgentCreationRequestedEvent(string Eventid)
    : EventID<DynamicAgentCreationRequestedArgs>(Eventid);

/// <summary>P2-4：动态子智能体创建审批已决策事件类型（workflow.dynamic.agent.creation.resolved）。</summary>
public record DynamicAgentCreationResolvedEvent(string Eventid)
    : EventID<DynamicAgentCreationResolvedArgs>(Eventid);

// ──────── 事件参数类型 ────────

/// <summary>
/// 数据变更事件的通用参数
/// </summary>
/// <param name="Id">变更实体的 ID</param>
/// <param name="Type">变更类型</param>
public record DataChangeArgs(string Id, ChangeType Type = ChangeType.Update) : EventArgs;

/// <summary>
/// 语音文本事件参数
/// </summary>
/// <param name="Text">识别或合成的文本内容</param>
public record VoiceTextArgs(string Text) : EventArgs;

/// <summary>
/// 语音信号事件参数（无载荷，仅表示信号）
/// </summary>
public record VoiceSignalArgs() : EventArgs;

/// <summary>
/// TTS 入队事件参数
/// </summary>
/// <param name="Sentence">断句后的文本</param>
public record TtsEnqueueArgs(string Sentence) : EventArgs;

/// <summary>
/// 工作目录变更事件参数
/// </summary>
/// <param name="Path">新的工作目录路径</param>
public record WorkspaceChangedArgs(string Path) : EventArgs;

/// <summary>
/// 会话创建事件参数
/// </summary>
/// <param name="SessionId">新创建的会话ID</param>
public record SessionCreatedArgs(string SessionId) : EventArgs;

/// <summary>
/// 会话标题更新事件参数
/// </summary>
/// <param name="SessionId">会话ID</param>
/// <param name="Title">AI 生成的新标题</param>
public record SessionTitleUpdatedArgs(string SessionId, string Title) : EventArgs;

/// <summary>
/// 对话事实事件统一上下文。
/// </summary>
public abstract record ConversationEventArgs(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    DateTimeOffset OccurredAt) : EventArgs;

/// <summary>
/// 一轮对话开始事件参数。
/// </summary>
public record ConversationTurnStartedArgs(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    DateTimeOffset OccurredAt,
    int AttachmentCount,
    IReadOnlyList<string> MentionedAgentIds) : ConversationEventArgs(
        SessionId,
        TurnId,
        TraceId,
        ProviderId,
        ProviderName,
        AgentId,
        AgentName,
        ModelId,
        ModelName,
        UserMessageId,
        AssistantMessageId,
        OccurredAt);

/// <summary>
/// 用户消息事件参数。
/// </summary>
public record ConversationUserMessageArgs(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    DateTimeOffset OccurredAt,
    string Content,
    IReadOnlyList<AttachmentInfo> Attachments) : ConversationEventArgs(
        SessionId,
        TurnId,
        TraceId,
        ProviderId,
        ProviderName,
        AgentId,
        AgentName,
        ModelId,
        ModelName,
        UserMessageId,
        AssistantMessageId,
        OccurredAt);

/// <summary>
/// 助手流式增量事件参数。
/// </summary>
public record ConversationAssistantDeltaArgs(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    DateTimeOffset OccurredAt,
    string Delta,
    int Sequence) : ConversationEventArgs(
        SessionId,
        TurnId,
        TraceId,
        ProviderId,
        ProviderName,
        AgentId,
        AgentName,
        ModelId,
        ModelName,
        UserMessageId,
        AssistantMessageId,
        OccurredAt);

/// <summary>
/// 对话轮次结束状态。
/// </summary>
public enum ConversationTurnStatus
{
    [Display(Name = "成功")]
    Succeeded,

    [Display(Name = "取消")]
    Cancelled,

    [Display(Name = "失败")]
    Failed
}

/// <summary>
/// 对话轮次结束事件参数。
/// </summary>
public record ConversationTurnCompletedArgs(
    string SessionId,
    string TurnId,
    string TraceId,
    string ProviderId,
    string ProviderName,
    string AgentId,
    string AgentName,
    string ModelId,
    string ModelName,
    string UserMessageId,
    string AssistantMessageId,
    DateTimeOffset OccurredAt,
    ConversationTurnStatus Status,
    string UserInput,
    string AssistantResponse,
    string? ErrorMessage,
    int AssistantDeltaCount,
    int AttachmentCount) : ConversationEventArgs(
        SessionId,
        TurnId,
        TraceId,
        ProviderId,
        ProviderName,
        AgentId,
        AgentName,
        ModelId,
        ModelName,
        UserMessageId,
        AssistantMessageId,
        OccurredAt);

/// <summary>
/// WebSocket 客户端连接状态变更事件参数
/// </summary>
/// <param name="ClientId">客户端 ID</param>
/// <param name="RemoteIp">远端 IP 地址</param>
/// <param name="RemotePort">远端端口</param>
/// <param name="IsConnected">true 表示连接，false 表示断开</param>
public record WebSocketClientConnectionChangedArgs(
    string ClientId,
    string RemoteIp,
    int RemotePort,
    bool IsConnected) : EventArgs
{
    public string RemoteEndpoint => RemotePort > 0 ? $"{RemoteIp}:{RemotePort}" : RemoteIp;
}

/// <summary>
/// WebSocket 输入通道收到的用户消息参数。
/// </summary>
/// <param name="ClientId">发送消息的客户端 ID</param>
/// <param name="Text">用户输入文本</param>
/// <param name="Attachments">附件列表</param>
public record WebSocketUserMessageReceivedArgs(
    string ClientId,
    string Text,
    IReadOnlyList<AttachmentInfo> Attachments) : EventArgs;

/// <summary>
/// 临时系统提示参数。该消息只用于界面即时展示，不写入聊天历史。
/// </summary>
/// <param name="Content">提示详细内容，对应 WebSocket 协议中的 data。</param>
/// <param name="Title">提示标题。</param>
/// <param name="Level">提示等级，如 info、success、warning、error、progress。</param>
/// <param name="Source">提示来源，如插件名、第三方软件名或客户端 ID。</param>
/// <param name="CreatedAt">提示创建时间。</param>
public record SystemNoticeArgs(
    string Content,
    string Title,
    string Level,
    string Source,
    DateTimeOffset CreatedAt) : EventArgs;

/// <summary>
/// MCP 服务器连接状态变更事件参数
/// </summary>
/// <param name="ServerName">MCP 服务器显示名称</param>
/// <param name="ServerId">MCP 服务器 ID</param>
/// <param name="IsConnected">true 表示已连接，false 表示断开</param>
/// <param name="IsReconnecting">true 表示正在自动重连中</param>
public record McpConnectionStateChangedArgs(
    string ServerName,
    string ServerId,
    bool IsConnected,
    bool IsReconnecting) : EventArgs;

// ════════════════════════════════════════════════════════════════════════
// Workflow 任务事件参数（阶段 2B 起）
// 详见 docs/未来版本策划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.4
// 所有 Workflow Args 共享以下定位字段：
//   TaskId / SourceSessionId / TraceId / WorkspaceId / Mode / SubMode / OccurredAt
// 这套字段允许 Memory 插件按工作区、追踪 ID、源会话进行任意聚合。
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Workflow 任务通用基类。所有 Workflow 事件继承该基类，复用统一定位字段。
/// </summary>
public abstract record WorkflowEventArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt) : EventArgs;

/// <summary>
/// Workflow 任务已启动事件参数（workflow.task.started）。
/// </summary>
public record WorkflowTaskStartedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string Title,
    string? ManagerAgentId,
    string? ManagerAgentName,
    string InitialInput,
    IReadOnlyList<string> ParticipantAgentIds,
    long StartedAt) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务步骤完成事件参数（workflow.step.completed）。
/// </summary>
public record WorkflowStepCompletedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string StepId,
    string? ParentStepId,
    int Sequence,
    string? AgentId,
    string? AgentName,
    string Action,
    string Status,
    long StartedAt,
    long? CompletedAt,
    long? DurationMs,
    int? TokenInputCount,
    int? TokenOutputCount,
    string? ErrorMessage,
    string? SummaryJson) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务完成事件参数（workflow.task.completed）。FinalReport 可用。
/// 阶段 6 Phase 4：新增 <see cref="AllowMemoryIngest"/> 字段（决策 6-4-A 修订）。
/// host 端在发布事件前查 <c>AgentService.GetById(ManagerAgentId).AllowWorkflowMemory</c> 填充该字段；
/// Memory 插件 <c>MemoryWorkflowEventHandler</c> 检查 false 时跳过入库（事件正常发，仅丢弃 ingest 副作用）。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §阶段 6 #5。
/// </summary>
public record WorkflowTaskCompletedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string Title,
    string? ManagerAgentId,
    string? ManagerAgentName,
    string? FinalReport,
    int StepCount,
    long? TotalDurationMs,
    long TotalTokenInputCount,
    long TotalTokenOutputCount,
    IReadOnlyList<string> ParticipantAgentIds,
    long CompletedAt,
    bool AllowMemoryIngest = true) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务失败事件参数（workflow.task.failed，含宿主重启孤儿清理场景）。
/// </summary>
public record WorkflowTaskFailedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string Title,
    string ErrorMessage,
    string FailureReason,           // "exception" / "timeout" / "host_restart_orphan" / "cancelled"
    long FailedAt) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务标题更新事件参数（workflow.task.title.updated，决策 6-A）。
/// </summary>
public record WorkflowTaskTitleUpdatedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string OldTitle,
    string NewTitle,
    bool IsAutoGenerated) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务 HITL 暂停事件参数（workflow.task.paused，阶段 5B 新增）。
///
/// 触发场景：Magentic <c>RequirePlanSignoff(true)</c> 在每次产出计划后等待用户批准；
/// 未来可扩展到其他 HITL 节点（如关键工具调用前的确认）。
///
/// <see cref="RequestId"/> 是 SDK <c>ExternalRequest.RequestId</c> 的回带，
/// 用于在 <c>ResumeAsync</c> 时把用户响应与原始请求配对，防止旧响应误生效。
/// </summary>
public record WorkflowTaskPausedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string Title,
    string PauseReason,
    string RequestId,
    string? RequestPayloadJson,
    long PausedAt) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// Workflow 任务 HITL 恢复事件参数（workflow.task.resumed，阶段 5B 新增）。
///
/// <see cref="ResumeAction"/> 取值：
/// <list type="bullet">
///   <item><c>"approved"</c> - 用户批准，发送空 ChatMessage 列表（Magentic 视为通过）</item>
///   <item><c>"revised"</c> - 用户提供修改建议，<see cref="RevisionPayloadJson"/> 含 ChatMessage 列表 JSON</item>
///   <item><c>"rejected"</c> - 用户拒绝，触发 OperationCanceledException 走 HandleTaskCancelled 路径</item>
/// </list>
/// </summary>
public record WorkflowTaskResumedArgs(
    string TaskId,
    string? SourceSessionId,
    string TraceId,
    string WorkspaceId,
    string Mode,
    string SubMode,
    DateTimeOffset OccurredAt,
    string Title,
    string RequestId,
    string ResumeAction,
    string? RevisionPayloadJson,
    long ResumedAt) : WorkflowEventArgs(
        TaskId,
        SourceSessionId,
        TraceId,
        WorkspaceId,
        Mode,
        SubMode,
        OccurredAt);

/// <summary>
/// 阶段 5B Phase 3 新增：Chat→Workflow 启发式建议参数。
/// 由 <c>AiChatHostedService.SendMessageAsync</c> 在 <c>mentions.Count==0</c> 且 user input 命中复杂任务关键词时发布。
/// 走 conversation topic（不是 workflow topic），由 UI Banner 订阅展示，用户点击后跳工作台 + 预填 NewTaskDialog。
/// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.3 / Phase 3 实施计划 §4.1。
/// </summary>
/// <param name="SourceSessionId">触发建议的 chat 会话 ID（用户点[切到工作模式]后会作为 NewTaskDialog 的 SourceSessionId 字段）。</param>
/// <param name="TraceId">分布式追踪 ID。</param>
/// <param name="OriginalInput">触发建议的 user input 全文（点击后预填到 NewTaskDialog.InitialInput）。</param>
/// <param name="SuggestedSubMode">推荐的 Workflow 子模式：当前一期固定为 "Magentic"（最通用），后续可按关键词分流。</param>
/// <param name="Reason">展示给用户的说明文本（中文，可由 UI 直接渲染）。</param>
/// <param name="OccurredAt">建议生成时间。</param>
public record WorkflowSuggestionArgs(
    string SourceSessionId,
    string TraceId,
    string OriginalInput,
    string SuggestedSubMode,
    string Reason,
    DateTimeOffset OccurredAt) : EventArgs;

/// <summary>
/// P2-4：动态子智能体创建审批请求事件参数。
///
/// 由 <c>CreateSubAgentTool.CreateSubAgentAsync</c> 在每次 Manager 调用 <c>create_subagent</c> 时发布
/// （除非该任务已通过 ApprovedAll 进入 auto-approve 模式）。UI 收到事件后展示审批卡片，
/// 用户点击 Approve / ApproveAll / Reject 后通过 <c>DynamicAgentCreationGate.ResolveDecision(RequestId, ...)</c>
/// 解锁工具内 await。
/// </summary>
/// <param name="TaskId">所属工作流任务 ID。</param>
/// <param name="RequestId">本次审批请求唯一标识，用于在 Gate 中配对响应。</param>
/// <param name="ManagerAgentId">发起请求的 Manager AgentId，便于审计。</param>
/// <param name="ProposedName">Manager 提议的子智能体名称（已通过命名正则与唯一性校验）。</param>
/// <param name="ProposedResponsibility">Manager 提议的职责描述（一句话简介）。</param>
/// <param name="ProposedInstructions">Manager 提议的子智能体系统提示词。</param>
/// <param name="ProposedRequiredTools">Manager 提议的必需工具白名单（已通过工具存在性校验）。</param>
/// <param name="CurrentCount">本任务当前已创建的动态子智能体数量。</param>
/// <param name="MaxSubAgents">本任务允许创建的子智能体上限。</param>
public record DynamicAgentCreationRequestedArgs(
    string TaskId,
    string RequestId,
    string ManagerAgentId,
    string ProposedName,
    string ProposedResponsibility,
    string ProposedInstructions,
    IReadOnlyList<string> ProposedRequiredTools,
    int CurrentCount,
    int MaxSubAgents) : WorkflowEventArgs(
        TaskId,
        null,
        string.Empty,
        string.Empty,
        "discussion",
        "magentic",
        DateTimeOffset.UtcNow);

/// <summary>
/// P2-4：动态子智能体创建审批已决策事件参数。
///
/// <see cref="Decision"/> 取值：
/// <list type="bullet">
///   <item><c>"approved"</c> - 单次批准，下次 create_subagent 仍会再次询问</item>
///   <item><c>"approved_all"</c> - 本任务后续 create_subagent 不再询问（auto-approve）</item>
///   <item><c>"rejected"</c> - 用户拒绝，工具返回失败字符串给 Manager</item>
/// </list>
/// </summary>
public record DynamicAgentCreationResolvedArgs(
    string TaskId,
    string RequestId,
    string Decision) : WorkflowEventArgs(
        TaskId,
        null,
        string.Empty,
        string.Empty,
        "discussion",
        "magentic",
        DateTimeOffset.UtcNow);

// ════════════════════════════════════════════════════════════════════════
// P4 任务执行引擎事件类型 + 事件参数
// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §11
// ════════════════════════════════════════════════════════════════════════

/// <summary>P4 阶段事件类型（task.phase.started / task.phase.completed）。</summary>
public record TaskPhaseEvent(string Eventid) : EventID<TaskPhaseEventArgs>(Eventid);

/// <summary>P4 计划事件类型（task.plan.created / updated / confirmed）。</summary>
public record TaskPlanEvent(string Eventid) : EventID<TaskPlanEventArgs>(Eventid);

/// <summary>P4 步骤事件类型（task.step.started / completed / failed / waiting_user / skipped）。</summary>
public record TaskStepEvent(string Eventid) : EventID<TaskStepEventArgs>(Eventid);

/// <summary>P4 步骤进度事件类型（task.step.progress）。</summary>
public record TaskStepProgressEvent(string Eventid) : EventID<TaskStepProgressEventArgs>(Eventid);

/// <summary>P4 步骤重试事件类型（task.step.retrying）。</summary>
public record TaskStepRetryEvent(string Eventid) : EventID<TaskStepRetryEventArgs>(Eventid);

/// <summary>P4 子智能体事件类型（task.agent.created / completed）。</summary>
public record TaskSubAgentEvent(string Eventid) : EventID<TaskSubAgentEventArgs>(Eventid);

/// <summary>P4 任务生命周期事件类型（task.engine.paused / resumed / completed / failed）。</summary>
public record TaskLifecycleEvent(string Eventid) : EventID<TaskLifecycleEventArgs>(Eventid);

/// <summary>
/// P4 任务执行引擎事件通用基类。
/// 所有 P4 事件继承该基类，复用统一定位字段。
/// </summary>
public abstract record TaskEngineEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt) : EventArgs;

/// <summary>
/// P4 阶段事件参数（阶段开始/完成）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="Phase">阶段名称：requirements / planning / executing / validating。</param>
public record TaskPhaseEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string Phase) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 计划事件参数（计划创建/更新/确认）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="PlanId">计划 ID。</param>
/// <param name="Version">计划版本号。</param>
/// <param name="StepCount">计划中的步骤数量。</param>
public record TaskPlanEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string PlanId,
    int Version,
    int StepCount) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 步骤事件参数（步骤开始/完成/失败/等待用户/跳过）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="StepId">步骤 ID。</param>
/// <param name="StepSequence">步骤序号（1-based）。</param>
/// <param name="Title">步骤标题。</param>
/// <param name="Status">步骤状态字符串。</param>
/// <param name="ResultSummary">结果摘要（完成时有值）。</param>
public record TaskStepEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string StepId,
    int StepSequence,
    string Title,
    string Status,
    string? ResultSummary = null) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 步骤进度事件参数（中途进度更新）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="StepId">步骤 ID。</param>
/// <param name="StepSequence">步骤序号。</param>
/// <param name="Title">步骤标题。</param>
/// <param name="ProgressPercent">进度百分比（0-100）。</param>
/// <param name="ProgressDetail">进度描述文本。</param>
public record TaskStepProgressEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string StepId,
    int StepSequence,
    string Title,
    int ProgressPercent,
    string? ProgressDetail = null) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 步骤重试事件参数。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="StepId">步骤 ID。</param>
/// <param name="StepSequence">步骤序号。</param>
/// <param name="Title">步骤标题。</param>
/// <param name="RetryCount">当前重试次数。</param>
/// <param name="MaxRetries">最大重试次数。</param>
/// <param name="ErrorMessage">导致重试的错误信息。</param>
/// <param name="NextDelayMs">下次重试前的延迟毫秒数。</param>
public record TaskStepRetryEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string StepId,
    int StepSequence,
    string Title,
    int RetryCount,
    int MaxRetries,
    string ErrorMessage,
    int NextDelayMs) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 子智能体事件参数（创建/完成）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="StepId">所属步骤 ID。</param>
/// <param name="AgentName">子智能体名称。</param>
/// <param name="AgentRole">子智能体角色描述。</param>
public record TaskSubAgentEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string StepId,
    string AgentName,
    string AgentRole) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 任务生命周期事件参数（暂停/恢复/完成/失败）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="Reason">事件原因描述（可为 null）。</param>
public record TaskLifecycleEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string? Reason = null) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// 模型变更类型
/// </summary>
public enum ChangeType
{
    [Display(Name = "添加")]
    Create,

    [Display(Name = "删除")]
    Delete,

    [Display(Name = "更新")]
    Update
}
