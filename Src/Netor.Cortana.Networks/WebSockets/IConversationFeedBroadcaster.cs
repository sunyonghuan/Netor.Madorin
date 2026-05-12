namespace Netor.Cortana.Networks;

/// <summary>
/// 向已订阅的 conversation-feed WebSocket 客户端广播会话事实事件。
/// </summary>
public interface IConversationFeedBroadcaster
{
    /// <summary>
    /// 广播 conversation-feed 消息。
    /// </summary>
    /// <param name="message">已序列化的 feed 协议消息。</param>
    /// <param name="cancellationToken">取消广播的令牌。</param>
    /// <returns>表示广播过程的任务。</returns>
    Task BroadcastConversationFeedAsync(string message, CancellationToken cancellationToken = default);
}
