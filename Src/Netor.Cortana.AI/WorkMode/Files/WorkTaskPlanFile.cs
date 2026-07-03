using System.Globalization;

namespace Netor.Cortana.AI.WorkMode.Files;

/// <summary>
/// 三层工作模式的文件版执行计划，对应工作区中的 plan.yaml。
/// </summary>
public sealed class WorkTaskPlanFile
{
    public int Version { get; set; } = 1;

    public string TaskId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public string Status { get; set; } = WorkTaskPlanStatuses.Planning;

    public List<WorkTaskPlanStepFile> Steps { get; set; } = [];
}

/// <summary>
/// 文件版执行计划中的一个顺序步骤。
/// </summary>
public sealed class WorkTaskPlanStepFile
{
    public string Id { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string? Role { get; set; }

    public string Input { get; set; } = string.Empty;

    public string Status { get; set; } = WorkTaskPlanStepStatuses.Pending;

    public string? Summary { get; set; }

    public bool IsMilestone { get; set; }

    public bool IsExpandable { get; set; }

    public string? ExpanderRole { get; set; }

    public string? ParentStepId { get; set; }

    public int Depth { get; set; }

    public DateTimeOffset? ExpandedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public static class WorkTaskPlanStatuses
{
    public const string Planning = "planning";
    public const string Running = "running";
    public const string Paused = "paused";
    public const string Cancelled = "cancelled";
    public const string Done = "done";
    public const string Failed = "failed";
}

public static class WorkTaskPlanStepStatuses
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Done = "done";
    public const string Failed = "failed";
}

internal static class WorkTaskPlanFileYaml
{
    public static string Serialize(WorkTaskPlanFile plan)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var lines = new List<string>
        {
            $"version: {plan.Version.ToString(CultureInfo.InvariantCulture)}",
            $"task_id: {YamlScalar.Escape(plan.TaskId)}",
            $"created_at: {YamlScalar.Escape(plan.CreatedAt.ToString("O", CultureInfo.InvariantCulture))}",
            $"status: {YamlScalar.Escape(plan.Status)}",
            "steps:"
        };

        foreach (var step in plan.Steps)
        {
            lines.Add($"  - id: {YamlScalar.Escape(step.Id)}");
            lines.Add($"    title: {YamlScalar.Escape(step.Title)}");
            lines.Add($"    role: {YamlScalar.Escape(step.Role ?? string.Empty)}");
            lines.Add($"    input: {YamlScalar.Escape(step.Input)}");
            lines.Add($"    status: {YamlScalar.Escape(step.Status)}");
            lines.Add($"    summary: {YamlScalar.Escape(step.Summary ?? string.Empty)}");
            lines.Add($"    is_milestone: {YamlScalar.ToBool(step.IsMilestone)}");
            lines.Add($"    expandable: {YamlScalar.ToBool(step.IsExpandable)}");
            lines.Add($"    expander_role: {YamlScalar.Escape(step.ExpanderRole ?? string.Empty)}");
            lines.Add($"    parent_step_id: {YamlScalar.Escape(step.ParentStepId ?? string.Empty)}");
            lines.Add($"    depth: {step.Depth.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"    expanded_at: {YamlScalar.Escape(step.ExpandedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty)}");
            lines.Add($"    attempts: {step.Attempts.ToString(CultureInfo.InvariantCulture)}");
            lines.Add($"    last_error: {YamlScalar.Escape(step.LastError ?? string.Empty)}");
            lines.Add($"    created_at: {YamlScalar.Escape(step.CreatedAt.ToString("O", CultureInfo.InvariantCulture))}");
            lines.Add($"    updated_at: {YamlScalar.Escape(step.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))}");
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    public static WorkTaskPlanFile Deserialize(string yaml)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var plan = new WorkTaskPlanFile();
        WorkTaskPlanStepFile? currentStep = null;

        foreach (var rawLine in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("  - ", StringComparison.Ordinal))
            {
                currentStep = new WorkTaskPlanStepFile();
                plan.Steps.Add(currentStep);
                SetStepScalar(currentStep, line[4..]);
                continue;
            }

            if (line.StartsWith("    ", StringComparison.Ordinal) && currentStep is not null)
            {
                SetStepScalar(currentStep, line[4..]);
                continue;
            }

            currentStep = null;
            SetPlanScalar(plan, line);
        }

        return plan;
    }

    private static void SetPlanScalar(WorkTaskPlanFile plan, string line)
    {
        if (!YamlScalar.TrySplit(line, out var key, out var value))
        {
            return;
        }

        switch (key)
        {
            case "version":
                plan.Version = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var version) ? version : 1;
                break;
            case "task_id":
                plan.TaskId = YamlScalar.Unescape(value);
                break;
            case "created_at":
                plan.CreatedAt = ParseDate(value);
                break;
            case "status":
                plan.Status = YamlScalar.Unescape(value);
                break;
        }
    }

    private static void SetStepScalar(WorkTaskPlanStepFile step, string line)
    {
        if (!YamlScalar.TrySplit(line, out var key, out var value))
        {
            return;
        }

        switch (key)
        {
            case "id":
                step.Id = YamlScalar.Unescape(value);
                break;
            case "title":
                step.Title = YamlScalar.Unescape(value);
                break;
            case "role":
                step.Role = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                break;
            case "input":
                step.Input = YamlScalar.Unescape(value);
                break;
            case "status":
                step.Status = YamlScalar.Unescape(value);
                break;
            case "summary":
                step.Summary = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                break;
            case "is_milestone":
                step.IsMilestone = YamlScalar.ParseBool(value);
                break;
            case "expandable":
            case "is_expandable":
                step.IsExpandable = YamlScalar.ParseBool(value);
                break;
            case "expander_role":
            case "expander":
                step.ExpanderRole = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                break;
            case "parent_step_id":
            case "parent_id":
                step.ParentStepId = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                break;
            case "depth":
                step.Depth = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var depth) ? Math.Max(0, depth) : 0;
                break;
            case "expanded_at":
                var expandedAt = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                step.ExpandedAt = expandedAt is null ? null : ParseDate(expandedAt);
                break;
            case "attempts":
                step.Attempts = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempts) ? attempts : 0;
                break;
            case "last_error":
                step.LastError = YamlScalar.NullIfEmpty(YamlScalar.Unescape(value));
                break;
            case "created_at":
                step.CreatedAt = ParseDate(value);
                break;
            case "updated_at":
                step.UpdatedAt = ParseDate(value);
                break;
        }
    }

    private static DateTimeOffset ParseDate(string value)
    {
        return DateTimeOffset.TryParse(
            YamlScalar.Unescape(value),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var timestamp)
            ? timestamp
            : DateTimeOffset.UtcNow;
    }
}
