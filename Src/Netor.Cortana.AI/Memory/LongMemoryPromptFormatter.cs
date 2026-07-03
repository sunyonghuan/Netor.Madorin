using Netor.Cortana.Entitys.Memory;

namespace Netor.Cortana.AI.Memory;

/// <summary>
/// 将长期记忆供应包格式化为可注入 AI 上下文的提示词片段。
/// </summary>
public static class LongMemoryPromptFormatter
{
    private const int MaxItemLength = 500;
    private const int MaxTotalLength = 4_000;

    private static readonly Dictionary<string, string> GroupTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["abstraction"] = "用户画像与长期认知",
        ["constraint"] = "项目约束",
        ["preference"] = "用户偏好",
        ["fact"] = "历史事实",
        ["task"] = "历史任务",
        ["other"] = "其他记忆"
    };

    /// <summary>
    /// 格式化长期记忆供应包。分层展示：用户画像在前，具体记忆在后。
    /// </summary>
    public static string Format(MemoryContextSupplyPackage package, double minimumConfidence)
    {
        ArgumentNullException.ThrowIfNull(package);

        if (!package.Enabled || package.Confidence <= 0 || package.Items.Count == 0)
        {
            return string.Empty;
        }

        var groups = package.Groups
            .OrderBy(static group => group.Priority)
            .Select(group => new
            {
                group.GroupKey,
                Title = ResolveGroupTitle(group.GroupKey, group.Title),
                Items = group.Items
                    .Where(item => !string.IsNullOrWhiteSpace(item.Content) && item.Confidence >= minimumConfidence)
                    .GroupBy(static item => item.Content.Trim(), StringComparer.OrdinalIgnoreCase)
                    .Select(static groupItems => groupItems.OrderByDescending(item => item.Score).First())
                    .OrderByDescending(static item => item.Score)
                    .ToList()
            })
            .Where(static group => group.Items.Count > 0)
            .ToList();

        if (groups.Count == 0)
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder();
        builder.AppendLine("--long-term-memory--");

        // 分区 1：用户画像（来自 abstractions，始终优先展示）
        var profileGroups = groups.Where(static g => string.Equals(g.GroupKey, "abstraction", StringComparison.OrdinalIgnoreCase)).ToList();
        if (profileGroups.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 关于用户的长期认知");
            builder.AppendLine("以下是对用户的持久性认知总结，始终作为背景参考。");

            foreach (var group in profileGroups)
            {
                foreach (var item in group.Items)
                {
                    if (builder.Length >= MaxTotalLength) break;
                    var content = NormalizeContent(item.Content);
                    if (content.Length == 0) continue;
                    builder.Append("- ").AppendLine(content);
                }
            }
        }

        // 分区 2：本次对话相关记忆（来自 fragments）
        var contextGroups = groups.Where(static g => !string.Equals(g.GroupKey, "abstraction", StringComparison.OrdinalIgnoreCase)).ToList();
        if (contextGroups.Count > 0 && builder.Length < MaxTotalLength)
        {
            builder.AppendLine();
            builder.AppendLine("## 本次对话相关记忆");
            builder.AppendLine("以下是与当前任务相关的具体记忆，仅在有帮助时参考；如与用户当前明确指令冲突，以当前指令为准。");

            foreach (var group in contextGroups)
            {
                if (builder.Length >= MaxTotalLength) break;

                builder.AppendLine();
                builder.Append('[').Append(group.Title).AppendLine("]");

                foreach (var item in group.Items)
                {
                    if (builder.Length >= MaxTotalLength) break;
                    var content = NormalizeContent(item.Content);
                    if (content.Length == 0) continue;
                    builder.Append("- ").AppendLine(content);
                }
            }
        }

        builder.AppendLine();
        builder.Append("> 以上内容来自长期记忆系统，可能不完整或过期。需要详细信息请使用记忆工具查询。");

        var text = builder.ToString();
        return text.Length <= MaxTotalLength ? text : text[..MaxTotalLength];
    }

    private static string ResolveGroupTitle(string groupKey, string title)
    {
        if (GroupTitles.TryGetValue(groupKey, out var mapped))
        {
            return mapped;
        }

        return string.IsNullOrWhiteSpace(title) ? "相关记忆" : title.Trim();
    }

    private static string NormalizeContent(string content)
    {
        var normalized = content.Trim().Replace("\r", string.Empty).Replace("\n", " ");
        return normalized.Length <= MaxItemLength ? normalized : normalized[..MaxItemLength];
    }
}
