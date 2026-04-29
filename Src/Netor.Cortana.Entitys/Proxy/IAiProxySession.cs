namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy 专用会话契约。
/// 只用于外部代理调用，不能映射到 Cortana 主聊天会话。
/// </summary>
public interface IAiProxySession
{
    /// <summary>
    /// Proxy 会话键。
    /// </summary>
    string SessionKey { get; }

    /// <summary>
    /// 创建时间。
    /// </summary>
    DateTimeOffset CreatedAt { get; }

    /// <summary>
    /// 最近访问时间。
    /// </summary>
    DateTimeOffset LastAccessAt { get; }

    /// <summary>
    /// 当前会话内的消息快照。
    /// </summary>
    IReadOnlyList<AiProxyMessage> Messages { get; }

    /// <summary>
    /// 追加消息。
    /// </summary>
    void Append(AiProxyMessage message);

    /// <summary>
    /// 清空会话消息。
    /// </summary>
    void Clear();
}
