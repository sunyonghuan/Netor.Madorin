using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.EventHub;

using System.Text;

namespace Netor.Cortana.AI.Providers;

internal sealed class ProviderToolLimitContextProvider(
    string providerDisplayName,
    string modelName,
    int maxTools,
    IPublisher? publisher,
    ILogger? logger) : AIContextProvider
{
    private bool _noticePublished;

    protected override ValueTask<AIContext> InvokingCoreAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default)
    {
        var result = ApplyLimit(context.AIContext.Tools, maxTools);
        if (!result.WasLimited)
        {
            return ValueTask.FromResult(context.AIContext);
        }

        context.AIContext.Tools = result.Tools;
        var message = BuildLimitMessage(providerDisplayName, modelName, result);
        context.AIContext.Instructions = AppendInstructions(context.AIContext.Instructions, message);
        PublishNoticeOnce(message, result);

        return ValueTask.FromResult(context.AIContext);
    }

    internal static ProviderToolLimitResult ApplyLimit(IEnumerable<AITool>? tools, int maxTools)
    {
        var toolList = tools?.ToList() ?? [];
        if (maxTools <= 0 || toolList.Count <= maxTools)
        {
            return new ProviderToolLimitResult(toolList, [], toolList.Count, maxTools);
        }

        var kept = toolList.Take(maxTools).ToArray();
        var removed = toolList.Skip(maxTools).Select(static tool => tool.Name).ToArray();
        return new ProviderToolLimitResult(kept, removed, toolList.Count, maxTools);
    }

    private static string AppendInstructions(string? currentInstructions, string message)
    {
        if (string.IsNullOrWhiteSpace(currentInstructions))
        {
            return message;
        }

        return $"{currentInstructions.TrimEnd()}{Environment.NewLine}{Environment.NewLine}{message}";
    }

    private static string BuildLimitMessage(
        string providerDisplayName,
        string modelName,
        ProviderToolLimitResult result)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[系统提示] 当前模型工具数量已被自动限制。");
        builder.AppendLine($"提供商/模型：{providerDisplayName} / {modelName}");
        builder.AppendLine($"模型当前工具上限：{result.MaxTools}；本轮原始工具数：{result.OriginalToolCount}；实际发送工具数：{result.Tools.Count}。");
        builder.AppendLine("为避免模型接口拒绝请求，超出上限的工具已在本轮临时停用。请在回复用户时说明这个限制，并建议用户禁用不需要的插件/MCP，或在工作模式中只挂载本任务需要的工具。");
        builder.Append("如该模型后续放开限制，可将系统设置 AI.Provider.Kimi.MaxTools 或环境变量 CORTANA_KIMI_MAX_TOOLS 设置为 0 来关闭限制。");

        var removedPreview = result.RemovedToolNames
            .Where(static name => !string.IsNullOrWhiteSpace(name))
            .Take(20)
            .ToArray();
        if (removedPreview.Length > 0)
        {
            builder.AppendLine();
            builder.Append("本轮被临时停用的部分工具：");
            builder.Append(string.Join(", ", removedPreview));
            if (result.RemovedToolNames.Count > removedPreview.Length)
            {
                builder.Append($" 等 {result.RemovedToolNames.Count} 个。");
            }
        }

        return builder.ToString();
    }

    private void PublishNoticeOnce(string message, ProviderToolLimitResult result)
    {
        if (_noticePublished || publisher is null)
        {
            return;
        }

        _noticePublished = true;
        try
        {
            publisher.Publish(Events.OnSystemNotice, new SystemNoticeArgs(
                message,
                "AI 工具数量已自动限制",
                "warning",
                providerDisplayName,
                DateTimeOffset.UtcNow));
        }
        catch (Exception ex)
        {
            logger?.LogDebug(ex, "发布 AI 工具数量限制系统提醒失败。");
        }

        logger?.LogWarning(
            "Provider tool count limited: Provider={Provider}, Model={Model}, Original={Original}, Max={Max}, Removed={Removed}",
            providerDisplayName,
            modelName,
            result.OriginalToolCount,
            result.MaxTools,
            result.RemovedToolNames.Count);
    }
}

internal sealed record ProviderToolLimitResult(
    IReadOnlyList<AITool> Tools,
    IReadOnlyList<string?> RemovedToolNames,
    int OriginalToolCount,
    int MaxTools)
{
    public bool WasLimited => OriginalToolCount > MaxTools && MaxTools > 0;
}
