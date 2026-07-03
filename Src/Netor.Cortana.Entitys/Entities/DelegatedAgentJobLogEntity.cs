namespace Netor.Cortana.Entitys;

/// <summary>
/// 专家模式后台委派任务轻量日志类型。
/// </summary>
public static class DelegatedAgentJobLogKinds
{
    public const string Started = "started";
    public const string Progress = "progress";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// 专家模式后台委派任务轻量日志。
/// </summary>
public sealed class DelegatedAgentJobLogEntity
{
    public long Id { get; set; }

    public string JobId { get; set; } = string.Empty;

    public string Kind { get; set; } = string.Empty;

    public string Message { get; set; } = string.Empty;

    public long CreatedAt { get; set; }
}
