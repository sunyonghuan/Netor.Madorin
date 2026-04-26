using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Tools;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.ToolHandlers;

/// <summary>
/// 记忆写入和透明化查看工具核心处理器。
/// </summary>
public sealed class MemoryWriteToolHandler(
    IMemoryNoteService noteService,
    IMemoryRecentService recentService,
    ILogger<MemoryWriteToolHandler> logger) : IMemoryWriteToolHandler
{
    private const string DefaultAgentId = "default";
    private const int MaximumRecentMemoryCount = 50;

    /// <inheritdoc />
    public string AddNote(string content, string memoryType, string topic, string reason, string workspaceId, bool userConfirmed)
    {
        if (string.IsNullOrWhiteSpace(content))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "记忆内容不能为空。");
        if (string.IsNullOrWhiteSpace(reason))
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "写入原因不能为空。");
        if (!userConfirmed)
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, "写入人工记忆前必须获得用户明确授权。");

        try
        {
            var result = noteService.AddNote(new MemoryAddNoteRequest
            {
                AgentId = DefaultAgentId,
                WorkspaceId = NormalizeOptional(workspaceId),
                Content = content,
                MemoryType = memoryType,
                Topic = NormalizeOptional(topic),
                Reason = reason,
                UserConfirmed = userConfirmed,
                TriggerSource = "memory_add_note_tool",
                TraceId = Guid.NewGuid().ToString("N")
            });

            return MemoryToolResult.Ok("人工记忆写入成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryAddNoteResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "人工记忆写入工具调用失败。Type={MemoryType}", memoryType);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "人工记忆写入工具调用异常。Type={MemoryType}", memoryType);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"人工记忆写入失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string ListRecent(int limit, string kind, string workspaceId)
    {
        try
        {
            var result = recentService.ListRecent(new MemoryListRecentRequest
            {
                AgentId = DefaultAgentId,
                WorkspaceId = NormalizeOptional(workspaceId),
                Limit = limit <= 0 ? null : Math.Min(limit, MaximumRecentMemoryCount),
                Kind = NormalizeOptional(kind)
            });

            return MemoryToolResult.Ok("最近记忆读取成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryListRecentResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "最近记忆列表工具调用失败。Kind={Kind}", kind);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "最近记忆列表工具调用异常。Kind={Kind}", kind);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"最近记忆读取失败: {ex.Message}");
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
