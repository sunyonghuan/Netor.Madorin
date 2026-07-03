using System.Text.Json;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.Entitys;

namespace Netor.Cortana.AI.WorkMode.Files;

/// <summary>
/// 管理工作模式三层架构的任务文件目录。
/// </summary>
public sealed class WorkTaskFileService(IAppPaths appPaths)
{
    public const string PlanFileName = "plan.yaml";
    public const string EnvironmentFileName = "environment.yaml";

    public string TasksDirectory => Path.Combine(appPaths.WorkspaceDirectory, ".cortana", "work-tasks");

    public string GetTaskDirectory(string taskId)
    {
        ValidateTaskId(taskId);
        return Path.Combine(TasksDirectory, taskId);
    }

    public string GetPlanPath(string taskId)
    {
        return Path.Combine(GetTaskDirectory(taskId), PlanFileName);
    }

    public void SavePlan(WorkTaskPlanFile plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ValidatePlan(plan);

        var directory = GetTaskDirectory(plan.TaskId);
        Directory.CreateDirectory(directory);
        File.WriteAllText(GetPlanPath(plan.TaskId), WorkTaskPlanFileYaml.Serialize(plan));
    }

    public WorkTaskPlanFile? LoadPlan(string taskId)
    {
        var path = GetPlanPath(taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                return WorkTaskPlanFileYaml.Deserialize(File.ReadAllText(path));
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(20);
            }
        }

        return WorkTaskPlanFileYaml.Deserialize(File.ReadAllText(path));
    }

    public WorkTaskPlanFile UpdateStepStatus(
        string taskId,
        string stepId,
        string status,
        string? summary = null,
        string? error = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepId);
        ArgumentException.ThrowIfNullOrWhiteSpace(status);

        var plan = LoadPlan(taskId) ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
        var step = plan.Steps.FirstOrDefault(item => string.Equals(item.Id, stepId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"任务计划步骤不存在：{stepId}");

        step.Status = status.Trim();
        step.Summary = string.IsNullOrWhiteSpace(summary) ? step.Summary : summary.Trim();
        step.LastError = string.IsNullOrWhiteSpace(error) ? step.LastError : error.Trim();
        step.UpdatedAt = DateTimeOffset.UtcNow;

        if (plan.Steps.Count > 0 && plan.Steps.All(static item => string.Equals(item.Status, WorkTaskPlanStepStatuses.Done, StringComparison.Ordinal)))
        {
            plan.Status = WorkTaskPlanStatuses.Done;
        }
        else if (plan.Steps.Any(static item => string.Equals(item.Status, WorkTaskPlanStepStatuses.Running, StringComparison.Ordinal)))
        {
            plan.Status = WorkTaskPlanStatuses.Running;
        }

        SavePlan(plan);
        return plan;
    }

    public WorkTaskPlanStepFile? ResetRunningStepForInterrupt(string taskId, string interruptInput)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(interruptInput);

        var plan = LoadPlan(taskId) ?? throw new InvalidOperationException($"任务计划不存在：{taskId}");
        var step = plan.Steps.FirstOrDefault(static item => string.Equals(item.Status, WorkTaskPlanStepStatuses.Running, StringComparison.Ordinal));
        if (step is null)
        {
            return null;
        }

        var normalizedInterrupt = interruptInput.Trim();
        step.Status = WorkTaskPlanStepStatuses.Pending;
        step.Summary = null;
        step.LastError = null;
        step.UpdatedAt = DateTimeOffset.UtcNow;
        step.Input = $$"""
            {{step.Input}}

            # 用户最新插话（需优先吸收并重做当前步骤）
            {{normalizedInterrupt}}
            """;

        plan.Status = WorkTaskPlanStatuses.Running;
        SavePlan(plan);
        return step;
    }

    public void SaveEnvironment(WorkTaskEnvironmentFile environment)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ValidateTaskId(environment.TaskId);

        var directory = GetTaskDirectory(environment.TaskId);
        Directory.CreateDirectory(directory);
        environment.UpdatedAt = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(directory, EnvironmentFileName), WorkTaskEnvironmentFileYaml.Serialize(environment));
    }

    public WorkTaskEnvironmentFile? LoadEnvironment(string taskId)
    {
        var path = Path.Combine(GetTaskDirectory(taskId), EnvironmentFileName);
        return File.Exists(path)
            ? WorkTaskEnvironmentFileYaml.Deserialize(File.ReadAllText(path))
            : null;
    }

    private static void ValidatePlan(WorkTaskPlanFile plan)
    {
        ValidateTaskId(plan.TaskId);
        if (plan.Version <= 0)
        {
            throw new InvalidDataException("plan.yaml version 必须大于 0。");
        }

        if (plan.Steps.Count == 0)
        {
            throw new InvalidDataException("plan.yaml 必须至少包含一个步骤。");
        }

        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var step in plan.Steps)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Id);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Title);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Status);
            if (!stepIds.Add(step.Id))
            {
                throw new InvalidDataException($"plan.yaml 步骤 id 重复：{step.Id}。");
            }

            if (step.IsExpandable && string.IsNullOrWhiteSpace(step.ExpanderRole))
            {
                throw new InvalidDataException($"可展开步骤 {step.Id} 必须指定 expander_role。");
            }

            if (step.Depth < 0)
            {
                throw new InvalidDataException($"步骤 {step.Id} 的 depth 不能小于 0。");
            }
        }
    }

    private static void ValidateTaskId(string taskId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        if (taskId.Length > 128 || taskId.Any(static c => !char.IsAsciiLetterOrDigit(c) && c is not '_' and not '-'))
        {
            throw new ArgumentException("taskId 只能包含字母、数字、下划线和短横线，且长度不能超过 128。", nameof(taskId));
        }
    }
}

internal static class YamlScalar
{
    public static bool TrySplit(string line, out string key, out string value)
    {
        var separator = line.IndexOf(':', StringComparison.Ordinal);
        if (separator < 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }

        key = line[..separator].Trim();
        value = line[(separator + 1)..].Trim();
        return key.Length > 0;
    }

    public static string EscapeKey(string value) => Escape(value);

    public static string Escape(string value)
    {
        return JsonSerializer.Serialize(value, WorkModeJsonContext.Default.String);
    }

    public static string Unescape(string value)
    {
        if (value.Length >= 2 && value[0] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize(value, WorkModeJsonContext.Default.String) ?? string.Empty;
            }
            catch (JsonException)
            {
                return value;
            }
        }

        return value;
    }

    public static string? NullIfEmpty(string value) => string.IsNullOrEmpty(value) ? null : value;

    public static string ToBool(bool value) => value ? "true" : "false";

    public static bool ParseBool(string value)
    {
        return Unescape(value).Trim().ToLowerInvariant() is "true" or "1" or "yes" or "on";
    }
}
