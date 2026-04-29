namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy 专用会话管理器契约。
/// 用于外部客户端连续对话隔离，不能与主聊天窗口会话复用。
/// </summary>
public interface IAiProxySessionManager
{
    /// <summary>
    /// 获取或创建 Proxy 会话。
    /// </summary>
    IAiProxySession GetOrCreateSession(string sessionKey);

    /// <summary>
    /// 尝试获取 Proxy 会话。
    /// </summary>
    bool TryGetSession(string sessionKey, out IAiProxySession? session);

    /// <summary>
    /// 清理指定 Proxy 会话。
    /// </summary>
    bool ClearSession(string sessionKey);

    /// <summary>
    /// 清理全部 Proxy 会话。
    /// </summary>
    void ClearAll();
}
