using System.ComponentModel;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.AI;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Tools;

/// <summary>
/// 当前工作任务执行日志只读工具。
/// </summary>
public sealed class ExecutionLogTools
{
    private readonly WorkExecutionLogService _logService;
    private readonly string _taskId;

    public ExecutionLogTools(WorkExecutionLogService logService, string taskId)
    {
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _taskId = taskId ?? throw new ArgumentNullException(nameof(taskId));
    }

    public AIFunction CreateGetExecutionLogsTool()
    {
        [Description("读取当前工作任务的真实执行日志。用于用户追问过程、证据、工具调用、C 层实际结果时。")]
        Task<string> GetExecutionLogsAsync(
            [Description("可选日志类型，例如 StepStart、StepComplete、Thinking、ToolCall、ToolResult、Acceptance、SelfEvaluation、SubAgentJobStart、SubAgentJobEnd；留空表示全部。")] string? logType = null,
            [Description("可选步骤标题关键字；留空表示不过滤。")] string? stepTitle = null,
            [Description("最多返回条数，范围 1-100；默认 50。")] int take = 50,
            [Description("是否包含原始 Content JSON；默认 false，只返回摘要。")] bool includeRawContent = false,
            CancellationToken ct = default)
        {
            var logs = _logService.ListByTask(_taskId);
            if (!string.IsNullOrWhiteSpace(logType))
            {
                logs = logs
                    .Where(log => string.Equals(log.LogType, logType.Trim(), StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            if (!string.IsNullOrWhiteSpace(stepTitle))
            {
                var keyword = stepTitle.Trim();
                logs = logs
                    .Where(log => LogMentionsStep(log, keyword))
                    .ToList();
            }

            take = Math.Clamp(take, 1, 100);
            logs = logs.TakeLast(take).ToList();
            if (logs.Count == 0)
            {
                return Task.FromResult("当前任务没有匹配的执行日志。");
            }

            var sb = new StringBuilder();
            sb.AppendLine($"当前任务执行日志：最近 {logs.Count} 条");
            sb.AppendLine();
            foreach (var log in logs)
            {
                sb.AppendLine($"- #{log.Sequence} {log.LogType} {FormatTime(log.CreatedAt)}：{SummarizeLog(log)}");
                if (includeRawContent)
                {
                    sb.AppendLine($"  content: {Trim(log.Content, 1200)}");
                }
            }

            return Task.FromResult(sb.ToString());
        }

        return AIFunctionFactory.Create(GetExecutionLogsAsync, new AIFunctionFactoryOptions
        {
            Name = "get_execution_logs",
            Description = "读取当前工作任务的真实执行日志。只读工具，用于追溯 C 层过程、工具调用、验收和实际结果。"
        });
    }

    private static bool LogMentionsStep(WorkExecutionLogEntity log, string keyword)
    {
        if (log.Content.Contains(keyword, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!TryParse(log.Content, out var root))
        {
            return false;
        }

        return ReadString(root, "StepTitle", "stepTitle")?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || ReadString(root, "Department", "department")?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true
            || ReadString(root, "Query", "query")?.Contains(keyword, StringComparison.OrdinalIgnoreCase) == true;
    }

    private static string SummarizeLog(WorkExecutionLogEntity log)
    {
        if (!TryParse(log.Content, out var root))
        {
            return Trim(log.Content, 600);
        }

        return log.LogType switch
        {
            WorkExecutionLogTypes.StepStart => $"开始步骤「{ReadString(root, "StepTitle", "stepTitle") ?? "未知"}」，阶段「{ReadString(root, "Department", "department") ?? "未分组"}」",
            WorkExecutionLogTypes.StepComplete => $"完成步骤「{ReadString(root, "StepTitle", "stepTitle") ?? "未知"}」：{Trim(ReadString(root, "Result", "result") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.Thinking => $"{ReadString(root, "AuthorName", "authorName") ?? "thinking"}：{Trim(ReadString(root, "Text", "text") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.ToolCall => $"调用工具 {ReadString(root, "ToolName", "toolName") ?? "未知"}，callId={ReadString(root, "CallId", "callId") ?? "未知"}",
            WorkExecutionLogTypes.ToolResult => $"工具结果 callId={ReadString(root, "CallId", "callId") ?? "未知"}，状态={ReadString(root, "Status", "status") ?? "未知"}，{Trim(ReadString(root, "Error", "error") ?? ReadString(root, "ResultText", "resultText") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.Acceptance => $"验收步骤「{ReadString(root, "StepTitle", "stepTitle") ?? "未知"}」，结论={ReadString(root, "Verdict", "verdict") ?? "未知"}：{Trim(ReadString(root, "Reason", "reason") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.SelfEvaluation => $"自评步骤「{ReadString(root, "StepTitle", "stepTitle") ?? "未知"}」，分数={ReadInt(root, "Score", "score")?.ToString() ?? "未知"}，结论={ReadString(root, "Verdict", "verdict") ?? "未知"}",
            WorkExecutionLogTypes.DeadlockDetected => $"死循环风险：{Trim(ReadString(root, "Reason", "reason") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.ParallelBlockStart => $"并行块开始：{ReadString(root, "BlockId", "blockId") ?? "未知"}",
            WorkExecutionLogTypes.ParallelBlockEnd => $"并行块结束：{ReadString(root, "BlockId", "blockId") ?? "未知"}，成功 {ReadInt(root, "SuccessCount", "successCount") ?? 0}/{ReadInt(root, "TotalCount", "totalCount") ?? 0}",
            WorkExecutionLogTypes.NestedWorkflowStart => $"嵌套工作流开始：{ReadString(root, "NestedTaskId", "nestedTaskId") ?? "未知"}，目标：{Trim(ReadString(root, "Goal", "goal") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.NestedWorkflowEnd => $"嵌套工作流结束：{ReadString(root, "NestedTaskId", "nestedTaskId") ?? "未知"}，报告：{Trim(ReadString(root, "FinalReport", "finalReport") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.SubAgentJobStart => $"子智能体后台任务开始：{ReadString(root, "OwnerName", "ownerName") ?? "未知"}，{Trim(ReadString(root, "Query", "query") ?? string.Empty, 600)}",
            WorkExecutionLogTypes.SubAgentJobEnd => $"子智能体后台任务结束，状态={ReadString(root, "Status", "status") ?? "未知"}，{Trim(ReadString(root, "Error", "error") ?? ReadString(root, "ResultJson", "resultJson") ?? string.Empty, 600)}",
            _ => Trim(log.Content, 600)
        };
    }

    private static bool TryParse(string content, out JsonElement root)
    {
        try
        {
            using var doc = JsonDocument.Parse(content);
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            root = default;
            return false;
        }
    }

    private static string? ReadString(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    private static int? ReadInt(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value) && value.TryGetInt32(out var number))
            {
                return number;
            }
        }

        return null;
    }

    private static string FormatTime(long unixMilliseconds)
    {
        return DateTimeOffset.FromUnixTimeMilliseconds(unixMilliseconds).LocalDateTime.ToString("MM-dd HH:mm:ss");
    }

    private static string Trim(string text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxLength)
        {
            return text;
        }

        return text[..maxLength] + "...";
    }
}
