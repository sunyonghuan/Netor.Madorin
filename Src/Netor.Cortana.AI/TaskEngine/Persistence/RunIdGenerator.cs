using System.Globalization;

namespace Netor.Cortana.AI.TaskEngine.Persistence;

/// <summary>
/// 执行运行 ID 生成器。
/// 格式：run-{yyyyMMdd}-{HHmmss}-{4位随机hex}
/// 保证同一任务的多次执行完全隔离。
/// 详见 docs/未来版本策划/聊天式任务发起与动态智能体/05-P4补充-上下文传递与恢复机制.md §4.8。
/// </summary>
public static class RunIdGenerator
{
    /// <summary>生成新的 Run ID。</summary>
    public static string Generate()
    {
        var now = DateTimeOffset.Now;
        var random = Random.Shared.Next(0, 0xFFFF).ToString("x4");
        return $"run-{now:yyyyMMdd}-{now:HHmmss}-{random}";
    }

    /// <summary>
    /// 从 Run ID 中解析启动时间（用于排序和清理）。
    /// </summary>
    /// <param name="runId">格式为 run-yyyyMMdd-HHmmss-xxxx 的 Run ID。</param>
    /// <returns>解析成功返回时间，否则 null。</returns>
    public static DateTimeOffset? ParseStartTime(string runId)
    {
        // run-20260524-011500-a3f2 → 2026-05-24 01:15:00
        if (string.IsNullOrEmpty(runId) || runId.Length < 20 || !runId.StartsWith("run-", StringComparison.Ordinal))
            return null;

        var datePart = runId.Substring(4, 8);   // 20260524
        var timePart = runId.Substring(13, 6);  // 011500

        if (DateTime.TryParseExact(
                datePart + timePart,
                "yyyyMMddHHmmss",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var dt))
        {
            return new DateTimeOffset(dt, TimeZoneInfo.Local.GetUtcOffset(dt));
        }

        return null;
    }
}
