using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus meeting 历史回放请求，按会议级粒度分页查询、组包响应。
/// </summary>
internal sealed class PluginBusMeetingHistoryDispatcher(
    CortanaDbContext db,
    ILogger logger,
    Func<string, string, CancellationToken, Task> sendAsync)
{
    /// <summary>
    /// 按时间戳分页回放已完成会议，并以 meeting.history.batch/completed 响应发送给指定客户端。
    /// </summary>
    public async Task ReplayAsync(string clientId, string? requestId, long sinceTimestamp, int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            var lastTimestamp = sinceTimestamp;
            var lastCursorId = string.Empty;
            while (true)
            {
                var queryLimit = batchSize + 1;
                var rows = db.Query(
                    $"""
                    SELECT *
                    FROM (
                        SELECT
                            'final:' || Id AS CursorId,
                            'final' AS RecordKind,
                            Id,
                            NULL AS MessageId,
                            SessionId,
                            WorkspaceId,
                            Topic,
                            Status,
                            HostAgentId,
                            ParticipantsJson,
                            NULL AS ContentMd,
                            NULL AS MessageRole,
                            FinalSummaryMd,
                            Provider,
                            Model,
                            CreatedAt,
                            UpdatedAt,
                            EndedAt
                        FROM MeetingSessions
                        WHERE Status = 2
                          AND IFNULL(FinalSummaryMd, '') <> ''
                          AND UpdatedAt >= @Since
                        UNION ALL
                        SELECT
                            'summary:' || mm.Id AS CursorId,
                            'summary' AS RecordKind,
                            ms.Id AS Id,
                            mm.Id AS MessageId,
                            ms.SessionId AS SessionId,
                            ms.WorkspaceId AS WorkspaceId,
                            ms.Topic AS Topic,
                            ms.Status AS Status,
                            ms.HostAgentId AS HostAgentId,
                            ms.ParticipantsJson AS ParticipantsJson,
                            mm.ContentMd AS ContentMd,
                            mm.MessageRole AS MessageRole,
                            NULL AS FinalSummaryMd,
                            ms.Provider AS Provider,
                            ms.Model AS Model,
                            mm.CreatedAt AS CreatedAt,
                            mm.CreatedAt AS UpdatedAt,
                            NULL AS EndedAt
                        FROM MeetingMessages mm
                        INNER JOIN MeetingSessions ms ON ms.Id = mm.MeetingId
                        WHERE mm.IsPartial = 0
                          AND mm.MessageRole = 'summary'
                          AND IFNULL(mm.ContentMd, '') <> ''
                          AND mm.CreatedAt >= @Since
                    ) AS ExportRows
                    WHERE UpdatedAt > @Since
                       OR (UpdatedAt = @Since AND CursorId > @LastCursorId)
                    ORDER BY UpdatedAt, CursorId
                    LIMIT {queryLimit}
                    """,
                    ReadRecord,
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Since", lastTimestamp);
                        cmd.Parameters.AddWithValue("@LastCursorId", lastCursorId);
                    });

                if (rows.Count == 0)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var hasMore = rows.Count > batchSize;
                var items = hasMore ? rows.Take(batchSize).ToArray() : rows.ToArray();
                total += items.Length;
                lastTimestamp = items[^1].UpdatedAt;
                lastCursorId = items[^1].CursorId;

                await SendBatchAsync(clientId, requestId, hasMore, items, cancellationToken).ConfigureAwait(false);
                if (!hasMore)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            logger.LogInformation("Meeting replay 完成：Since={Since}, Total={Total}", sinceTimestamp, total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Meeting replay 失败");
            await sendAsync(clientId, PluginBusMessageFactory.CreateControlError(clientId, $"meeting replay failed: {ex.Message}"), cancellationToken).ConfigureAwait(false);
        }
    }

    private static MeetingExportRecord ReadRecord(Microsoft.Data.Sqlite.SqliteDataReader r)
    {
        return new MeetingExportRecord
        {
            CursorId = r.GetString(r.GetOrdinal("CursorId")),
            MeetingId = r.GetString(r.GetOrdinal("Id")),
            RecordKind = r.GetString(r.GetOrdinal("RecordKind")),
            MessageId = r.IsDBNull(r.GetOrdinal("MessageId")) ? null : r.GetString(r.GetOrdinal("MessageId")),
            SessionId = r.GetString(r.GetOrdinal("SessionId")),
            WorkspaceId = r.GetString(r.GetOrdinal("WorkspaceId")),
            Topic = r.GetString(r.GetOrdinal("Topic")),
            Status = r.GetInt32(r.GetOrdinal("Status")),
            HostAgentId = r.GetString(r.GetOrdinal("HostAgentId")),
            ParticipantsJson = r.GetString(r.GetOrdinal("ParticipantsJson")),
            ContentMd = r.IsDBNull(r.GetOrdinal("ContentMd")) ? null : r.GetString(r.GetOrdinal("ContentMd")),
            MessageRole = r.IsDBNull(r.GetOrdinal("MessageRole")) ? null : r.GetString(r.GetOrdinal("MessageRole")),
            FinalSummaryMd = r.IsDBNull(r.GetOrdinal("FinalSummaryMd")) ? null : r.GetString(r.GetOrdinal("FinalSummaryMd")),
            Provider = r.GetString(r.GetOrdinal("Provider")),
            Model = r.GetString(r.GetOrdinal("Model")),
            CreatedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
            UpdatedAt = r.GetInt64(r.GetOrdinal("UpdatedAt")),
            EndedAt = r.IsDBNull(r.GetOrdinal("EndedAt")) ? null : r.GetInt64(r.GetOrdinal("EndedAt")),
        };
    }

    private Task SendBatchAsync(string clientId, string? requestId, bool hasMore, MeetingExportRecord[] items, CancellationToken cancellationToken)
    {
        var batch = new MeetingExportBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            HasMore = hasMore,
            Items = items
        };
        var payload = JsonSerializer.SerializeToElement(batch, WebSocketJsonContext.Default.MeetingExportBatch);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.MeetingTopic,
            Op = CortanaWsEndpoints.MeetingHistoryBatchOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.MeetingHistoryBatchOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }

    private Task SendCompletedAsync(string clientId, string? requestId, int total, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(
            new MeetingHistoryCompletedPayload { Total = total },
            WebSocketJsonContext.Default.MeetingHistoryCompletedPayload);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.MeetingTopic,
            Op = CortanaWsEndpoints.MeetingHistoryCompletedOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.MeetingHistoryCompletedOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }
}
