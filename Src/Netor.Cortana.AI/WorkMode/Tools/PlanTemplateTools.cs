using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;
using Netor.EventHub;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 工作计划模板工具。
/// </summary>
public sealed class PlanTemplateTools
{
    private readonly WorkTaskService _taskService;
    private readonly WorkPlanTemplateService _templateService;
    private readonly WorkTaskFileService _fileService;
    private readonly IPublisher _publisher;
    private readonly string _taskId;

    public PlanTemplateTools(
        WorkTaskService taskService,
        WorkPlanTemplateService templateService,
        WorkTaskFileService fileService,
        IPublisher publisher,
        string taskId)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _templateService = templateService ?? throw new ArgumentNullException(nameof(templateService));
        _fileService = fileService ?? throw new ArgumentNullException(nameof(fileService));
        _publisher = publisher ?? throw new ArgumentNullException(nameof(publisher));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateListPlanTemplatesTool()
    {
        [Description("按关键词和分类查询可复用的工作计划模板。")]
        Task<string> ListPlanTemplatesAsync(
            [Description("关键词，可空")] string? keyword,
            [Description("分类，可空")] string? category,
            [Description("最多返回条数，默认 10，最大 20")] int take,
            CancellationToken ct)
        {
            var task = _taskService.GetById(_taskId);
            var templates = _templateService.Search(
                Normalize(keyword),
                Normalize(category),
                task?.WorkspaceId,
                Math.Clamp(take <= 0 ? 10 : take, 1, 20));

            if (templates.Count == 0)
            {
                return Task.FromResult("没有找到可用计划模板。");
            }

            var sb = new StringBuilder();
            sb.AppendLine("可用计划模板：");
            foreach (var template in templates)
            {
                sb.AppendLine($"- id: {template.Id}");
                sb.AppendLine($"  名称: {template.Name}");
                if (!string.IsNullOrWhiteSpace(template.Category))
                {
                    sb.AppendLine($"  分类: {template.Category}");
                }
                if (!string.IsNullOrWhiteSpace(template.Description))
                {
                    sb.AppendLine($"  描述: {template.Description}");
                }
                sb.AppendLine($"  使用次数: {template.UseCount}");
            }

            return Task.FromResult(sb.ToString());
        }

        return AIFunctionFactory.Create(ListPlanTemplatesAsync, new AIFunctionFactoryOptions
        {
            Name = "list_plan_templates",
            Description = "查询可复用的工作计划模板。"
        });
    }

    public AIFunction CreateLoadPlanFromTemplateTool()
    {
        [Description("从计划模板加载计划到当前任务。参数可以是模板 id 或模板名称关键词。")]
        async Task<string> LoadPlanFromTemplateAsync(
            [Description("模板 id 或模板名称关键词")] string nameOrId,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(nameOrId))
            {
                return "错误：模板 id 或名称不能为空";
            }

            var task = _taskService.GetById(_taskId);
            if (task is null)
            {
                return "错误：任务不存在";
            }

            var template = _templateService.FindBestMatch(nameOrId.Trim(), task.WorkspaceId);
            if (template is null)
            {
                return $"没有找到匹配的计划模板：{nameOrId}";
            }

            WorkPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize(template.PlanJson, WorkModeJsonContext.Default.WorkPlan);
            }
            catch (JsonException ex)
            {
                return $"错误：模板计划 JSON 无效 - {ex.Message}";
            }

            var validationError = ValidatePlan(plan);
            if (validationError is not null)
            {
                return validationError;
            }

            var planJson = JsonSerializer.Serialize(plan!, WorkModeJsonContext.Default.WorkPlan);
            _taskService.UpdatePlan(_taskId, planJson);
            _fileService.SavePlan(WorkPlanLegacyConverter.ToPlanFile(plan!, task));
            _taskService.ResetOrchestratorForNewPlan(_taskId);
            _templateService.IncrementUseCount(template.Id);
            await _publisher.PublishAsync(Events.OnWorkPlanUpdated, new WorkPlanUpdatedArgs(_taskId, planJson));
            await PlanTools.RequestPlanConfirmationAsync(_taskService, _publisher, _taskId);

