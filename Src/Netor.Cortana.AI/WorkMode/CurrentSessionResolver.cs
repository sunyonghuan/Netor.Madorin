namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 当前会话解析器实现。
/// 通过 AiChatHostedService 的内部访问器获取当前会话信息。
/// </summary>
public sealed class CurrentSessionResolver : ICurrentSessionResolver
{
    private readonly AiChatHostedService _chatService;

    public CurrentSessionResolver(AiChatHostedService chatService)
    {
        _chatService = chatService ?? throw new ArgumentNullException(nameof(chatService));
    }

    public string? GetCurrentSessionId()
    {
        return _chatService.CurrentSessionId;
    }

    public string? GetCurrentWorkspaceId()
    {
        return _chatService.CurrentWorkspaceId;
    }
}
