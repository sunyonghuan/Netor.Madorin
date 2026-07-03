using System.Text.Json;

using Netor.Cortana.AI.WorkMode.Json;
using Netor.Cortana.AI.WorkMode.Models;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.AI.WorkMode.Reliability;

/// <summary>
/// 工作模式死循环轻量检测器。
/// </summary>
public sealed class DeadlockDetector
{
    private const int RecentLogCount = 12;
    private const int RepeatedToolFailureThreshold = 3;
    private const int SelfEvaluationFailureThreshold = 5;
    private static readonly TimeSpan StalledThreshold = TimeSpan.FromMinutes(30);

    private readonly WorkTaskService _taskService;
    private readonly WorkExecutionLogService _logService;
    private readonly WorkPendingInputService _pendingInputService;

    public DeadlockDetector(
        WorkTaskService taskService,
        WorkExecutionLogService logService,
        WorkPendingInputService pendingInputService)
    {
        _taskService = taskService ?? throw new ArgumentNullException(nameof(taskService));
        _logService = logService ?? throw new ArgumentNullException(nameof(logService));
        _pendingInputService = pendingInputService ?? throw new ArgumentNullException(nameof(pendingInputService));
    }

    public DeadlockDetectionResult CheckToolCall(string taskId, string toolName, string? parametersJson)
    {
        var repeatedToolFailure = CheckRepeatedToolFailure(taskId, toolName, parametersJson);
        if (repeatedToolFailure.IsDeadlock)
        {
            return repeatedToolFailure;
        }

        var selfEvaluation = CheckSelfEvaluationOscillation(taskId);
        if (selfEvaluation.IsDeadlock)
        {
            return selfEvaluation;
        }

        return CheckProgressStalled(taskId);
    }

    public void Report(string taskId, string reason)
    {
        var record = new WorkExecutionLogRecords.DeadlockDetected(reason);
        _logService.Append(taskId, WorkExecutionLogTypes.DeadlockDetected,
            JsonSerializer.Serialize(record, WorkModeJsonContext.Default.DeadlockDetected));

        _pendingInputService.Enqueue(taskId,
            $"系统检测到任务可能陷入循环：{reason}。请停止重复尝试，先调整策略；如果必须继续，请调用 ask_user 让用户决策。");
        _taskService.SetPreemption(taskId, true);
    }

    private DeadlockDetectionResult CheckRepeatedToolFailure(string taskId, string toolName, string? parametersJson)
    {
        var recentLogs = _logService.RecentByTask(taskId, RecentLogCount);
        var failures = 0;
        foreach (var log in recentLogs)
        {
            if (log.LogType != WorkExecutionLogTypes.ToolResult)
            {
                continue;
            }

            WorkExecutionLogRecords.ToolResult? result;
            try
            {
                result = JsonSerializer.Deserialize(log.Content, WorkModeJsonContext.Default.ToolResult);
            }
            catch (JsonException)
            {
                continue;
            }

            if (result is null || !string.Equals(result.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var call = FindToolCall(recentLogs, result.CallId);
            if (call is null)
            {
                continue;
            }

            if (string.Equals(call.ToolName, toolName, StringComparison.Ordinal)
                && string.Equals(call.ParametersJson ?? string.Empty, parametersJson ?? string.Empty, StringComparison.Ordinal))
            {
                failures++;
            }
        }

        return failures >= RepeatedToolFailureThreshold
            ? DeadlockDetectionResult.RepeatedToolFailure(toolName)
            : DeadlockDetectionResult.Healthy();
    }

    private DeadlockDetectionResult CheckSelfEvaluationOscillation(string taskId)
    {
        var recentLogs = _logService.RecentByTask(taskId, RecentLogCount);
        var failures = 0;
        foreach (var log in recentLogs)
        {
            if (log.LogType != WorkExecutionLogTypes.SelfEvaluation)
            {
                continue;
            }

            WorkExecutionLogRecords.SelfEvaluation? evaluation;
            try
            {
                evaluation = JsonSerializer.Deserialize(log.Content, WorkModeJsonContext.Default.SelfEvaluation);
            }
            catch (JsonException)
            {
                continue;
            }

            if (evaluation is not null
                && evaluation.Score < 7
                && !evaluation.Verdict.Equals("PASS", StringComparison.OrdinalIgnoreCase))
            {
                failures++;
            }
        }

        return failures >= SelfEvaluationFailureThreshold
            ? DeadlockDetectionResult.SelfEvaluationOscillation()
            : DeadlockDetectionResult.Healthy();
    }

    private DeadlockDetectionResult CheckProgressStalled(string taskId)
    {
        var task = _taskService.GetById(taskId);
        if (task?.HeartbeatAt is null || !task.IsActive)
        {
            return DeadlockDetectionResult.Healthy();
        }

        var stalled = DateTimeOffset.Now - DateTimeOffset.FromUnixTimeMilliseconds(task.HeartbeatAt.Value);
        return stalled >= StalledThreshold
            ? DeadlockDetectionResult.ProgressStalled(stalled)
            : DeadlockDetectionResult.Healthy();
    }

    private static WorkExecutionLogRecords.ToolCall? FindToolCall(
        IEnumerable<WorkExecutionLogEntity> logs,
        string callId)
    {
        foreach (var log in logs)
        {
            if (log.LogType != WorkExecutionLogTypes.ToolCall)
            {
                continue;
            }

            try
            {
                var call = JsonSerializer.Deserialize(log.Content, WorkModeJsonContext.Default.ToolCall);
                if (string.Equals(call?.CallId, callId, StringComparison.Ordinal))
                {
                    return call;
                }
            }
            catch (JsonException)
            {
                // 忽略历史坏数据。
            }
        }

        return null;
    }
}

public sealed record DeadlockDetectionResult(bool IsDeadlock, string? Reason)
{
    public static DeadlockDetectionResult Healthy() => new(false, null);

    public static DeadlockDetectionResult RepeatedToolFailure(string tool)
        => new(true, $"工具 {tool} 使用相同参数连续失败，可能陷入重复调用。");

    public static DeadlockDetectionResult SelfEvaluationOscillation()
        => new(true, "自评连续不通过，可能陷入反复修改但无法收敛。");

    public static DeadlockDetectionResult ProgressStalled(TimeSpan duration)
        => new(true, $"任务超过 {duration.TotalMinutes:F0} 分钟没有有效进展。");
}
