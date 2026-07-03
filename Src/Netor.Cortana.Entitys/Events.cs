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

    // ──────── 工作模式事件（v1.0） ────────

    /// <summary>工作任务已创建。</summary>
    public static WorkTaskCreatedEvent OnWorkTaskCreated = new("work.task.created");

    /// <summary>工作任务标题已更新。</summary>
    public static WorkTaskTitleUpdatedEvent OnWorkTaskTitleUpdated = new("work.task.title.updated");

    /// <summary>工作任务已完成。</summary>
    public static WorkTaskCompletedEvent OnWorkTaskCompleted = new("work.task.completed");

    /// <summary>工作任务已失败。</summary>
    public static WorkTaskFailedEvent OnWorkTaskFailed = new("work.task.failed");

    /// <summary>工作任务已取消。</summary>
    public static WorkTaskCancelledEvent OnWorkTaskCancelled = new("work.task.cancelled");

    /// <summary>工作任务执行轮次已暂停。</summary>
    public static WorkTaskPausedEvent OnWorkTaskPaused = new("work.task.paused");

    /// <summary>工作计划已更新。</summary>
    public static WorkPlanUpdatedEvent OnWorkPlanUpdated = new("work.plan.updated");

    /// <summary>主步骤已开始。</summary>
    public static WorkStepStartedEvent OnWorkStepStarted = new("work.step.started");

    /// <summary>主步骤已完成。</summary>
    public static WorkStepCompletedEvent OnWorkStepCompleted = new("work.step.completed");

    /// <summary>工具调用已开始。</summary>
    public static WorkToolCallEvent OnWorkToolCall = new("work.tool.call");

    /// <summary>工具调用已完成。</summary>
    public static WorkToolResultEvent OnWorkToolResult = new("work.tool.result");

    /// <summary>AI 流式输出增量（总经理/部门主管的文本输出）。</summary>
    public static WorkAssistantDeltaEvent OnWorkAssistantDelta = new("work.assistant.delta");

    /// <summary>HITL 授权请求（工具授权）。</summary>
    public static WorkApprovalRequestedEvent OnWorkApprovalRequested = new("work.approval.requested");

    /// <summary>HITL ask_user 请求。</summary>
    public static WorkAskUserRequestedEvent OnWorkAskUserRequested = new("work.askuser.requested");

    /// <summary>并行块已开始。</summary>
    public static WorkParallelBlockStartedEvent OnWorkParallelBlockStarted = new("work.parallel.started");

    /// <summary>并行块已结束。</summary>
    public static WorkParallelBlockEndedEvent OnWorkParallelBlockEnded = new("work.parallel.ended");

    /// <summary>嵌套子工作流已开始。</summary>
    public static WorkNestedWorkflowStartedEvent OnWorkNestedWorkflowStarted = new("work.nested.started");

    /// <summary>嵌套子工作流已结束。</summary>
    public static WorkNestedWorkflowEndedEvent OnWorkNestedWorkflowEnded = new("work.nested.ended");

    /// <summary>子智能体背景任务已开始。</summary>
    public static WorkSubAgentJobStartedEvent OnWorkSubAgentJobStarted = new("work.subagent.started");

    /// <summary>子智能体背景任务进度更新。</summary>
    public static WorkSubAgentJobProgressEvent OnWorkSubAgentJobProgress = new("work.subagent.progress");

    /// <summary>子智能体背景任务已完成。</summary>
    public static WorkSubAgentJobCompletedEvent OnWorkSubAgentJobCompleted = new("work.subagent.completed");

    /// <summary>子智能体背景任务已失败。</summary>
    public static WorkSubAgentJobFailedEvent OnWorkSubAgentJobFailed = new("work.subagent.failed");

    /// <summary>B 层任务事件已创建。</summary>
    public static WorkTaskEventCreatedEvent OnWorkTaskEventCreated = new("work.task.event.created");

    // ──────── 会议模式事件（v1.0） ────────

    /// <summary>会议已创建。</summary>
    public static MeetingCreatedEvent OnMeetingCreated = new("meeting.created");

    /// <summary>会议发言者已切换。</summary>
    public static MeetingSpeakerChangedEvent OnMeetingSpeakerChanged = new("meeting.speaker.changed");

    /// <summary>会议消息流式增量。</summary>
    public static MeetingMessageDeltaEvent OnMeetingMessageDelta = new("meeting.message.delta");

    /// <summary>会议消息已完成。</summary>
    public static MeetingMessageCompletedEvent OnMeetingMessageCompleted = new("meeting.message.completed");

    /// <summary>会议内部消息已丢弃。</summary>
    public static MeetingMessageDiscardedEvent OnMeetingMessageDiscarded = new("meeting.message.discarded");

    /// <summary>会议思考过程流式增量。</summary>
    public static MeetingThinkingDeltaEvent OnMeetingThinkingDelta = new("meeting.thinking.delta");

    /// <summary>会议工具调用开始。</summary>
    public static MeetingToolCallEvent OnMeetingToolCall = new("meeting.tool.call");

    /// <summary>会议工具调用结果。</summary>
    public static MeetingToolResultEvent OnMeetingToolResult = new("meeting.tool.result");

    /// <summary>会议 HITL ask_user 请求。</summary>
    public static MeetingAskUserRequestedEvent OnMeetingAskUserRequested = new("meeting.askuser.requested");

    /// <summary>用户在会议中发言。</summary>
    public static MeetingUserSpokeEvent OnMeetingUserSpoke = new("meeting.user.spoke");

    /// <summary>会议已完成。</summary>
    public static MeetingCompletedEvent OnMeetingCompleted = new("meeting.completed");

    /// <summary>会议已取消或散会。</summary>
    public static MeetingCancelledEvent OnMeetingCancelled = new("meeting.cancelled");

    /// <summary>会议当前执行轮次已暂停。</summary>
    public static MeetingPausedEvent OnMeetingPaused = new("meeting.paused");

    /// <summary>会议总结草稿已产生。</summary>
    public static MeetingSummaryDraftEvent OnMeetingSummaryDraft = new("meeting.summary.draft");

    /// <summary>会议部分消息已保存。</summary>
    public static MeetingMessagePartialSavedEvent OnMeetingMessagePartialSaved = new("meeting.message.partial.saved");

    /// <summary>会议 LLM 调用正在重试。</summary>
    public static MeetingLlmRetryingEvent OnMeetingLlmRetrying = new("meeting.llm.retrying");
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

