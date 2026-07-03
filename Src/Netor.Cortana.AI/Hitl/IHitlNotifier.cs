namespace Netor.Cortana.AI.Hitl;

/// <summary>
/// HITL 通知出口，由具体模式决定发布哪个事件族。
/// </summary>
public interface IHitlNotifier
{
    /// <summary>
    /// 通知界面当前上下文正在等待用户回答。
    /// </summary>
    Task PublishAskUserAsync(
        string contextId,
        string requestId,
        string question,
        CancellationToken cancellationToken = default);
}
