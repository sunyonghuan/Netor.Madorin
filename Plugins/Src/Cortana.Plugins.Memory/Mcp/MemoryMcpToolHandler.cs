using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Services;
using Cortana.Plugins.Memory.Tools;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Memory.Mcp;

/// <summary>
/// MCP 独立模式工具处理器实现。
/// </summary>
public sealed class MemoryMcpToolHandler(
    IMemoryObservationWriter observationWriter,
    IMemoryRuntimeContext runtimeContext,
    ILogger<MemoryMcpToolHandler> logger) : IMemoryMcpToolHandler
{
    /// <inheritdoc />
    public string RecordTurn(string role, string content, string agentId, string workspaceId, string sessionId, string turnId, string messageId, string source, long createdTimestamp)
    {
        try
        {
            var result = observationWriter.RecordTurn(new MemoryRecordTurnRequest
            {
                Role = role,
                Content = content,
                AgentId = NormalizeOptional(agentId),
                WorkspaceId = NormalizeOptional(workspaceId),
                SessionId = NormalizeOptional(sessionId),
                TurnId = NormalizeOptional(turnId),
                MessageId = NormalizeOptional(messageId),
                Source = NormalizeOptional(source),
                CreatedTimestamp = createdTimestamp <= 0 ? null : createdTimestamp
            });

            return MemoryToolResult.Ok("对话轮次记录成功。",
                JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryRecordTurnResult));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            logger.LogWarning(ex, "MCP 对话轮次记录失败。Role={Role}", role);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "MCP 对话轮次记录异常。Role={Role}", role);
            return MemoryToolResult.Fail(MemoryToolErrorCodes.InternalError, $"对话轮次记录失败: {ex.Message}");
        }
    }

    /// <inheritdoc />
    public string SetScope(string agentId, string workspaceId, string source)
    {
        runtimeContext.SetScope(NormalizeOptional(agentId), NormalizeOptional(workspaceId), NormalizeOptional(source));
        return GetScope();
    }

    /// <inheritdoc />
    public string GetScope()
    {
        var scope = runtimeContext.GetScope();
        var result = new MemoryScopeResult
        {
            AgentId = scope.AgentId,
            WorkspaceId = scope.WorkspaceId,
            Source = scope.Source
        };

        return MemoryToolResult.Ok("当前 MCP 记忆作用域读取成功。",
            JsonSerializer.Serialize(result, MemoryToolJsonContext.Default.MemoryScopeResult));
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