// ════════════════════════════════════════════════════════════════════════
// 工作模式事件类型（v1.0）
// ════════════════════════════════════════════════════════════════════════

public record WorkTaskCreatedEvent(string Eventid) : EventID<WorkTaskCreatedArgs>(Eventid);
public record WorkTaskTitleUpdatedEvent(string Eventid) : EventID<WorkTaskTitleUpdatedArgs>(Eventid);
public record WorkTaskCompletedEvent(string Eventid) : EventID<WorkTaskCompletedArgs>(Eventid);
public record WorkTaskFailedEvent(string Eventid) : EventID<WorkTaskFailedArgs>(Eventid);
public record WorkTaskCancelledEvent(string Eventid) : EventID<WorkTaskCancelledArgs>(Eventid);
public record WorkTaskPausedEvent(string Eventid) : EventID<WorkTaskPausedArgs>(Eventid);
public record WorkPlanUpdatedEvent(string Eventid) : EventID<WorkPlanUpdatedArgs>(Eventid);
public record WorkStepStartedEvent(string Eventid) : EventID<WorkStepStartedArgs>(Eventid);
public record WorkStepCompletedEvent(string Eventid) : EventID<WorkStepCompletedArgs>(Eventid);
public record WorkToolCallEvent(string Eventid) : EventID<WorkToolCallArgs>(Eventid);
public record WorkToolResultEvent(string Eventid) : EventID<WorkToolResultArgs>(Eventid);
public record WorkAssistantDeltaEvent(string Eventid) : EventID<WorkAssistantDeltaArgs>(Eventid);
public record WorkApprovalRequestedEvent(string Eventid) : EventID<WorkApprovalRequestedArgs>(Eventid);
public record WorkAskUserRequestedEvent(string Eventid) : EventID<WorkAskUserRequestedArgs>(Eventid);
public record WorkParallelBlockStartedEvent(string Eventid) : EventID<WorkParallelBlockStartedArgs>(Eventid);
public record WorkParallelBlockEndedEvent(string Eventid) : EventID<WorkParallelBlockEndedArgs>(Eventid);
public record WorkNestedWorkflowStartedEvent(string Eventid) : EventID<WorkNestedWorkflowStartedArgs>(Eventid);
public record WorkNestedWorkflowEndedEvent(string Eventid) : EventID<WorkNestedWorkflowEndedArgs>(Eventid);
public record WorkSubAgentJobStartedEvent(string Eventid) : EventID<WorkSubAgentJobStartedArgs>(Eventid);
public record WorkSubAgentJobProgressEvent(string Eventid) : EventID<WorkSubAgentJobProgressArgs>(Eventid);
public record WorkSubAgentJobCompletedEvent(string Eventid) : EventID<WorkSubAgentJobCompletedArgs>(Eventid);
public record WorkSubAgentJobFailedEvent(string Eventid) : EventID<WorkSubAgentJobFailedArgs>(Eventid);
public record WorkTaskEventCreatedEvent(string Eventid) : EventID<WorkTaskEventCreatedArgs>(Eventid);

