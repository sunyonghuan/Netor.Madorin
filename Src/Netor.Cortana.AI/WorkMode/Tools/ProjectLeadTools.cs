using System.ComponentModel;
using System.Text.Json;

using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.AI.WorkMode.Files;
using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.ProjectLead;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 三层工作模式的 A 层移交工具。
/// </summary>
public sealed class ProjectLeadTools(
    WorkTaskFileService fileService,
    ProjectLeadService projectLeadService,
    ILogger<ProjectLeadTools> logger,
    string taskId,
    WorkTaskService? taskService = null,
    WorkTaskCancellationRegistry? cancellationRegistry = null)
{
    public AIFunction CreateFinalizePlanTool()
    {
        [Description("确认并交付已写入 plan.yaml 的三层工作计划，启动项目组长服务。")]
        string FinalizePlan(CancellationToken ct)
        {
            try
            {
                var guard = ValidateFinalizeGuard();
                if (guard is not null)
                {
                    return guard;
                }

                var plan = LoadExistingPlan();
                plan.Status = WorkTaskPlanStatuses.Running;
                fileService.SavePlan(plan);
                taskService?.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
                StartProjectLeadInBackground();

                return $"计划已交付项目组长执行，共 {plan.Steps.Count} 个步骤。";
            }
            catch (Exception ex)
            {
                return $"错误：交付计划失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(FinalizePlan, new AIFunctionFactoryOptions
        {
            Name = "finalize_plan",
            Description = "把已确认的 plan.yaml 交给 B 层 ProjectLeadService 执行，不需要传入计划内容。"
        });
    }

    private string? ValidateFinalizeGuard()
    {
        if (!File.Exists(fileService.GetPlanPath(taskId)))
        {
            return "错误：尚未制定计划，不能交付项目组长执行。请先调用 set_plan。";
        }

        var plan = LoadExistingPlan();
        if (!string.Equals(plan.Status, WorkTaskPlanStatuses.Planning, StringComparison.Ordinal))
        {
            return $"错误：计划当前状态是 {plan.Status}，不能 finalize。";
        }

        var task = taskService?.GetById(taskId);
        if (string.Equals(task?.PendingRequestKind, PlanTools.PlanConfirmationKind, StringComparison.Ordinal))
        {
            return "错误：计划尚未经过用户确认，不能交付项目组长执行。";
        }

        var environmentError = ValidateEnvironment();
        if (environmentError is not null)
        {
            return environmentError;
        }

        return task?.OrchestratorState switch
        {
            WorkTaskOrchestratorStates.Running => "错误：项目组长已在执行中，不能重复 finalize。",
            _ => null
        };
    }

    private string? ValidateEnvironment()
    {
        var environment = fileService.LoadEnvironment(taskId);
        if (environment is null || environment.Values.Count == 0)
        {
            return "错误：尚未设置任务环境，不能交付项目组长执行。请先调用 set_environment，至少写入 workspace_dir 和 task_goal。";
        }

        var missing = new List<string>();
        if (!HasAnyEnvironmentValue(environment, "workspace_dir", "workspace", "working_dir", "current_working_directory"))
        {
            missing.Add("workspace_dir");
        }

        if (!HasAnyEnvironmentValue(environment, "task_goal", "goal", "objective", "task_objective"))
        {
            missing.Add("task_goal");
        }

        return missing.Count == 0
            ? null
            : $"错误：任务环境缺少必要字段 {string.Join(", ", missing)}。请调用 set_environment 补齐后再交付项目组长执行。";
    }

    private static bool HasAnyEnvironmentValue(WorkTaskEnvironmentFile environment, params string[] keys)
        => keys.Any(key => environment.Values.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value));

    public AIFunction CreatePauseOrchestratorTool()
    {
        [Description("暂停 B 层项目组长推进。用于用户提出重大变更，需要先停止继续调度时。")]
        string PauseOrchestrator([Description("暂停原因。")] string reason)
        {
            return SetPlanStatus(
                WorkTaskPlanStatuses.Paused,
                string.IsNullOrWhiteSpace(reason) ? "编排器已暂停。" : $"编排器已暂停：{reason.Trim()}");
        }

        return AIFunctionFactory.Create(PauseOrchestrator, new AIFunctionFactoryOptions
        {
            Name = "pause_orchestrator",
            Description = "暂停三层工作模式的 B 层调度循环，当前已完成内容保留。"
        });
    }

    public AIFunction CreateResumeOrchestratorTool()
    {
        [Description("恢复 B 层项目组长推进。用于计划确认或暂停后继续执行。")]
        string ResumeOrchestrator()
        {
            try
            {
                var plan = LoadExistingPlan();
                if (string.Equals(plan.Status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal)
                    || string.Equals(plan.Status, WorkTaskPlanStatuses.Done, StringComparison.Ordinal))
                {
                    return $"当前计划状态为 {plan.Status}，无法恢复。";
                }

                plan.Status = WorkTaskPlanStatuses.Running;
                fileService.SavePlan(plan);
                taskService?.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Running, touchHeartbeat: true);
                StartProjectLeadInBackground();
                return "编排器已恢复，项目组长将继续读取 plan.yaml 推进。";
            }
            catch (Exception ex)
            {
                return $"错误：恢复编排器失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(ResumeOrchestrator, new AIFunctionFactoryOptions
        {
            Name = "resume_orchestrator",
            Description = "恢复三层工作模式的 B 层调度循环。"
        });
    }

    public AIFunction CreateCancelOrchestratorTool()
    {
        [Description("取消 B 层项目组长推进。用于用户明确要求停止本轮计划。")]
        string CancelOrchestrator([Description("取消原因。")] string reason)
        {
            return SetPlanStatus(
                WorkTaskPlanStatuses.Cancelled,
                string.IsNullOrWhiteSpace(reason) ? "编排器已取消。" : $"编排器已取消：{reason.Trim()}");
        }

        return AIFunctionFactory.Create(CancelOrchestrator, new AIFunctionFactoryOptions
        {
            Name = "cancel_orchestrator",
            Description = "把 plan.yaml 标记为 cancelled，B 层下一轮读取后退出。"
        });
    }

    public AIFunction CreateUpdatePlanTool()
    {
        [Description("更新尚未开始或已失败的计划步骤。running/done 步骤会被保留，不能覆盖。")]
        string UpdatePlan(
            [Description("计划更新 JSON。格式：{\"steps\":[{\"id\":\"ch-02\",\"title\":\"第二章\",\"role\":\"novel-writer\",\"input\":\"新要求\",\"expandable\":false,\"expander_role\":\"planner\"}]}。已开始或完成步骤不能覆盖。")] string updateJson,
            CancellationToken ct)
        {
            try
            {
                var plan = LoadExistingPlan();
                var protectedSteps = plan.Steps
                    .Where(static step => string.Equals(step.Status, WorkTaskPlanStepStatuses.Running, StringComparison.Ordinal)
                        || string.Equals(step.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal))
                    .ToList();
                var protectedIds = protectedSteps
                    .Select(static step => step.Id)
                    .ToHashSet(StringComparer.Ordinal);
                var replacementSteps = WorkTaskPlanStepJsonParser.ParseSteps(updateJson);

                foreach (var step in replacementSteps)
                {
                    if (protectedIds.Contains(step.Id))
                    {
                        return $"错误：步骤 {step.Id} 已经开始或完成，不能通过 update_plan 覆盖。";
                    }
                }

                plan.Steps = [.. protectedSteps, .. replacementSteps];
                plan.Status = WorkTaskPlanStatuses.Paused;
                fileService.SavePlan(plan);
                SyncLegacyPlanCache(plan);
                taskService?.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Paused, touchHeartbeat: false);
                return $"计划已更新：保留 {protectedSteps.Count} 个 running/done 步骤，写入 {replacementSteps.Count} 个待执行步骤。请确认后调用 resume_orchestrator。";
            }
            catch (JsonException ex)
            {
                return $"错误：计划更新 JSON 格式无效 - {ex.Message}";
            }
            catch (Exception ex)
            {
                return $"错误：更新计划失败 - {ex.Message}";
            }
        }

        return AIFunctionFactory.Create(UpdatePlan, new AIFunctionFactoryOptions
        {
            Name = "update_plan",
            Description = "更新 plan.yaml 中尚未开始或已失败的步骤，已 running/done 的步骤保持不变。"
        });
    }

    private void SyncLegacyPlanCache(WorkTaskPlanFile plan)
    {
        var legacyPlan = WorkPlanLegacyConverter.ToLegacyPlan(plan);
        var legacyPlanJson = JsonSerializer.Serialize(legacyPlan, WorkModeJsonContext.Default.WorkPlan);
        taskService?.UpdatePlan(taskId, legacyPlanJson);
    }

    private WorkTaskPlanFile LoadExistingPlan()
    {
        return fileService.LoadPlan(taskId)
            ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
    }

    private string SetPlanStatus(string status, string successMessage)
    {
        try
        {
            var plan = LoadExistingPlan();
            plan.Status = status;
            fileService.SavePlan(plan);
            if (string.Equals(status, WorkTaskPlanStatuses.Paused, StringComparison.Ordinal))
            {
                taskService?.SetOrchestratorState(taskId, WorkTaskOrchestratorStates.Paused, touchHeartbeat: false);
            }
            else if (string.Equals(status, WorkTaskPlanStatuses.Cancelled, StringComparison.Ordinal))
            {
                taskService?.RecordExecutionCancelled(taskId, successMessage);
            }
            return successMessage;
        }
        catch (Exception ex)
        {
            return $"错误：更新编排器状态失败 - {ex.Message}";
        }
    }

    private void StartProjectLeadInBackground()
    {
        _ = Task.Run(async () =>
        {
            using var nonExpertScope = NonExpertProcessScope.Enter();
            using var linkedCts = new CancellationTokenSource();
            var registered = cancellationRegistry?.TryRegisterOrchestrator(taskId, linkedCts) ?? false;
            try
            {
                await projectLeadService.RunAsync(taskId, linkedCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                logger.LogInformation("项目组长服务已取消：TaskId={TaskId}", taskId);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "项目组长服务启动后执行失败：TaskId={TaskId}", taskId);
            }
            finally
            {
                if (registered)
                {
                    cancellationRegistry?.TryUnregisterOrchestrator(taskId);
                }
            }
        }, CancellationToken.None);
    }

}
