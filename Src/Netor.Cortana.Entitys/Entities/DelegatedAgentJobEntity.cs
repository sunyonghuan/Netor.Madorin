namespace Netor.Cortana.Entitys;

/// <summary>
/// 专家模式后台委派任务状态。
/// </summary>
public static class DelegatedAgentJobStates
{
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}

/// <summary>
/// 专家模式后台委派任务记录。
/// </summary>
public sealed class DelegatedAgentJobEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ScopeKind { get; set; } = "chat";

    public string ScopeId { get; set; } = string.Empty;

    public string ParentTurnId { get; set; } = string.Empty;

    public string ParentAgentId { get; set; } = string.Empty;

    public string ChildName { get; set; } = string.Empty;

    public string ChildInstructions { get; set; } = string.Empty;

    public string TaskInputJson { get; set; } = string.Empty;

    public string ToolMountsJson { get; set; } = "[]";

    public string? ProviderId { get; set; }

    public string? ModelId { get; set; }

    public string State { get; set; } = DelegatedAgentJobStates.Pending;

    public string? ProgressDescription { get; set; }

    public string? ResultJson { get; set; }

    public string? Error { get; set; }

    public long CreatedAt { get; set; }

    public long UpdatedAt { get; set; }

    public long? CompletedAt { get; set; }
}
