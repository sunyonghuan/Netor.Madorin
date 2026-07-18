using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

using System.Text;

namespace Netor.Cortana.AI;

/// <summary>
/// 为图片/视频生成构造带智能体与最近会话上下文的提示词。
/// </summary>
public sealed class ChatGenerationContextBuilder(
    CortanaDbContext dbContext,
    IAppPaths appPaths,
    ILogger<ChatGenerationContextBuilder> logger)
{
    public string BuildPrompt(string userPrompt, string sessionId, string mediaKind, AgentEntity agentEntity)
    {
        var prompt = (userPrompt ?? string.Empty).Trim();
        var builder = new StringBuilder();

        builder.AppendLine($"你正在为 Cortana 的「{agentEntity.Name ?? "当前智能体"}」执行{mediaKind}生成任务。");
        builder.AppendLine("请严格延续当前会话已经确定的设计方向、主题、主体、构图、风格和修改要求。");
        builder.AppendLine("如果用户本轮是在要求修改上一版结果，不要重新发明主题；应基于最近一次相关结果进行局部修正。");

        AppendLimitedSection(builder, "智能体描述", agentEntity.Description, 900);
        AppendLimitedSection(builder, "智能体指令", agentEntity.Instructions, 1800);

        var recentMessages = LoadRecentGenerationContextMessages(sessionId, maxMessages: 10);
        if (recentMessages.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 最近对话上下文（从旧到新）");
            foreach (var message in recentMessages)
            {
                var role = string.Equals(message.Role, "user", StringComparison.OrdinalIgnoreCase)
                    ? "用户"
                    : string.IsNullOrWhiteSpace(message.AuthorName) ? message.Role : message.AuthorName;
                builder.AppendLine($"- {role}: {TruncateForPrompt(CleanHistoryContent(message.Content), 700)}");
            }
        }

        var recentAssets = LoadRecentGeneratedAssets(sessionId, maxAssets: 4);
        if (recentAssets.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## 最近生成资源");
            foreach (var asset in recentAssets)
            {
                var absolutePath = Path.Combine(appPaths.WorkspaceResourcesDirectory, asset.RelativePath);
                builder.AppendLine($"- {asset.AssetGroup}: {asset.OriginalName} ({absolutePath})");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## 本轮用户要求");
        builder.AppendLine(prompt);
        builder.AppendLine();
        builder.AppendLine("## 生成要求");
        builder.AppendLine("- 优先满足本轮用户要求，同时保持最近上下文中的主体和设计意图。");
        builder.AppendLine("- 对于“太复杂、线条太多、比例不正、颜色不对”等反馈，只修正反馈点，不改变原始题材。");
        builder.AppendLine("- 输出应直接用于视觉生成模型，避免解释性文字、水印、界面元素或多余文本。");

        return TruncateForPrompt(builder.ToString(), 6000);
    }

    private List<ChatMessageEntity> LoadRecentGenerationContextMessages(string sessionId, int maxMessages)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        try
        {
            var messages = dbContext.Query(
                """
                SELECT * FROM (
                    SELECT * FROM ChatMessages
                    WHERE SessionId = @SessionId
                      AND Role <> 'tool'
                      AND Content NOT LIKE '[工具调用]%'
                      AND Content NOT LIKE '[工具结果]%'
                    ORDER BY CreatedTimestamp DESC, rowid DESC
                    LIMIT @Limit
                ) ORDER BY CreatedTimestamp ASC
                """,
                ChatMessageService.ReadEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@SessionId", sessionId);
                    cmd.Parameters.AddWithValue("@Limit", maxMessages);
                });

            return messages
                .Where(static message => !string.IsNullOrWhiteSpace(message.Content))
                .ToList();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载生成上下文历史失败：{SessionId}", sessionId);
            return [];
        }
    }

    private List<ChatMessageAssetEntity> LoadRecentGeneratedAssets(string sessionId, int maxAssets)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return [];
        }

        try
        {
            return dbContext.Query(
                """
                SELECT * FROM ChatMessageAssets
                WHERE SessionId = @SessionId
                  AND Status = 'active'
                  AND AssetKind = 'generated'
                  AND (AssetGroup = 'images' OR AssetGroup = 'video')
                ORDER BY CreatedTimestamp DESC, SortOrder DESC
                LIMIT @Limit
                """,
                ChatMessageAssetService.ReadEntity,
                cmd =>
                {
                    cmd.Parameters.AddWithValue("@SessionId", sessionId);
                    cmd.Parameters.AddWithValue("@Limit", maxAssets);
                });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "加载生成资源上下文失败：{SessionId}", sessionId);
            return [];
        }
    }

    private static void AppendLimitedSection(StringBuilder builder, string title, string content, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine($"## {title}");
        builder.AppendLine(TruncateForPrompt(content.Trim(), maxLength));
    }

    private static string CleanHistoryContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return string.Empty;
        }

        return content
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
    }

    private static string TruncateForPrompt(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "\n...[已截断]";
    }
}
