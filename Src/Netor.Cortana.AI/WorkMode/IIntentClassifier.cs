namespace Netor.Cortana.AI.WorkMode;

/// <summary>
/// 意图分类器，判断用户输入的意图类型。
/// </summary>
public interface IIntentClassifier
{
    /// <summary>
    /// 分类用户输入的意图。
    /// </summary>
    /// <param name="input">用户输入文本。</param>
    /// <param name="hasActiveTask">当前是否有活跃任务。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>意图类型。</returns>
    Task<WorkModeIntent> ClassifyAsync(string input, bool hasActiveTask, CancellationToken cancellationToken = default);
}

/// <summary>
/// 工作模式意图类型。
/// </summary>
public enum WorkModeIntent
{
    /// <summary>新建任务。</summary>
    NewTask,

    /// <summary>继续当前任务（多轮对话）。</summary>
    ContinueTask,

    /// <summary>回答 HITL 挂起请求。</summary>
    ResumeTask,

    /// <summary>软抢占（插话）。</summary>
    SoftPreemption,

    /// <summary>取消任务。</summary>
    CancelTask,

    /// <summary>未知意图（兜底）。</summary>
    Unknown
}
