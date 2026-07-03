namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 当前会话解析器，获取当前对话的会话 ID 和相关上下文。
/// </summary>
public interface ICurrentSessionResolver
{
    /// <summary>
    /// 获取当前会话 ID。
    /// </summary>
    /// <returns>会话 ID；未找到时返回 null。</returns>
    string? GetCurrentSessionId();

    /// <summary>
    /// 获取当前工作区 ID。
    /// </summary>
    /// <returns>工作区 ID；未找到时返回 null。</returns>
    string? GetCurrentWorkspaceId();
}
