namespace Netor.Cortana.Entitys;

/// <summary>
/// 附件信息，包含文件路径、文件名和 MIME 类型。
/// P3-2 扩展：支持文件夹附件（IsFolder + FileCount + TotalBytes）。
/// </summary>
/// <param name="Path">文件或文件夹完整路径。</param>
/// <param name="Name">文件名或文件夹名。</param>
/// <param name="MimeType">MIME 类型（文件夹为 "inode/directory"）。</param>
/// <param name="IsFolder">是否为文件夹附件。</param>
/// <param name="FileCount">文件夹下有效文件数（仅 IsFolder=true 时有意义）。</param>
/// <param name="TotalBytes">文件夹下有效文件总大小（仅 IsFolder=true 时有意义）。</param>
public sealed record AttachmentInfo(
    string Path,
    string Name,
    string MimeType,
    bool IsFolder = false,
    int FileCount = 0,
    long TotalBytes = 0);

/// <summary>
/// 用户消息中 @智能体 的提及信息。
/// </summary>
/// <param name="Agent">被提及的智能体实体。</param>
/// <param name="StartIndex">@ 符号在原始文本中的起始位置。</param>
/// <param name="EndIndex">智能体名称在原始文本中的结束位置（不含）。</param>
public sealed record AgentMention(AgentEntity Agent, int StartIndex, int EndIndex);

/// <summary>
/// WebSocket 客户端消息，保留原有 type/data/attachments 字段，并承载可选扩展字段。
/// </summary>
public sealed record WebSocketClientMessage(
    string ClientId,
    string Type,
    string Data,
    List<AttachmentInfo> Attachments,
    string? Title,
    string? Level,
    string? Source);

/// <summary>
/// 聊天消息传输契约，解耦 AI 对话层对具体传输实现的依赖。
/// 由 Networks 层（WebSocketServer）或其他传输实现，AI 层通过构造函数注入。
/// </summary>
public interface IChatTransport
{
    /// <summary>
    /// 传输层监听端口，供前端连接使用。
    /// </summary>
    int Port { get; }

    /// <summary>
    /// 向指定客户端发送流式文本片段（token）。
    /// </summary>
    /// <param name="clientId">客户端标识</param>
    /// <param name="token">文本片段</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SendTokenAsync(string clientId, string token, CancellationToken cancellationToken = default);

    /// <summary>
    /// 通知指定客户端本次回复已完成。
    /// </summary>
    /// <param name="clientId">客户端标识</param>
    /// <param name="sessionId">会话 ID（可选）</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SendDoneAsync(string clientId, string? sessionId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向指定客户端发送错误消息。
    /// </summary>
    /// <param name="clientId">客户端标识</param>
    /// <param name="message">错误描述</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task SendErrorAsync(string clientId, string message, CancellationToken cancellationToken = default);

    /// <summary>
    /// 向所有已连接的客户端广播消息。
    /// </summary>
    /// <param name="type">消息类型</param>
    /// <param name="data">消息数据</param>
    /// <param name="cancellationToken">取消令牌</param>
    Task BroadcastAsync(string type, string data, CancellationToken cancellationToken = default);

    /// <summary>
    /// 当收到客户端发送的消息时触发。
    /// 参数：clientId, type, data, attachments。
    /// </summary>
    event Func<string, string, string, List<AttachmentInfo>, Task>? OnMessageReceived;

    /// <summary>
    /// 当收到客户端发送的完整消息时触发。用于读取兼容扩展字段。
    /// </summary>
    event Func<WebSocketClientMessage, Task>? OnClientMessageReceived;
}
