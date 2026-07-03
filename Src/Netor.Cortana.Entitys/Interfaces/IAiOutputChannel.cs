namespace Netor.Cortana.Entitys;

/// <summary>
/// AI 对话输出通道接口。每个实现代表一种输出方式（WebSocket、TTS 语音等），
/// AiChatService 向所有活跃通道广播 AI 流式回复。
/// </summary>
public interface IAiOutputChannel
{
    /// <summary>通道名称，用于日志和调试。</summary>
    string Name { get; }

    /// <summary>当前通道是否处于活跃状态，决定是否接收 AI 输出。</summary>
    bool IsActive { get; }

    /// <summary>AI 流式回复的单个 token。</summary>
    Task OnTokenAsync(string turnId, string token, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>AI 回复完成。</summary>
    Task OnDoneAsync(string turnId, string sessionId, CancellationToken cancellationToken = default);

    /// <summary>AI 回复被取消（如唤醒词打断），通道应清理缓冲区并停止进行中的工作。</summary>
    Task OnCancelledAsync(string turnId);

    /// <summary>AI 回复出错。</summary>
    Task OnErrorAsync(string turnId, string message, CancellationToken cancellationToken = default);
}
