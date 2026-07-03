using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 最近完成任务复用工具。
/// </summary>
public sealed class RecentTaskTools
{
    private readonly WorkTaskService _taskService;
    private readonly string _taskId;

    public RecentTaskTools(WorkTaskService taskService, string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateGetRecentCompletedTaskPlanTool()
    {
        [Description("获取当前会话最近完成任务的计划，适合用户说“再做一份”“按刚才方案改一下”。")]
        Task<string> GetRecentCompletedTaskPlanAsync(CancellationToken ct)
        {
            var currentTask = _taskService.GetById(_taskId);
            if (currentTask is null)
            {
                return Task.FromResult("错误：当前任务不存在");
            }

            var sourceTask = !string.IsNullOrWhiteSpace(currentTask.SourceTaskId)
                ? _taskService.GetById(currentTask.SourceTaskId)
                : _taskService.GetMostRecentCompletedTask(currentTask.SessionId);

            if (sourceTask is null || string.IsNullOrWhiteSpace(sourceTask.CurrentPlanJson))
            {
                return Task.FromResult("没有找到可复用的最近完成任务计划。");
            }

            WorkPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize(sourceTask.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
            }
            catch (JsonException ex)
            {
                return Task.FromResult($"错误：最近任务计划 JSON 无效 - {ex.Message}");
            }

            if (plan?.MainSteps is null || plan.MainSteps.Count == 0)
            {
                return Task.FromResult("最近完成任务没有有效计划。");
            }

            _taskService.SetSourceTask(_taskId, sourceTask.Id);

            var sb = new StringBuilder();
            sb.AppendLine($"来源任务：{sourceTask.Title}");
            sb.AppendLine($"source_task_id: {sourceTask.Id}");
            sb.AppendLine();
            sb.AppendLine("可复用计划：");
            for (var i = 0; i < plan.MainSteps.Count; i++)
            {
                var mainStep = plan.MainSteps[i];
                sb.AppendLine($"{i + 1}. {mainStep.Title}");
                foreach (var subStep in mainStep.SubSteps)
                {
                    sb.AppendLine($"   - {subStep.Title}");
                    sb.AppendLine($"     验收标准：{subStep.AcceptanceCriteria}");
                }
            }

            return Task.FromResult(sb.ToString());
        }

        return AIFunctionFactory.Create(GetRecentCompletedTaskPlanAsync, new AIFunctionFactoryOptions
        {
            Name = "get_recent_completed_task_plan",
            Description = "获取最近完成任务的计划作为新任务参考，并把当前任务 SourceTaskId 指向来源任务。"
        });
    }
}
