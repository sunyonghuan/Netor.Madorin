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

/// <summary>MCP 服务器连接状态变更事件</summary>
public record McpConnectionStateChangedEvent(string Eventid) : EventID<McpConnectionStateChangedArgs>(Eventid);

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
