using System.Text.Json;
using Netor.Cortana.Plugin;

namespace ReminderPlugin;

[Tool]
public class ReminderTools(ReminderStore store)
{
    [Tool(Name = "get_current_time",
        Description = "获取系统当前时间。创建或修改提醒前必须先调用此工具获取准确的当前时间，禁止自行猜测时间。")]
    public string GetCurrentTime()
    {
        var now = DateTimeOffset.Now;
        return $"当前系统时间: {now:yyyy-MM-dd HH:mm:ss} (时区: {now:zzz}, 星期{"日一二三四五六"[(int)now.DayOfWeek]})";
    }

    [Tool(Name = "create_reminder",
        Description = "创建一个定时提醒。到期后 Cortana 会自动收到通知。支持单次和循环提醒。调用前必须先调用 get_current_time 获取当前时间。")]
    public string CreateReminder(
        [Parameter(Description = "提醒标题")] string title,
        [Parameter(Description = "提醒内容")] string message,
        [Parameter(Description = "触发时间，ISO 8601 格式，例如 2025-04-12T09:00:00+08:00")] string triggerTime,
        [Parameter(Description = "重复类型：once(单次) / daily(每天) / weekly(每周) / monthly(每月) / custom(自定义间隔)，默认 once")] string repeatType,
        [Parameter(Description = "自定义重复间隔（分钟），仅 repeatType=custom 时需要，默认 0")] int repeatIntervalMinutes,
        [Parameter(Description = "最大触发次数，0 表示无限重复（仅限 daily/weekly/monthly/custom），默认 1")] int maxTriggerCount,
        [Parameter(Description = "标签列表，用逗号分隔，例如 '工作,会议'，空字符串表示无标签")] string tags)
    {
        if (!DateTimeOffset.TryParse(triggerTime, out var dt))
            return "错误：triggerTime 格式无效，请使用 ISO 8601 格式。";

        if (string.IsNullOrWhiteSpace(repeatType)) repeatType = "once";
        repeatType = repeatType.ToLowerInvariant();

        string[] validTypes = ["once", "daily", "weekly", "monthly", "custom"];
        if (!validTypes.Contains(repeatType))
            return $"错误：repeatType 无效，仅支持 {string.Join("/", validTypes)}。";

        if (repeatType == "custom" && repeatIntervalMinutes <= 0)
            return "错误：repeatType=custom 时 repeatIntervalMinutes 必须大于 0。";

        if (dt <= DateTimeOffset.Now)
            return $"警告：触发时间 {dt:yyyy-MM-dd HH:mm:ss} 已过去，请使用 get_current_time 获取当前时间后重新设置一个未来的时间。";

        if (repeatType == "once") maxTriggerCount = 1;
        if (maxTriggerCount <= 0 && repeatType == "once") maxTriggerCount = 1;

        var item = new ReminderItem
        {
            Id = Guid.NewGuid().ToString("N"),
            Title = title,
            Message = message,
            TriggerTime = dt,
            RepeatType = repeatType,
            RepeatIntervalMinutes = repeatIntervalMinutes,
            MaxTriggerCount = maxTriggerCount,
            TriggeredCount = 0,
            IsEnabled = true,
            CreatedAt = DateTimeOffset.Now,
            Tags = string.IsNullOrWhiteSpace(tags) ? [] : [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]
        };

        store.Add(item);
        var tagInfo = item.Tags.Count > 0 ? $"，标签=[{string.Join(", ", item.Tags)}]" : "";
        return $"已创建提醒 [{item.Title}]，ID={item.Id}，触发时间={item.TriggerTime:yyyy-MM-dd HH:mm:ss}，重复={item.RepeatType}，最大触发={item.MaxTriggerCount}次{tagInfo}。";
    }

