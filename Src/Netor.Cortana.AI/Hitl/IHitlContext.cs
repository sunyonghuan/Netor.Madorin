namespace Netor.Cortana.AI.Hitl;

/// <summary>
/// HITL 挂起上下文，屏蔽工作任务与会议会话的持久化差异。
/// </summary>
public interface IHitlContext
{
    /// <summary>
    /// 当前上下文 ID。工作模式为任务 ID，会议模式为会议 ID。
    /// </summary>
    string ContextId { get; }

    /// <summary>
    /// 设置待用户响应的挂起请求。
    /// </summary>
    void SetPending(string requestId, string kind, string snapshotJson);

    /// <summary>
    /// 清除待用户响应的挂起请求。
    /// </summary>
    void ClearPending();
}
