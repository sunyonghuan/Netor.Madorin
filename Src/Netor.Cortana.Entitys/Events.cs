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

    // ──────── Chat↔Workflow 桥接（阶段 5B Phase 3） ────────

    /// <summary>
    /// Chat 端基于启发式检测到当前 user input 像是复杂任务，建议切到 Workflow 工作模式。
    /// 由 <c>AiChatHostedService.SendMessageAsync</c> 在 <c>mentions.Count==0</c> 分支发布；
    /// UI 端订阅后在 Chat 输入框上方展示 banner，用户点击 [切到工作模式] 跳转工作台 Tab。
    /// 走 conversation topic（不是 workflow topic），因为这是 chat 端的提示。
    /// </summary>
    public static WorkflowSuggestionEvent OnWorkflowSuggestion = new("conversation.workflow.suggestion");

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

    /// <summary>P4 验证阶段完成（携带验证分数/摘要/问题列表）。</summary>
    public static TaskValidationEvent OnTaskValidationCompleted = new("task.validation.completed");

    /// <summary>P4 子智能体向用户提问（需求分析/计划制定阶段的多轮对话）。</summary>
    public static TaskUserQuestionEvent OnTaskUserQuestionAsked = new("task.user.question_asked");

    /// <summary>P4 用户已回答问题。</summary>
    public static TaskUserQuestionEvent OnTaskUserQuestionAnswered = new("task.user.question_answered");
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

// ──────── Chat↔Workflow 桥接事件类型 ────────

/// <summary>
/// Chat→Workflow 启发式建议事件（conversation.workflow.suggestion，阶段 5B Phase 3 新增）。
/// 由 <c>AiChatHostedService</c> 在 user input 命中"复杂任务"启发式时发布，UI 端弹 banner 引导切到工作模式。
/// </summary>
public record WorkflowSuggestionEvent(string Eventid) : EventID<WorkflowSuggestionArgs>(Eventid);

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
// Chat↔Workflow 桥接事件参数（阶段 5B Phase 3，保留）
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// 阶段 5B Phase 3：Chat→Workflow 启发式建议参数。
/// 由 <c>AiChatHostedService.SendMessageAsync</c> 在 <c>mentions.Count==0</c> 且 user input 命中复杂任务关键词时发布。
/// 走 conversation topic（不是 workflow topic），由 UI Banner 订阅展示，用户点击后跳工作台 + 预填任务输入框。
/// </summary>
/// <param name="SourceSessionId">触发建议的 chat 会话 ID。</param>
/// <param name="TraceId">分布式追踪 ID。</param>
/// <param name="OriginalInput">触发建议的 user input 全文（点击后预填到工作流输入框）。</param>
/// <param name="SuggestedSubMode">推荐的 Workflow 子模式：当前固定为 "Magentic"。</param>
/// <param name="Reason">展示给用户的说明文本（中文，可由 UI 直接渲染）。</param>
/// <param name="OccurredAt">建议生成时间。</param>
public record WorkflowSuggestionArgs(
    string SourceSessionId,
    string TraceId,
    string OriginalInput,
    string SuggestedSubMode,
    string Reason,
    DateTimeOffset OccurredAt) : EventArgs;

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

/// <summary>P4 验证完成事件类型（task.validation.completed）。</summary>
public record TaskValidationEvent(string Eventid) : EventID<TaskValidationEventArgs>(Eventid);

/// <summary>P4 用户问答事件类型（task.user.question_asked / question_answered）。</summary>
public record TaskUserQuestionEvent(string Eventid) : EventID<TaskUserQuestionEventArgs>(Eventid);

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
/// P4 验证完成事件参数。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="Passed">验证是否通过。</param>
/// <param name="Score">验证分数（0-100）。</param>
/// <param name="Summary">验证摘要。</param>
/// <param name="Issues">发现的问题列表。</param>
public record TaskValidationEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    bool Passed,
    int Score,
    string? Summary = null,
    IReadOnlyList<string>? Issues = null) : TaskEngineEventArgs(TaskId, OccurredAt);

/// <summary>
/// P4 用户问答事件参数（子智能体提问 / 用户回答）。
/// </summary>
/// <param name="TaskId">任务 ID。</param>
/// <param name="OccurredAt">事件发生时间。</param>
/// <param name="RequestId">问题请求唯一 ID。</param>
/// <param name="Phase">当前阶段（requirements / planning）。</param>
/// <param name="Question">问题文本。</param>
/// <param name="UserAnswer">用户回答（提问时为 null，回答时有值）。</param>
/// <param name="Round">对话轮次。</param>
public record TaskUserQuestionEventArgs(
    string TaskId,
    DateTimeOffset OccurredAt,
    string RequestId,
    string Phase,
    string Question,
    string? UserAnswer = null,
    int Round = 0) : TaskEngineEventArgs(TaskId, OccurredAt);

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
