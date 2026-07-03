using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.AI;
using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus conversation 历史回放请求，负责分页查询、hasMore 计算和响应组包。
/// </summary>
internal sealed class PluginBusConversationHistoryDispatcher(
    CortanaDbContext db,
    ILogger logger,
    Func<string, string, CancellationToken, Task> sendAsync)
{
    /// <summary>
    /// 按时间戳分页回放会话消息，并以 conversation.history.batch/completed 响应发送给指定客户端。
    /// </summary>
    /// <param name="clientId">接收回放数据的客户端标识。</param>
    /// <param name="requestId">原始回放请求标识。</param>
    /// <param name="sinceTimestamp">回放起始时间戳。</param>
    /// <param name="batchSize">每批最多回放的消息数量。</param>
    /// <param name="cancellationToken">取消查询或发送的令牌。</param>
    public async Task ReplayAsync(string clientId, string? requestId, long sinceTimestamp, int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            var last = sinceTimestamp;
            while (true)
            {
                var queryLimit = batchSize + 1;
                var sessionPredicate = ChatSessionService.BuildVisibleExpertSessionPredicate("s");
                var rows = db.Query(
                    $"""
                    SELECT
                        m.Id,
                        m.SessionId,
                        m.Role,
                        m.Content,
                        m.CreatedTimestamp,
                        m.ModelName,
                        m.AgentId AS MessageAgentId,
                        m.AgentName AS MessageAgentName,
                        s.Categorize AS WorkspaceId,
                        s.AgentName AS SessionAgentId,
                        s.RawDiscription
                    FROM ChatMessages m
                    INNER JOIN ChatSessions s ON s.Id = m.SessionId
                    WHERE m.CreatedTimestamp >= @Since
                      AND {sessionPredicate}
                    ORDER BY m.CreatedTimestamp
                    LIMIT {queryLimit}
                    """,
                    ConversationExportRecordMapper.Read,
                    cmd => cmd.Parameters.AddWithValue("@Since", last));

                if (rows.Count == 0)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var hasMore = rows.Count > batchSize;
                var items = hasMore ? rows.Take(batchSize).ToArray() : rows.ToArray();
                total += items.Length;
                last = items[^1].CreatedTimestamp + 1;

                await SendBatchAsync(clientId, requestId, hasMore, items, cancellationToken).ConfigureAwait(false);
                if (!hasMore)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            logger.LogInformation("Conversation replay 完成：Since={Since}, Total={Total}", sinceTimestamp, total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Conversation replay 失败");
            await sendAsync(clientId, PluginBusMessageFactory.CreateControlError(clientId, $"replay failed: {ex.Message}"), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendBatchAsync(string clientId, string? requestId, bool hasMore, ConversationExportRecord[] items, CancellationToken cancellationToken)
    {
        var batch = new ConversationExportBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            HasMore = hasMore,
            Items = items
        };
        var payload = JsonSerializer.SerializeToElement(batch, WebSocketJsonContext.Default.ConversationExportBatch);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.ConversationTopic,
            Op = CortanaWsEndpoints.ConversationHistoryBatchOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.ConversationHistoryBatchOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }

    private Task SendCompletedAsync(string clientId, string? requestId, int total, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(new ConversationHistoryCompletedPayload { Total = total }, WebSocketJsonContext.Default.ConversationHistoryCompletedPayload);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.ConversationTopic,
            Op = CortanaWsEndpoints.ConversationHistoryCompletedOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.ConversationHistoryCompletedOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }
}

/// <summary>
/// 历史回放完成响应载荷。
/// </summary>
internal sealed record ConversationHistoryCompletedPayload
{
    [System.Text.Json.Serialization.JsonPropertyName("total")]
    public int Total { get; init; }
}