// ════════════════════════════════════════════════════════════════════════
// 会议模式事件类型（v1.0）
// ════════════════════════════════════════════════════════════════════════

public record MeetingCreatedEvent(string Eventid) : EventID<MeetingCreatedArgs>(Eventid);
public record MeetingSpeakerChangedEvent(string Eventid) : EventID<MeetingSpeakerChangedArgs>(Eventid);
public record MeetingMessageDeltaEvent(string Eventid) : EventID<MeetingMessageDeltaArgs>(Eventid);
public record MeetingMessageCompletedEvent(string Eventid) : EventID<MeetingMessageCompletedArgs>(Eventid);
public record MeetingMessageDiscardedEvent(string Eventid) : EventID<MeetingMessageDiscardedArgs>(Eventid);
public record MeetingThinkingDeltaEvent(string Eventid) : EventID<MeetingThinkingDeltaArgs>(Eventid);
public record MeetingToolCallEvent(string Eventid) : EventID<MeetingToolCallArgs>(Eventid);
public record MeetingToolResultEvent(string Eventid) : EventID<MeetingToolResultArgs>(Eventid);
public record MeetingAskUserRequestedEvent(string Eventid) : EventID<MeetingAskUserRequestedArgs>(Eventid);
public record MeetingUserSpokeEvent(string Eventid) : EventID<MeetingUserSpokeArgs>(Eventid);
public record MeetingCompletedEvent(string Eventid) : EventID<MeetingCompletedArgs>(Eventid);
public record MeetingCancelledEvent(string Eventid) : EventID<MeetingCancelledArgs>(Eventid);
public record MeetingPausedEvent(string Eventid) : EventID<MeetingPausedArgs>(Eventid);
public record MeetingSummaryDraftEvent(string Eventid) : EventID<MeetingSummaryDraftArgs>(Eventid);
public record MeetingMessagePartialSavedEvent(string Eventid) : EventID<MeetingMessagePartialSavedArgs>(Eventid);
public record MeetingLlmRetryingEvent(string Eventid) : EventID<MeetingLlmRetryingArgs>(Eventid);

// ════════════════════════════════════════════════════════════════════════
// 工作模式事件参数（v1.0）
// ════════════════════════════════════════════════════════════════════════

/// <summary>工作任务创建事件参数。</summary>
public record WorkTaskCreatedArgs(string TaskId, string SessionId, string Title) : EventArgs;

