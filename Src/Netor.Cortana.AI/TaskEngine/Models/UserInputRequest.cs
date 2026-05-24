namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 子智能体向用户提出的澄清问题。由引擎发布事件推送到 UI。
/// 在需求分析/计划制定阶段，当 LLM 输出 [ASK_USER] 标记时生成此请求。
/// </summary>
public sealed class UserInputRequest
{
    /// <summary>请求唯一 ID（用于匹配回答）。</summary>
    public required string RequestId { get; init; }

    /// <summary>关联任务 ID。</summary>
    public required string TaskId { get; init; }

    /// <summary>当前阶段（"requirements" / "planning"）。</summary>
    public required string Phase { get; init; }

    /// <summary>问题文本（子智能体生成的，已去除 [ASK_USER] 标记）。</summary>
    public required string Question { get; init; }

    /// <summary>可选的建议选项（LLM 可提供几个选项让用户快速选择）。</summary>
    public List<string>? SuggestedOptions { get; init; }

    /// <summary>当前对话轮次。</summary>
    public int Round { get; init; }

    /// <summary>请求发起时间。</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}
