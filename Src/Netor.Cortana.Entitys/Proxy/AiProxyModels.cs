namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// AI 代理运行模式。
/// </summary>
public enum AiProxyMode
{
    /// <summary>
    /// 使用 Proxy 专用 Agent 隔离实例。默认模式。
    /// </summary>
    ProxyAgent = 0,

    /// <summary>
    /// 仅按模型接口处理外部 messages，不启用 Agent 工具链。
    /// </summary>
    ModelOnly = 1
}

/// <summary>
/// AI 代理请求类型。
/// </summary>
public enum AiProxyRequestKind
{
    /// <summary>
    /// Ollama /api/chat 风格请求。
    /// </summary>
    Chat = 0,

    /// <summary>
    /// Ollama /api/generate 风格请求。
    /// </summary>
    Generate = 1
}

/// <summary>
/// AI 代理响应结束原因。
/// </summary>
public enum AiProxyFinishReason
{
    /// <summary>
    /// 未结束，仍在流式输出。
    /// </summary>
    None = 0,

    /// <summary>
    /// 正常完成。
    /// </summary>
    Stop = 1,

    /// <summary>
    /// 达到输出长度限制。
    /// </summary>
    Length = 2,

    /// <summary>
    /// 被取消。
    /// </summary>
    Cancelled = 3,

    /// <summary>
    /// 发生错误。
    /// </summary>
    Error = 4
}

/// <summary>
/// 代理可暴露给外部客户端的模型描述。
/// </summary>
/// <param name="Name">外部可见模型名，例如 cortana/default:latest。</param>
/// <param name="ProviderId">内部厂商 ID。</param>
/// <param name="ModelId">内部模型 ID。</param>
/// <param name="DisplayName">显示名称。</param>
/// <param name="ContextLength">上下文窗口长度。</param>
/// <param name="ProviderName">厂商名称。</param>
/// <param name="ModelName">内部模型名称。</param>
public sealed record AiProxyModelDescriptor(
    string Name,
    string ProviderId,
    string ModelId,
    string DisplayName,
    int ContextLength,
    string ProviderName = "",
    string ModelName = "");

/// <summary>
/// AI 代理消息。该类型只服务于 Proxy 独立通道，不代表主聊天窗口消息。
/// </summary>
/// <param name="Role">消息角色：system、user、assistant、tool。</param>
/// <param name="Content">消息内容。</param>
/// <param name="Name">可选名称。</param>
public sealed record AiProxyMessage(
    string Role,
    string Content,
    string? Name = null);

/// <summary>
/// AI 代理请求。外部 Ollama 请求会被转换为该统一请求模型。
/// </summary>
/// <param name="RequestId">请求 ID，用于日志与取消跟踪。</param>
/// <param name="SessionKey">Proxy 专用会话键。为空时表示无状态单次请求。</param>
/// <param name="Kind">请求类型。</param>
/// <param name="Mode">代理模式。</param>
/// <param name="Model">外部模型名。</param>
/// <param name="Messages">请求消息列表。</param>
/// <param name="Stream">是否流式输出。</param>
/// <param name="ProviderId">指定内部厂商 ID。</param>
/// <param name="ModelId">指定内部模型 ID。</param>
/// <param name="AgentId">指定 Proxy Agent ID。</param>
/// <param name="Temperature">可选温度。</param>
/// <param name="MaxTokens">可选最大输出 Token。</param>
/// <param name="TopP">可选 TopP。</param>
public sealed record AiProxyAgentRequest(
    string RequestId,
    string? SessionKey,
    AiProxyRequestKind Kind,
    AiProxyMode Mode,
    string Model,
    IReadOnlyList<AiProxyMessage> Messages,
    bool Stream,
    string? ProviderId = null,
    string? ModelId = null,
    string? AgentId = null,
    double? Temperature = null,
    int? MaxTokens = null,
    double? TopP = null);

/// <summary>
/// AI 代理流式响应片段。
/// </summary>
/// <param name="Content">本次增量文本。</param>
/// <param name="Done">是否完成。</param>
/// <param name="FinishReason">完成原因。</param>
/// <param name="InputTokens">本次已知输入 Token。</param>
/// <param name="OutputTokens">本次已知输出 Token。</param>
/// <param name="ErrorMessage">错误信息。</param>
public sealed record AiProxyChatDelta(
    string Content,
    bool Done = false,
    AiProxyFinishReason FinishReason = AiProxyFinishReason.None,
    long? InputTokens = null,
    long? OutputTokens = null,
    string? ErrorMessage = null);

/// <summary>
/// AI 代理设置快照。由 UI/配置服务提供给代理后端或网络服务使用。
/// </summary>
public sealed record AiProxyOptionsSnapshot(
    bool Enabled,
    string Host,
    int Port,
    AiProxyMode Mode,
    string? ProviderId,
    string? ModelId,
    string? AgentId,
    bool ExposeDefaultModel,
    bool AllowLan,
    bool RequireApiKey,
    int MaxConcurrentRequests,
    string Version = "0.5.7");

/// <summary>
/// AI 代理用量快照。
/// </summary>
public sealed record AiProxyUsageSnapshot(
    long LastInputTokens,
    long TotalOutputTokens,
    long MaxContextTokens,
    double ContextUsageRatio,
    long TotalRequests,
    long ActiveRequests,
    long SucceededRequests,
    long FailedRequests,
    string LastError);
