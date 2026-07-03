namespace Netor.Cortana.Networks;

/// <summary>
/// 向已订阅的 PluginBus WebSocket 客户端广播插件总线消息。
/// </summary>
public interface IPluginBusBroadcaster
{
    /// <summary>
    /// 广播 PluginBus 消息。
    /// </summary>
    /// <param name="message">已序列化的 PluginBus 协议消息。</param>
    /// <param name="cancellationToken">取消广播的令牌。</param>
    /// <returns>表示广播过程的任务。</returns>
    Task BroadcastPluginBusAsync(string topic, string message, CancellationToken cancellationToken = default);
}
