using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Tools;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆读取类工具核心处理器。
/// </summary>
public sealed class MemoryReadToolHandler(
    IMemoryRecallService recallService,
    IMemorySupplyService supplyService,
    IMemoryStatusService statusService,
    ILogger<MemoryReadToolHandler> logger) : IMemoryReadToolHandler
{
    private const string DefaultAgentId = "default";
    private const int MaximumRecallMemoryCount = 50;
    private const int MaximumSupplyMemoryCount = 50;

    /// <inheritdoc />
    public string Recall(string queryText, string queryIntent, string workspaceId, int maxMemoryCount)
    {
        if (string.IsNullOrWhiteSpace(queryText))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "查询文本不能为空。");

        try
        {
            var result = recallService.Recall(new MemoryRecallRequest
            {
                AgentId = DefaultAgentId,
                WorkspaceId = NormalizeOptional(workspaceId),
                QueryText = queryText,
                QueryIntent = NormalizeOptional(queryIntent),
                TriggerSource = "memory_recall_tool",
                TraceId = Guid.NewGuid().ToString("N")
            });

            result = LimitRecallResult(result, maxMemoryCount);
            return MemoryToolResult.Ok("记忆召回成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryRecallResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "记忆召回工具调用失败。Query={QueryText}", queryText);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆召回工具调用异常。Query={QueryText}", queryText);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"记忆召回失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string SupplyContext(string scenario, string currentTask, string recentMessages, string workspaceId, int maxMemoryCount, int maxTokenBudget, string triggerSource)
    {
        try
        {
            var result = supplyService.Supply(new MemorySupplyRequest
            {
                AgentId = DefaultAgentId,
                WorkspaceId = NormalizeOptional(workspaceId),
                Scenario = NormalizeOptional(scenario),
                CurrentTask = NormalizeOptional(currentTask),
                RecentMessages = SplitMessages(recentMessages),
                MaxMemoryCount = maxMemoryCount <= 0 ? null : Math.Min(maxMemoryCount, MaximumSupplyMemoryCount),
                MaxTokenBudget = maxTokenBudget <= 0 ? null : maxTokenBudget,
                TriggerSource = NormalizeOptional(triggerSource) ?? "memory_supply_context_tool",
                TraceId = Guid.NewGuid().ToString("N")
            });

            return MemoryToolResult.Ok("记忆上下文供应成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemorySupplyResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "记忆上下文供应工具调用失败。Task={CurrentTask}", currentTask);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆上下文供应工具调用异常。Task={CurrentTask}", currentTask);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"记忆上下文供应失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string GetStatus(string workspaceId)
    {
        try
        {
            var result = statusService.GetStatus(new MemoryStatusRequest
            {
                AgentId = DefaultAgentId,
                WorkspaceId = NormalizeOptional(workspaceId)
            });

            return MemoryToolResult.Ok("记忆系统状态读取成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryStatusResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "记忆系统状态工具调用失败。Workspace={WorkspaceId}", workspaceId);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "记忆系统状态工具调用异常。Workspace={WorkspaceId}", workspaceId);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"记忆系统状态读取失败: {ex.Message}");
        }
    }

    private static MemoryRecallResult LimitRecallResult(MemoryRecallResult result, int maxMemoryCount)
    {
        var limit = maxMemoryCount <= 0 ? MaximumRecallMemoryCount : Math.Min(maxMemoryCount, MaximumRecallMemoryCount);
        var items = result.Items.Take(limit).ToList();
        var itemIds = items.Select(static item => item.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var windows = result.Windows
            .Select(window => new MemoryRecallWindow
            {
                Topic = window.Topic,
                Title = window.Title,
                Items = window.Items.Where(item => itemIds.Contains(item.Id)).ToList()
            })
            .Where(static window => window.Items.Count > 0)
            .ToList();

        return new MemoryRecallResult
        {
            RequestId = result.RequestId,
            Windows = windows,
            Items = items,
            Confidence = result.Confidence,
            Summary = result.Summary
        };
    }

    private static IReadOnlyList<string> SplitMessages(string? recentMessages)
    {
        if (string.IsNullOrWhiteSpace(recentMessages)) return [];

        return recentMessages
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static message => !string.IsNullOrWhiteSpace(message))
            .TakeLast(20)
            .ToList();
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
