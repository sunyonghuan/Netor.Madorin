using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Hitl.Json;
using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Tools;

using HitlPendingRequestSnapshot = Netor.Cortana.AI.Hitl.Models.HitlPendingRequestSnapshot;

/// <summary>
/// 计划管理工具：set_plan / get_plan / final_report。
/// 详见 Docs/已完成功能规划/工作模式方案策划/11-AF框架集成方案.md §3.1。
/// </summary>
public sealed class PlanTools
{
    public const string PlanConfirmationKind = "plan_confirmation";
    private const string PlanConfirmationQuestion = "工作计划已制定，确认后我再开始执行。";

    private readonly WorkTaskService _taskService;
    private readonly WorkTaskFileService _fileService;
    private readonly IPublisher _publisher;
    private readonly string _taskId;
    private readonly WorkTaskTitleService? _titleService;
    private readonly IWorkTaskReportCompactionService? _reportCompactionService;

    public PlanTools(
        WorkTaskService taskService,
        WorkTaskFileService fileService,
        IPublisher publisher,
        string taskId,
        WorkTaskTitleService? titleService = null,
        IWorkTaskReportCompactionService? reportCompactionService = null)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
        _titleService = titleService;
        _reportCompactionService = reportCompactionService;
    }

    /// <summary>
    /// 创建 set_plan 工具。
    /// </summary>
    public AIFunction CreateSetPlanTool()
    {
        [Description("制定工作计划，将任务分解为主步骤和子步骤")]
        async Task<string> SetPlanAsync(
            [Description("工作计划 JSON，格式：{\"main_steps\":[{\"title\":\"主步骤标题\",\"sub_steps\":[{\"title\":\"子步骤标题\",\"acceptance_criteria\":\"验收标准\"}]}]}")] string planJson,
            CancellationToken ct)
        {
            try
            {
                // 解析计划 JSON
                var plan = JsonSerializer.Deserialize(planJson, WorkModeJsonContext.Default.WorkPlan);
                var validationError = ValidatePlan(plan);
                if (validationError is not null)
                {
                    return validationError;
                }

                planJson = JsonSerializer.Serialize(plan!, WorkModeJsonContext.Default.WorkPlan);
                if (plan is null || plan.MainSteps.Count == 0)
                    return "错误：计划格式无效或为空";

                var task = _taskService.GetById(_taskId);
                if (task is null)
                {
                    return "错误：任务不存在";
                }

                // 保存兼容缓存与 plan.yaml
                _taskService.UpdatePlan(_taskId, planJson);
                _fileService.SavePlan(WorkPlanLegacyConverter.ToPlanFile(plan!, task));
                _taskService.ResetOrchestratorForNewPlan(_taskId);

                // 发布事件
                await _publisher.PublishAsync(Events.OnWorkPlanUpdated, new WorkPlanUpdatedArgs(_taskId, planJson));

                await RequestPlanConfirmationAsync(_taskService, _publisher, _taskId);

                return $"计划已制定，共 {plan.MainSteps.Count} 个主步骤。请等待用户确认后再执行计划。";
            }
            catch (JsonException ex)
            {
                return $"错误：计划 JSON 格式无效 - {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"错误：保存计划失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(SetPlanAsync, new AIFunctionFactoryOptions
        {
            Name = "set_plan",
            Description = "制定工作计划，将任务分解为主步骤和子步骤。每个子步骤必须有明确的验收标准。"
        });
    }

    public static async Task RequestPlanConfirmationAsync(
        WorkTaskService taskService,
        IPublisher publisher,
        string taskId)
    {
        var requestId = Guid.NewGuid().ToString("N");
        var snapshot = new HitlPendingRequestSnapshot(
            requestId,
            PlanConfirmationKind,
            null,
            null,
            null,
            PlanConfirmationQuestion);

        taskService.SetPendingRequest(
            taskId,
            requestId,
            PlanConfirmationKind,
            JsonSerializer.Serialize(snapshot, HitlJsonContext.Default.HitlPendingRequestSnapshot));

        await publisher.PublishAsync(
            Events.OnWorkAskUserRequested,
            new WorkAskUserRequestedArgs(taskId, requestId, PlanConfirmationQuestion));
    }

    /// <summary>
    /// 创建 get_plan 工具。
    /// </summary>
    public AIFunction CreateGetPlanTool()
    {
        [Description("查看当前工作计划")]
        Task<string> GetPlanAsync(CancellationToken ct)
        {
            try
            {
                var task = _taskService.GetById(_taskId);
                if (task is null)
                    return Task.FromResult("错误：任务不存在");

                if (string.IsNullOrEmpty(task.CurrentPlanJson))
                    return Task.FromResult("当前没有工作计划");

                // 解析并格式化显示
                var plan = JsonSerializer.Deserialize(task.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
                var validationError = ValidatePlan(plan);
                if (validationError is not null)
                    return Task.FromResult(validationError);

                var validPlan = plan!;
                var result = $"当前工作计划（共 {validPlan.MainSteps.Count} 个主步骤）：\n\n";
                for (var i = 0; i < validPlan.MainSteps.Count; i++)
                {
                    var mainStep = validPlan.MainSteps[i];
                    result += $"{i + 1}. {mainStep.Title}\n";
                    foreach (var subStep in mainStep.SubSteps)
                    {
                        result += $"   - {subStep.Title}\n";
                        result += $"     验收标准：{subStep.AcceptanceCriteria}\n";
                    }
                    result += "\n";
                }

                return Task.FromResult(result);
            }
            catch (Exception ex)
            {
                return Task.FromResult($"错误：读取计划失败 - {ex.Message}");
            }
        }

        return AIFunctionFactory.Create(GetPlanAsync, new AIFunctionFactoryOptions
        {
            Name = "get_plan",
            Description = "查看当前工作计划的详细内容"
        });
    }

    /// <summary>
    /// 创建 final_report 工具。
    /// </summary>
    public AIFunction CreateFinalReportTool()
    {
        [Description("读取当前任务计划和步骤摘要，生成进度或最终报告。不修改任务状态。")]
        async Task<string> FinalReportAsync(CancellationToken ct)
        {
            try
            {
                var task = _taskService.GetById(_taskId);
                if (task is null)
                {
                    return "错误：任务不存在";
                }

                var plan = _fileService.LoadPlan(_taskId);
                if (plan is null)
                {
                    return string.IsNullOrWhiteSpace(task.FinalReport)
                        ? "当前任务还没有 plan.yaml，暂无可汇报的执行进度。"
                        : task.FinalReport;
                }

                var rawReport = BuildPlanReport(task, plan);
                if (_reportCompactionService is null)
                {
                    return rawReport;
                }

                var compacted = await _reportCompactionService.CompactAsync(_taskId, rawReport, ct).ConfigureAwait(false);
                return string.IsNullOrWhiteSpace(compacted) ? rawReport : compacted;
            }
            catch (Exception ex)
            {
                return $"错误：生成最终报告失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(FinalReportAsync, new AIFunctionFactoryOptions
        {
            Name = "final_report",
            Description = "读取 plan.yaml 和步骤摘要，生成当前进度或最终报告。不修改任务状态。"
        });
    }

    private static string BuildPlanReport(WorkTaskEntity task, WorkTaskPlanFile plan)
    {
        var total = plan.Steps.Count;
        var done = plan.Steps.Count(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal));
        var failed = plan.Steps.Count(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Failed, StringComparison.Ordinal));

        var lines = new List<string>
        {
            $"任务：{task.Title}",
            $"状态：{plan.Status}",
            $"进度：{done}/{total} 步完成" + (failed > 0 ? $"，{failed} 步失败" : string.Empty),
            string.Empty,
            "步骤摘要："
        };

        foreach (var step in plan.Steps)
        {
            var summary = !string.IsNullOrWhiteSpace(step.Summary)
                ? step.Summary
                : !string.IsNullOrWhiteSpace(step.LastError)
                    ? $"失败：{step.LastError}"
                    : "暂无摘要";
            lines.Add($"- [{step.Status}] {step.Title}: {summary}");
        }

        if (!string.IsNullOrWhiteSpace(task.FinalReport))
        {
            lines.Add(string.Empty);
            lines.Add("已归档最终报告：");
            lines.Add(task.FinalReport);
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static string? ValidatePlan(WorkPlan? plan)
    {
        if (plan?.MainSteps is null || plan.MainSteps.Count == 0)
        {
            return "错误：计划格式无效或为空，必须包含 main_steps";
        }

        for (var i = 0; i < plan.MainSteps.Count; i++)
        {
            var mainStep = plan.MainSteps[i];
            if (string.IsNullOrWhiteSpace(mainStep.Title))
            {
                return $"错误：第 {i + 1} 个主步骤缺少 title";
            }

            if (mainStep.SubSteps is null || mainStep.SubSteps.Count == 0)
            {
                return $"错误：主步骤 {i + 1} 缺少 sub_steps";
            }

            for (var j = 0; j < mainStep.SubSteps.Count; j++)
            {
                var subStep = mainStep.SubSteps[j];
                if (string.IsNullOrWhiteSpace(subStep.Title))
                {
                    return $"错误：主步骤 {i + 1} 的第 {j + 1} 个子步骤缺少 title";
                }

                if (string.IsNullOrWhiteSpace(subStep.AcceptanceCriteria))
                {
                    return $"错误：主步骤 {i + 1} 的第 {j + 1} 个子步骤缺少 acceptance_criteria";
                }
            }
        }

        return null;
    }
}
