namespace Netor.Cortana.Entitys;

/// <summary>
/// AI 对话输入通道接口。每个实现代表一种输入来源（WebSocket 文字、STT 语音等），
/// 收到用户输入后调用 AiChatService.SendMessageAsync 统一处理。
/// 输入通道自管理生命周期（监听启停），AiChatService 不感知具体输入来源。
/// </summary>
public interface IAiInputChannel
{
    /// <summary>通道名称，用于日志和调试。</summary>
    string Name { get; }
}