    [Tool(Name = "list_reminders", Description = "列出所有提醒，包含 ID、标题、下次触发时间、重复类型、标签和状态。支持按标签筛选。")]
    public string ListReminders(
        [Parameter(Description = "按标签筛选，空字符串表示列出所有")] string tag)
    {
        var all = string.IsNullOrWhiteSpace(tag) ? store.GetAll() : store.GetByTag(tag);
        if (all.Count == 0) return string.IsNullOrWhiteSpace(tag) ? "当前没有任何提醒。" : $"没有标签为 [{tag}] 的提醒。";

        var lines = all.Select(r =>
        {
            var tagStr = r.Tags.Count > 0 ? $" | 标签: {string.Join(", ", r.Tags)}" : "";
            return $"- [{r.Id}] {r.Title} | 下次触发: {r.TriggerTime:yyyy-MM-dd HH:mm:ss} | " +
                $"重复: {r.RepeatType} | 已触发: {r.TriggeredCount}/{r.MaxTriggerCount} | " +
                $"启用: {r.IsEnabled}{tagStr}";
        });
        return string.Join("\n", lines);
    }

    [Tool(Name = "update_reminder", Description = "更新指定提醒的属性。只传入需要修改的字段，其余保持不变。")]
    public string UpdateReminder(
        [Parameter(Description = "提醒 ID")] string id,
        [Parameter(Description = "新标题，空字符串表示不修改")] string title,
        [Parameter(Description = "新内容，空字符串表示不修改")] string message,
        [Parameter(Description = "新触发时间(ISO 8601)，空字符串表示不修改")] string triggerTime,
        [Parameter(Description = "新重复类型，空字符串表示不修改")] string repeatType,
        [Parameter(Description = "是否启用，1=启用 0=禁用 -1=不修改")] int isEnabled,
        [Parameter(Description = "新标签列表（逗号分隔），空字符串表示不修改，传入 'clear' 清除所有标签")] string tags)
    {
        var item = store.GetById(id);
        if (item is null) return $"错误：未找到 ID={id} 的提醒。";

        if (!string.IsNullOrWhiteSpace(title)) item.Title = title;
        if (!string.IsNullOrWhiteSpace(message)) item.Message = message;
        if (!string.IsNullOrWhiteSpace(triggerTime))
        {
            if (!DateTimeOffset.TryParse(triggerTime, out var dt))
                return "错误：triggerTime 格式无效。";
            item.TriggerTime = dt;
        }
        if (!string.IsNullOrWhiteSpace(repeatType)) item.RepeatType = repeatType.ToLowerInvariant();
        if (isEnabled >= 0) item.IsEnabled = isEnabled == 1;
        if (!string.IsNullOrWhiteSpace(tags))
        {
            item.Tags = tags.Equals("clear", StringComparison.OrdinalIgnoreCase)
                ? []
                : [.. tags.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        }

        store.Update(item);
        return $"已更新提醒 [{item.Title}] (ID={item.Id})。";
    }

    [Tool(Name = "delete_reminder", Description = "根据 ID 删除指定提醒。")]
    public string DeleteReminder(
        [Parameter(Description = "提醒 ID")] string id)
    {
        return store.Delete(id) ? $"已删除提醒 ID={id}。" : $"错误：未找到 ID={id} 的提醒。";
    }

    [Tool(Name = "search_reminders", Description = "按关键词搜索提醒。匹配标题、内容和标签。")]
    public string SearchReminders(
        [Parameter(Description = "搜索关键词")] string keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return "错误：请提供搜索关键词。";

        var results = store.Search(keyword);
        if (results.Count == 0) return $"没有找到包含 [{keyword}] 的提醒。";

        var lines = results.Select(r =>
        {
            var tagStr = r.Tags.Count > 0 ? $" | 标签: {string.Join(", ", r.Tags)}" : "";
            return $"- [{r.Id}] {r.Title} | 下次触发: {r.TriggerTime:yyyy-MM-dd HH:mm:ss} | " +
                $"重复: {r.RepeatType} | 启用: {r.IsEnabled}{tagStr}";
        });
        return $"找到 {results.Count} 条匹配结果：\n{string.Join("\n", lines)}";
    }

    [Tool(Name = "delete_reminders_by_tag", Description = "批量删除指定标签下的所有提醒。")]
    public string DeleteRemindersByTag(
        [Parameter(Description = "要删除的标签名")] string tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
            return "错误：请提供标签名。";

        var count = store.DeleteByTag(tag);
        return count > 0 ? $"已删除标签 [{tag}] 下的 {count} 条提醒。" : $"没有找到标签为 [{tag}] 的提醒。";
    }
}
