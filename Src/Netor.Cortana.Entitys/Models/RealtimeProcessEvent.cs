namespace Netor.Cortana.Entitys;

/// <summary>
/// 当前对话轮次内的实时过程事件。
/// </summary>
public sealed class RealtimeProcessEvent
{
    /// <summary>对话轮次 ID。</summary>
    public string TurnId { get; init; } = string.Empty;

    /// <summary>过程 ID，同一过程的事件使用相同 ID。</summary>
    public string ProcessId { get; init; } = string.Empty;

    /// <summary>过程类型：tool / command / thinking / agent。</summary>
    public string Kind { get; init; } = string.Empty;

    /// <summary>显示标题。</summary>
    public string Title { get; init; } = string.Empty;

    /// <summary>状态：running / success / failed / cancelled。</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>详细内容。</summary>
    public string Content { get; init; } = string.Empty;

    /// <summary>命令退出码。</summary>
    public int? ExitCode { get; init; }

    /// <summary>耗时毫秒。</summary>
    public long DurationMs { get; init; }

    /// <summary>事件时间。</summary>
    public DateTimeOffset Timestamp { get; init; }
}