            return $"已加载计划模板：{template.Name}，共 {plan!.MainSteps.Count} 个主步骤。请等待用户确认后再执行计划。";
        }

        return AIFunctionFactory.Create(LoadPlanFromTemplateAsync, new AIFunctionFactoryOptions
        {
            Name = "load_plan_from_template",
            Description = "从模板加载计划到当前任务，会覆盖当前任务的 CurrentPlanJson。"
        });
    }

    public AIFunction CreateSaveCurrentPlanAsTemplateTool()
    {
        [Description("把当前任务的工作计划保存为可复用模板。当前任务必须已经有计划。")]
        Task<string> SaveCurrentPlanAsTemplateAsync(
            [Description("模板名称")] string name,
            [Description("模板描述，可空")] string? description,
            [Description("模板分类，可空")] string? category,
            [Description("作用范围：user 或 workspace。默认 user")] string? scope,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Task.FromResult("错误：模板名称不能为空");
            }

            var task = _taskService.GetById(_taskId);
            if (task is null)
            {
                return Task.FromResult("错误：任务不存在");
            }

            if (string.IsNullOrWhiteSpace(task.CurrentPlanJson))
            {
                return Task.FromResult("错误：当前任务还没有计划，不能保存为模板");
            }

            WorkPlan? plan;
            try
            {
                plan = JsonSerializer.Deserialize(task.CurrentPlanJson, WorkModeJsonContext.Default.WorkPlan);
            }
            catch (JsonException ex)
            {
                return Task.FromResult($"错误：当前计划 JSON 无效 - {ex.Message}");
            }

            var validationError = ValidatePlan(plan);
            if (validationError is not null)
            {
                return Task.FromResult(validationError);
            }

            var normalizedScope = NormalizeScope(scope);
            var planJson = JsonSerializer.Serialize(plan!, WorkModeJsonContext.Default.WorkPlan);
            var entity = new WorkPlanTemplateEntity
            {
                Name = name.Trim(),
                Description = Normalize(description) ?? string.Empty,
                Category = Normalize(category) ?? string.Empty,
                PlanJson = planJson,
                SourceTaskId = _taskId,
                SourceKind = "task",
                Scope = normalizedScope,
                WorkspaceId = normalizedScope == "workspace" ? task.WorkspaceId : null,
            };

            _templateService.Create(entity);
            return Task.FromResult($"已保存计划模板：{entity.Name}，id: {entity.Id}");
        }

        return AIFunctionFactory.Create(SaveCurrentPlanAsTemplateAsync, new AIFunctionFactoryOptions
        {
            Name = "save_current_plan_as_template",
            Description = "把当前任务的计划保存为模板，供后续任务复用。"
        });
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string NormalizeScope(string? scope)
    {
        var normalized = Normalize(scope)?.ToLowerInvariant();
        return normalized is "workspace" ? "workspace" : "user";
    }

    private static string? ValidatePlan(WorkPlan? plan)
    {
        if (plan?.MainSteps is null || plan.MainSteps.Count == 0)
        {
            return "错误：模板计划为空或缺少 main_steps";
        }

        for (var i = 0; i < plan.MainSteps.Count; i++)
        {
            var mainStep = plan.MainSteps[i];
            if (string.IsNullOrWhiteSpace(mainStep.Title))
            {
                return $"错误：模板第 {i + 1} 个主步骤缺少 title";
            }

            if (mainStep.SubSteps is null || mainStep.SubSteps.Count == 0)
            {
                return $"错误：模板主步骤 {i + 1} 缺少 sub_steps";
            }

            for (var j = 0; j < mainStep.SubSteps.Count; j++)
            {
                var subStep = mainStep.SubSteps[j];
                if (string.IsNullOrWhiteSpace(subStep.Title))
                {
                    return $"错误：模板主步骤 {i + 1} 的第 {j + 1} 个子步骤缺少 title";
                }

                if (string.IsNullOrWhiteSpace(subStep.AcceptanceCriteria))
                {
                    return $"错误：模板主步骤 {i + 1} 的第 {j + 1} 个子步骤缺少 acceptance_criteria";
                }
            }
        }

        return null;
    }
}
