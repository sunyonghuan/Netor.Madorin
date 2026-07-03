using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 工作模式启动恢复服务。
/// 应用启动时把上次未结束的活跃任务标记为孤儿任务，等待 UI 让用户决定继续或放弃。
/// </summary>
public sealed class WorkModeStartupService : IHostedService
{
    private readonly WorkTaskService _taskService;
    private readonly ILogger<WorkModeStartupService> _logger;

    public WorkModeStartupService(WorkTaskService taskService, ILogger<WorkModeStartupService> logger)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var activeTasks = _taskService.GetRecoverableActiveTasks();
        foreach (var task in activeTasks)
        {
            if (task.IsOrphaned)
            {
                continue;
            }

            _taskService.MarkOrphaned(task.Id);
            _logger.LogInformation("工作模式检测到未完成任务并标记为孤儿任务：{TaskId}", task.Id);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
