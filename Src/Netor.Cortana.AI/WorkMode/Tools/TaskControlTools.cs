using System.ComponentModel;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 工作任务控制工具。
/// </summary>
public sealed class TaskControlTools
{
    private readonly WorkTaskService _taskService;
    private readonly IPublisher _publisher;
    private readonly WorkTaskCancellationRegistry _cancellationRegistry;
    private readonly string _taskId;

    public TaskControlTools(
        WorkTaskService taskService,
        IPublisher publisher,
        WorkTaskCancellationRegistry cancellationRegistry,
        string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _cancellationRegistry = cancellationRegistry ?? throw new ArgumentNullException(nameof(cancellationRegistry));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateCancelTaskTool()
    {
        [Description("取消当前工作任务。用户明确要求停止、取消、不要继续时调用。")]
        async Task<string> CancelTaskAsync(
            [Description("取消原因")] string reason,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            if (task is null)
            {
                return "错误：当前任务不存在";
            }

            if (!task.IsActive)
            {
                return "当前任务已经结束，无需重复取消。";
            }

            _taskService.RecordExecutionCancelled(_taskId, string.IsNullOrWhiteSpace(reason) ? "用户取消" : reason);
            await _cancellationRegistry.CancelAsync(_taskId);
            await _publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(_taskId));
            return "当前执行已取消，工作记录仍可继续讨论和调整。";
        }

        return AIFunctionFactory.Create(CancelTaskAsync, new AIFunctionFactoryOptions
        {
            Name = "cancel_task",
            Description = "取消当前执行轮次。仅在用户明确要求取消/停止时调用；不会关闭当前工作记录。"
        });
    }

    public AIFunction CreateCloseTaskRecordTool()
    {
        [Description("关闭当前工作记录。仅当用户明确表示当前工作已经完整结束、归档、关闭时调用。")]
        async Task<string> CloseTaskRecordAsync(
            [Description("关闭原因")] string reason,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            if (task is null)
            {
                return "错误：当前任务不存在";
            }

            if (!task.IsActive)
            {
                return "当前工作记录已经关闭。";
            }

            await _cancellationRegistry.CancelAsync(_taskId);
            _taskService.CloseTaskRecord(_taskId, string.IsNullOrWhiteSpace(reason) ? "用户明确结束当前工作。" : reason);
            await _publisher.PublishAsync(Events.OnWorkTaskCancelled, new WorkTaskCancelledArgs(_taskId));
            return "当前工作记录已关闭。后续如需继续，请从工作记录中重新选择或点击加号开始新的工作。";
        }

        return AIFunctionFactory.Create(CloseTaskRecordAsync, new AIFunctionFactoryOptions
        {
            Name = "close_task_record",
            Description = "关闭当前工作记录。仅在用户明确要求结束/关闭/归档整个当前工作时调用；普通停止执行请用 cancel_task。"
        });
    }
}
