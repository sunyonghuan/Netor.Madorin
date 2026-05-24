namespace Netor.Cortana.AI.TaskEngine.Models;

/// <summary>
/// 验证阶段结果。由验证审查员子智能体产出，持久化到文件供 UI 展示和历史回看。
/// </summary>
public sealed class ValidationResult
{
    /// <summary>验证是否通过。</summary>
    public bool Passed { get; set; }

    /// <summary>验证分数（0-100）。</summary>
    public int Score { get; set; }

    /// <summary>验证摘要（给用户看的一句话总结）。</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>发现的问题列表（验证未通过时有值）。</summary>
    public List<string> Issues { get; set; } = [];

    /// <summary>改进建议列表（可选）。</summary>
    public List<string> Suggestions { get; set; } = [];

    /// <summary>验证完成时间。</summary>
    public DateTimeOffset CompletedAt { get; set; }
}
