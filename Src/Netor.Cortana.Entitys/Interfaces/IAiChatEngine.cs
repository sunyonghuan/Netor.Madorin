namespace Netor.Cortana.Entitys;

/// <summary>
/// AI 对话引擎契约。输入通道通过此接口将用户输入发送到 AI 引擎，
/// 解耦输入层（Networks、Voice）对 AI 层的直接依赖。
/// 由 AI 层的 AiChatService 实现。
/// </summary>
public interface IAiChatEngine
{
    /// <summary>
    /// 发送用户消息并启动 AI 流式响应。
    /// AI 回复将广播到所有活跃的输出通道。
    /// </summary>
    /// <param name="userInput">用户输入文本。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <param name="attachments">附件列表（可选）。</param>
    /// <param name="mentions">@智能体提及列表（可选）。</param>
    Task SendMessageAsync(string userInput, CancellationToken cancellationToken, List<AttachmentInfo>? attachments = null, List<AgentMention>? mentions = null);

    /// <summary>
    /// 创建新会话。创建成功后会通过 EventHub 发布 <c>Events.OnSessionCreated</c> 事件。
    /// </summary>
    Task NewSessionAsync();

    /// <summary>
    /// 中止当前正在进行的流式响应。
    /// </summary>
    void Stop();

    /// <summary>
    /// 取消当前正在进行的 AI 对话和 TTS 播放。
    /// </summary>
    void CancelCurrentTask();
}
