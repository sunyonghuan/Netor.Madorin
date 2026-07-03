using System.ComponentModel;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 执行中用户插话队列工具。
/// </summary>
public sealed class PendingInputTools
{
    private readonly WorkTaskService _taskService;
    private readonly WorkPendingInputService _pendingInputService;
    private readonly string _taskId;

    public PendingInputTools(
        WorkTaskService taskService,
        WorkPendingInputService pendingInputService,
        string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateCheckPendingUserInputTool()
    {
        [Description("检查用户在执行中是否发来了新的补充、修改或暂停指令。每个主步骤或耗时工具后调用一次。")]
        Task<string> CheckPendingUserInputAsync(CancellationToken ct)
        {
            var input = _pendingInputService.Dequeue(_taskId);
            if (string.IsNullOrWhiteSpace(input))
            {
                return Task.FromResult("没有新的用户输入。");
            }

            if (!_pendingInputService.HasPending(_taskId))
            {
                _taskService.SetPreemption(_taskId, false);
            }

            return Task.FromResult($"收到用户插话：{input}");
        }

        return AIFunctionFactory.Create(CheckPendingUserInputAsync, new AIFunctionFactoryOptions
        {
            Name = "check_pending_user_input",
            Description = "消费执行中用户插话队列。返回用户最新输入，或提示没有新输入。"
        });
    }
}
