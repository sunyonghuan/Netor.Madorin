namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 意图分类器实现（关键词版本 + AI 兜底）。
/// v1.0 使用关键词匹配，AI 兜底先返回 Unknown。
/// </summary>
public sealed class IntentClassifier : IIntentClassifier
{
    // 软抢占关键词
    private static readonly string[] SoftPreemptionKeywords = new[]
    {
        "立刻停", "马上停", "暂停", "等一下", "先别做", "停下"
    };

    // 取消任务关键词
    private static readonly string[] CancelKeywords = new[]
    {
        "取消", "不做了", "算了", "放弃"
    };

    public Task<WorkModeIntent> ClassifyAsync(string input, bool hasActiveTask, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
            return Task.FromResult(WorkModeIntent.Unknown);

        var normalized = input.Trim();

        // 无活跃任务 → 新建任务
        if (!hasActiveTask)
            return Task.FromResult(WorkModeIntent.NewTask);

        // 有活跃任务 → 检测关键词
        if (ContainsAny(normalized, CancelKeywords))
            return Task.FromResult(WorkModeIntent.CancelTask);

        if (ContainsAny(normalized, SoftPreemptionKeywords))
            return Task.FromResult(WorkModeIntent.SoftPreemption);

        // 默认：继续任务（多轮对话）
        // TODO: 阶段 3 完善 ResumeTask 检测（检查 WorkTasks.PendingRequestId）
        return Task.FromResult(WorkModeIntent.ContinueTask);
    }

    private static bool ContainsAny(string input, string[] keywords)
    {
        foreach (var keyword in keywords)
        {
            if (input.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
