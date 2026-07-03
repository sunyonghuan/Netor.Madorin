using System.Text.Json;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.WorkMode.Files;

internal static class WorkPlanLegacyConverter
{
    public static WorkTaskPlanFile ToPlanFile(
        WorkPlan plan,
        WorkTaskEntity task,
        string status = WorkTaskPlanStatuses.Planning)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(task);

        var defaultRole = ResolveDefaultRole(task);
        var steps = new List<WorkTaskPlanStepFile>();
        for (var mainIndex = 0; mainIndex < plan.MainSteps.Count; mainIndex++)
        {
            var mainStep = plan.MainSteps[mainIndex];
            for (var subIndex = 0; subIndex < mainStep.SubSteps.Count; subIndex++)
            {
                var subStep = mainStep.SubSteps[subIndex];
                var stepNumber = $"{mainIndex + 1}.{subIndex + 1}";
                steps.Add(new WorkTaskPlanStepFile
                {
                    Id = $"step-{mainIndex + 1:D2}-{subIndex + 1:D2}",
                    Title = $"{stepNumber} {subStep.Title.Trim()}",
                    Role = defaultRole,
                    Input = BuildInput(mainStep.Title, subStep),
                    Status = WorkTaskPlanStepStatuses.Pending,
                    IsMilestone = subIndex == mainStep.SubSteps.Count - 1
                });
            }
        }

        return new WorkTaskPlanFile
        {
            Version = 1,
            TaskId = task.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = status,
            Steps = steps
        };
    }

    public static WorkPlan ToLegacyPlan(WorkTaskPlanFile plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var mainSteps = plan.Steps
            .Select(static step => new WorkPlanMainStep(
                string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title,
                [
                    new WorkPlanSubStep(
                        string.IsNullOrWhiteSpace(step.Title) ? step.Id : step.Title,
                        string.IsNullOrWhiteSpace(step.Input) ? "按 plan.yaml 步骤要求完成并产出摘要。" : step.Input)
                ]))
            .ToList();

        return new WorkPlan(mainSteps);
    }

    private static string BuildInput(string mainTitle, WorkPlanSubStep subStep)
    {
        return $"""
            主步骤：{mainTitle.Trim()}
            子步骤：{subStep.Title.Trim()}
            验收标准：{subStep.AcceptanceCriteria.Trim()}
            """;
    }

    private static string? ResolveDefaultRole(WorkTaskEntity task)
    {
        if (!string.IsNullOrWhiteSpace(task.MentionsJson))
        {
            try
            {
                var mentions = JsonSerializer.Deserialize(task.MentionsJson, WorkModeJsonContext.Default.ListAgentMentionDto);
                if (mentions is { Count: 1 })
                {
                    return string.IsNullOrWhiteSpace(mentions[0].AgentName)
                        ? mentions[0].AgentId
                        : mentions[0].AgentName;
                }
            }
            catch (JsonException)
            {
                // 旧缓存异常时回退到任务 AgentName。
            }
        }

        return string.IsNullOrWhiteSpace(task.AgentName) ? null : task.AgentName;
    }
}
