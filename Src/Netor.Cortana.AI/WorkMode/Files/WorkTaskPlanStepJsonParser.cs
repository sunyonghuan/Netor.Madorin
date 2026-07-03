using System.Text.Json;

namespace Netor.Cortana.AI.WorkMode.Files;

/// <summary>
/// 解析 A/B 层工具传入的 plan step JSON。
/// </summary>
internal static class WorkTaskPlanStepJsonParser
{
    public static List<WorkTaskPlanStepFile> ParseSteps(
        string planJson,
        string? parentStepId = null,
        int depth = 0)
    {
        using var document = JsonDocument.Parse(ExtractJson(planJson));
        var stepsElement = GetStepsElement(document.RootElement);

        var steps = new List<WorkTaskPlanStepFile>();
        var stepIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            if (stepElement.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("steps 中的每一项都必须是对象。");
            }

            var id = GetRequiredString(stepElement, "id");
            var title = GetRequiredString(stepElement, "title");
            var input = GetStringOrRaw(stepElement, "input");
            if (!stepIds.Add(id))
            {
                throw new JsonException($"步骤 id 重复：{id}。");
            }

            var isExpandable = GetOptionalBool(stepElement, "is_expandable")
                || GetOptionalBool(stepElement, "expandable");
            var expanderRole = GetOptionalString(stepElement, "expander_role")
                ?? GetOptionalString(stepElement, "expander");

            steps.Add(new WorkTaskPlanStepFile
            {
                Id = id,
                Title = title,
                Role = GetOptionalString(stepElement, "role"),
                Input = input,
                Status = WorkTaskPlanStepStatuses.Pending,
                IsMilestone = GetOptionalBool(stepElement, "is_milestone"),
                IsExpandable = isExpandable,
                ExpanderRole = expanderRole,
                ParentStepId = parentStepId ?? GetOptionalString(stepElement, "parent_step_id"),
                Depth = depth,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

        if (steps.Count == 0)
        {
            throw new JsonException("计划必须至少包含一个步骤。");
        }

        return steps;
    }

    private static JsonElement GetStepsElement(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array)
        {
            return root;
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("计划根节点必须是 JSON 对象或步骤数组。");
        }

        if (!root.TryGetProperty("steps", out var stepsElement) || stepsElement.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("计划必须包含 steps 数组。");
        }

        return stepsElement;
    }

    private static string ExtractJson(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var trimmed = value.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var firstNewLine = trimmed.IndexOf('\n', StringComparison.Ordinal);
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewLine >= 0 && lastFence > firstNewLine
            ? trimmed[(firstNewLine + 1)..lastFence].Trim()
            : trimmed;
    }

    private static string GetRequiredString(JsonElement element, string propertyName)
    {
        var value = GetOptionalString(element, propertyName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new JsonException($"步骤缺少必填字段 {propertyName}。");
        }

        return value;
    }

    private static string? GetOptionalString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()?.Trim()
            : null;
    }

    private static string GetStringOrRaw(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return string.Empty;
        }

        return property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : property.GetRawText();
    }

    private static bool GetOptionalBool(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => false
        };
    }
}
