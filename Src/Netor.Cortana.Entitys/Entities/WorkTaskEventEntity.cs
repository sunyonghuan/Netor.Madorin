namespace Netor.Cortana.Entitys;

/// <summary>
/// 工作模式 B 层写给 A/UI 的任务事件。
/// </summary>
public sealed class WorkTaskEventEntity
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string TaskId { get; set; } = string.Empty;

    public string? RunId { get; set; }

    public string Kind { get; set; } = WorkTaskEventKinds.Milestone;

    public string Message { get; set; } = string.Empty;

    public long CreatedAt { get; set; }

    public long? ReadAt { get; set; }
}

public static class WorkTaskEventKinds
{
    public const string Milestone = "Milestone";
    public const string Error = "Error";
    public const string Completion = "Completion";
}

public static class WorkTaskOrchestratorStates
{
    public const string Idle = "idle";
    public const string Running = "running";
    public const string PauseRequested = "pause_requested";
    public const string Paused = "paused";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
}
