using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.AI.WorkMode.Tools;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 强插话服务：立刻打断当前正在运行的步骤，回写步骤输入，并立即重启 B 层继续执行。
/// </summary>
public sealed class RunningStepInterruptService(
    WorkTaskFileService fileService,
    WorkTaskService taskService,
    WorkExecutionLogService logService,
    WorkTaskCancellationRegistry cancellationRegistry,
    ProjectLeadService projectLeadService,
    ILogger<ProjectLeadTools> projectLeadToolsLogger)
{
    public async Task<bool> InterruptCurrentStepAsync(string taskId, string interruptInput, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptInput);

        var step = fileService.ResetRunningStepForInterrupt(taskId, interruptInput);
        if (step is null)
        {
            return false;
        }

        var interruptRecord = new WorkExecutionLogRecords.UserInterrupt(interruptInput.Trim());
        logService.Append(
            taskId,
            WorkExecutionLogTypes.UserInterrupt,
            JsonSerializer.Serialize(interruptRecord, WorkModeJsonContext.Default.UserInterrupt));

        await cancellationRegistry.CancelAsync(taskId).ConfigureAwait(false);
        taskService.SetPreemption(taskId, false);
        taskService.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);

        var tools = new ProjectLeadTools(
            fileService,
            projectLeadService,
            projectLeadToolsLogger,
            taskId,
            taskService,
            cancellationRegistry);
        _ = tools.CreateResumeOrchestratorTool().InvokeAsync(new AIFunctionArguments(), cancellationToken);
        return true;
    }
}
