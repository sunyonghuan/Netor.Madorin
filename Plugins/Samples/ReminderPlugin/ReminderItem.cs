namespace ReminderPlugin;

/// <summary>
/// 提醒实体。
/// </summary>
public sealed class ReminderItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;

    /// <summary>下次触发时间（UTC）。</summary>
    public DateTimeOffset TriggerTime { get; set; }

    /// <summary>重复类型：once / daily / weekly / monthly / custom。</summary>
    public string RepeatType { get; set; } = "once";

    /// <summary>自定义间隔（分钟），仅 RepeatType=custom 时有效。</summary>
    public int RepeatIntervalMinutes { get; set; }

    /// <summary>最大触发次数（含首次）。0 表示不限。</summary>
    public int MaxTriggerCount { get; set; } = 1;

    /// <summary>已触发次数。</summary>
    public int TriggeredCount { get; set; }

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>标签列表，用于分组筛选。</summary>
    public List<string> Tags { get; set; } = [];
}