/// <summary>工作任务标题更新事件参数。</summary>
public record WorkTaskTitleUpdatedArgs(string TaskId, string Title) : EventArgs;

/// <summary>工作任务完成事件参数。</summary>
public record WorkTaskCompletedArgs(
    string TaskId,
    string FinalReport,
    string ManagerAgentId,
    string ManagerAgentName,
    string SourceSessionId,
    string WorkspaceId,
    long CompletedAt,
    bool AllowMemoryIngest) : EventArgs;

/// <summary>工作任务失败事件参数。</summary>
public record WorkTaskFailedArgs(string TaskId, string ErrorMessage) : EventArgs;

/// <summary>工作任务取消事件参数。</summary>
public record WorkTaskCancelledArgs(string TaskId) : EventArgs;

/// <summary>工作任务暂停事件参数。</summary>
public record WorkTaskPausedArgs(string TaskId, string Reason) : EventArgs;

/// <summary>工作计划更新事件参数。</summary>
public record WorkPlanUpdatedArgs(string TaskId, string PlanJson) : EventArgs;

/// <summary>主步骤开始事件参数。</summary>
public record WorkStepStartedArgs(string TaskId, string StepTitle, string Department) : EventArgs;

/// <summary>主步骤完成事件参数。</summary>
public record WorkStepCompletedArgs(string TaskId, string StepTitle, string Result) : EventArgs;

/// <summary>工具调用事件参数。</summary>
public record WorkToolCallArgs(string TaskId, string CallId, string ToolName, string? ParametersJson) : EventArgs;

/// <summary>工具结果事件参数。</summary>
public record WorkToolResultArgs(string TaskId, string CallId, string Status, string? ResultText, string? Error) : EventArgs;

/// <summary>AI 流式输出增量事件参数。</summary>
public record WorkAssistantDeltaArgs(string TaskId, string? AuthorName, string? Text) : EventArgs;

/// <summary>HITL 授权请求事件参数。</summary>
public record WorkApprovalRequestedArgs(string TaskId, string RequestId, string ToolName, string ParameterText, string Risk) : EventArgs;

/// <summary>HITL ask_user 请求事件参数。</summary>
public record WorkAskUserRequestedArgs(string TaskId, string RequestId, string Question) : EventArgs;

/// <summary>并行块开始事件参数。</summary>
public record WorkParallelBlockStartedArgs(string TaskId, string BlockId, string[] StepTitles) : EventArgs;

/// <summary>并行块结束事件参数。</summary>
public record WorkParallelBlockEndedArgs(string TaskId, string BlockId, int TotalCount, int SuccessCount) : EventArgs;

/// <summary>嵌套子工作流开始事件参数。</summary>
public record WorkNestedWorkflowStartedArgs(string TaskId, string NestedTaskId, string Goal) : EventArgs;

/// <summary>嵌套子工作流结束事件参数。</summary>
public record WorkNestedWorkflowEndedArgs(string TaskId, string NestedTaskId, string FinalReport) : EventArgs;

/// <summary>子智能体背景任务开始事件参数。</summary>
public record WorkSubAgentJobStartedArgs(string TaskId, string JobId, string AgentName, string Query) : EventArgs;

/// <summary>子智能体背景任务进度更新事件参数。</summary>
public record WorkSubAgentJobProgressArgs(string TaskId, string JobId, string ProgressDescription) : EventArgs;

/// <summary>子智能体背景任务完成事件参数。</summary>
public record WorkSubAgentJobCompletedArgs(string TaskId, string JobId, string? ResultJson) : EventArgs;

/// <summary>子智能体背景任务失败事件参数。</summary>
public record WorkSubAgentJobFailedArgs(string TaskId, string JobId, string Error) : EventArgs;

/// <summary>B 层任务事件创建参数。</summary>
public record WorkTaskEventCreatedArgs(string TaskId, string EventId, string Kind, string Message) : EventArgs;

