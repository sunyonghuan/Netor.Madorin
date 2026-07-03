namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 请求上下文，跟踪当前发起 AI 请求的 WebSocket 客户端 ID。
/// 用于将 AI 回复定向发送给请求方，而非广播给所有客户端。
/// </summary>
public sealed class WebSocketRequestContext
{
    /// <summary>
    /// 当前正在请求 AI 的 WebSocket 客户端 ID。
    /// 为 null 时表示请求来自非 WebSocket 来源（主界面、语音），输出通道应广播。
    /// </summary>
    public string? ActiveClientId { get; set; }
}
