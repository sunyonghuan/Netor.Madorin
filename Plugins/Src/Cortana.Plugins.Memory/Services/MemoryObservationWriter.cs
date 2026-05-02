using System.Text.Json;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Serialization;
using Cortana.Plugins.Memory.Storage;

namespace Cortana.Plugins.Memory.Services;

/// <summary>
/// 将 MCP 或其他外部来源提供的对话消息写入 observation_records。
/// </summary>
public sealed class MemoryObservationWriter(IMemoryStore store, IMemoryRuntimeContext runtimeContext) : IMemoryObservationWriter
{
    /// <inheritdoc />
    public MemoryRecordTurnResult RecordTurn(MemoryRecordTurnRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Role)) throw new ArgumentException("消息角色不能为空。", nameof(request));
        if (string.IsNullOrWhiteSpace(request.Content)) throw new ArgumentException("消息内容不能为空。", nameof(request));

        store.EnsureInitialized();

        var agentId = runtimeContext.ResolveAgentId(request.AgentId);
        var workspaceId = runtimeContext.ResolveWorkspaceId(request.WorkspaceId);
        var source = runtimeContext.ResolveSource(request.Source);
        var timestamp = request.CreatedTimestamp is > 0
            ? request.CreatedTimestamp.Value
            : DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var observationId = Guid.NewGuid().ToString("N");
        var sessionId = NormalizeOptional(request.SessionId) ?? $"mcp-{DateTimeOffset.UtcNow:yyyyMMdd}";
        var turnId = NormalizeOptional(request.TurnId) ?? Guid.NewGuid().ToString("N");
        var messageId = NormalizeOptional(request.MessageId) ?? Guid.NewGuid().ToString("N");
        var role = request.Role.Trim();

        var facts = new McpObservationSourceFacts
        {
            Source = source,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            TurnId = turnId,
            MessageId = messageId,
            Role = role,
            Content = request.Content,
            CreatedTimestamp = timestamp
        };

        store.InsertObservation(new ObservationRecord
        {
            Id = observationId,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            TurnId = turnId,
            MessageId = messageId,
            EventType = "mcp.message.recorded",
            Role = role,
            Content = request.Content,
            AttachmentsJson = "[]",
            CreatedTimestamp = timestamp,
            TraceId = Guid.NewGuid().ToString("N"),
            SourceFactsJson = JsonSerializer.Serialize(facts, MemoryInternalJsonContext.Default.McpObservationSourceFacts),
            CreatedAt = DateTimeOffset.FromUnixTimeMilliseconds(timestamp).UtcDateTime.ToString("O")
        });

        return new MemoryRecordTurnResult
        {
            ObservationId = observationId,
            AgentId = agentId,
            WorkspaceId = workspaceId,
            SessionId = sessionId,
            Role = role,
            CreatedTimestamp = timestamp
        };
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
