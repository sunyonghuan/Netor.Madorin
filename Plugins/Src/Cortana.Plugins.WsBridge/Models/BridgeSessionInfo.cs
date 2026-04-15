namespace Cortana.Plugins.WsBridge.Models;

/// <summary>
/// 会话摘要信息，用于工具层返回状态查询结果。
/// </summary>
public sealed record BridgeSessionInfo
{
    public string SessionId { get; init; } = string.Empty;
    public string AdapterId { get; init; } = string.Empty;
    public string WsUrl { get; init; } = string.Empty;
    public string State { get; init; } = string.Empty;
    public DateTimeOffset CreatedAt { get; init; }
}
