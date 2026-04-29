namespace Netor.Cortana.Entitys.Proxy;

/// <summary>
/// Proxy Agent 后端契约。
/// 该接口专用于 Ollama/OpenAI 等外部协议代理通道，必须与主聊天窗口会话隔离。
/// </summary>
public interface IAiProxyAgentBackend
{
    /// <summary>
    /// 获取当前可暴露给外部客户端的模型列表。
    /// </summary>
    IReadOnlyList<AiProxyModelDescriptor> ListModels();

    /// <summary>
    /// 处理一次外部代理请求。
    /// 实现方不得读取、复用或写入主聊天窗口历史。
    /// </summary>
    /// <param name="request">代理请求。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>流式响应片段。</returns>
    IAsyncEnumerable<AiProxyChatDelta> ChatAsync(
        AiProxyAgentRequest request,
        CancellationToken cancellationToken = default);
}