// ════════════════════════════════════════════════════════════════════════
// 会议模式事件参数（v1.0）
// ════════════════════════════════════════════════════════════════════════

/// <summary>会议参会者快照。</summary>
public record MeetingParticipantDto(string AgentId, string Name, int JoinOrder);

/// <summary>会议创建事件参数。</summary>
public record MeetingCreatedArgs(
    string MeetingId,
    string SessionId,
    string Topic,
    IReadOnlyList<MeetingParticipantDto> Participants) : EventArgs;

/// <summary>发言者切换事件参数。</summary>
public record MeetingSpeakerChangedArgs(
    string MeetingId,
    string SpeakerId,
    string SpeakerName,
    string SpeakerKind) : EventArgs;

/// <summary>会议消息流式增量事件参数。</summary>
public record MeetingMessageDeltaArgs(
    string MeetingId,
    string MessageId,
    string SpeakerId,
    string SpeakerKind,
    string DeltaText,
    string SpeakerName = "") : EventArgs;

/// <summary>会议思考过程流式增量事件参数。</summary>
public record MeetingThinkingDeltaArgs(
    string MeetingId,
    string MessageId,
    string SpeakerId,
    string DeltaText) : EventArgs;

/// <summary>会议工具调用事件参数。</summary>
public record MeetingToolCallArgs(
    string MeetingId,
    string MessageId,
    string CallId,
    string ToolName,
    string ArgsJson) : EventArgs;

/// <summary>会议工具结果事件参数。</summary>
public record MeetingToolResultArgs(
    string MeetingId,
    string MessageId,
    string CallId,
    string? ResultText,
    string? ExceptionMessage) : EventArgs;

/// <summary>会议消息完成事件参数。</summary>
public record MeetingMessageCompletedArgs(
    string MeetingId,
    string MessageId,
    string SpeakerId,
    string SpeakerKind,
    string ContentMd,
    string MessageRole,
    bool AwaitingUserReply,
    string SessionId,
    string WorkspaceId,
    string Topic,
    string HostAgentId,
    string Model,
    long CreatedAt,
    string SpeakerName = "") : EventArgs;

/// <summary>会议内部消息丢弃事件参数。</summary>
public record MeetingMessageDiscardedArgs(
    string MeetingId,
    string MessageId) : EventArgs;

/// <summary>会议 ask_user 请求事件参数。</summary>
public record MeetingAskUserRequestedArgs(
    string MeetingId,
    string RequestId,
    string Question) : EventArgs;

/// <summary>用户在会议中发言事件参数。</summary>
public record MeetingUserSpokeArgs(
    string MeetingId,
    string MessageId,
    string Text,
    string Kind) : EventArgs;

/// <summary>会议完成事件参数。</summary>
public record MeetingCompletedArgs(
    string MeetingId,
    string FinalSummary,
    string SessionId,
    string WorkspaceId,
    string Topic,
    string HostAgentId,
    string Model,
    long CreatedAt,
    long EndedAt) : EventArgs;

/// <summary>会议取消事件参数。</summary>
public record MeetingCancelledArgs(string MeetingId, string Reason) : EventArgs;

/// <summary>会议当前执行轮次暂停事件参数。</summary>
public record MeetingPausedArgs(string MeetingId, string Reason) : EventArgs;

/// <summary>会议总结草稿事件参数。</summary>
public record MeetingSummaryDraftArgs(string MeetingId, string SummaryMarkdown) : EventArgs;

/// <summary>会议部分消息保存事件参数。</summary>
public record MeetingMessagePartialSavedArgs(
    string MeetingId,
    string MessageId,
    string SpeakerId,
    string PartialContent,
    string ErrorMessage) : EventArgs;

/// <summary>会议 LLM 重试事件参数。</summary>
public record MeetingLlmRetryingArgs(
    string MeetingId,
    string OperationName,
    int Attempt,
    int MaxAttempts,
    string ExceptionMessage) : EventArgs;
