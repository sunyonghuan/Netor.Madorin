namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 步骤执行结果。每步完成后由子智能体产出，持久化到文件供后续步骤引用。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/04-P4方案设计-任务执行引擎.md §7.2。
/// </summary>
public sealed class StepResult
{
    /// <summary>步骤 ID。</summary>
    public string StepId { get; set; } = string.Empty;

    /// <summary>结果摘要（给用户看 + 传给后续步骤）。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>结果详情（给后续步骤的子智能体作为上下文，可为 null）。</summary>
    public string? Detail { get; set; }

    /// <summary>步骤完成时间。</summary>
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>该步骤消耗的 Token 数。</summary>
    public int TokensUsed { get; set; }
}
