using Netor.Cortana.AI.Hitl;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Hitl;

/// <summary>
/// 工作任务的 HITL 上下文适配器。
/// </summary>
public sealed class WorkTaskHitlContext : IHitlContext
{
    private readonly WorkTaskService _taskService;

    public WorkTaskHitlContext(WorkTaskService taskService, string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        ContextId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public string ContextId { get; }

    public void SetPending(string requestId, string kind, string snapshotJson)
    {
        _taskService.SetPendingRequest(ContextId, requestId, kind, snapshotJson);
    }

    public void ClearPending()
    {
        _taskService.SetPendingRequest(ContextId, null, null, null);
    }
}
