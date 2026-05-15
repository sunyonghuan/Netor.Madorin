using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus workflow 历史回放请求，按 OrchestrationTask 任务级粒度分页查询、组包响应。
/// 与 <see cref="PluginBusConversationHistoryDispatcher"/> 同模式，但走独立的 workflow topic。
/// 详见 docs/未来版本策划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.5。
/// </summary>
internal sealed class PluginBusWorkflowHistoryDispatcher(
    CortanaDbContext db,
    ILogger logger,
    Func<string, string, CancellationToken, Task> sendAsync)
{
    /// <summary>
    /// 按时间戳分页回放 Workflow 任务，并以 workflow.history.batch/completed 响应发送给指定客户端。
    /// </summary>
    /// <param name="clientId">接收回放数据的客户端标识。</param>
    /// <param name="requestId">原始回放请求标识。</param>
    /// <param name="sinceTimestamp">回放起始时间戳（基于 OrchestrationTask.LastActiveTimestamp）。</param>
    /// <param name="batchSize">每批最多回放的任务数量。</param>
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
                var rows = db.Query(
                    $"""
                    SELECT
                        Id,
                        Title,
                        Status,
                        Mode,
                        SubMode,
                        WorkspaceId,
                        TraceId,
                        SourceSessionId,
                        SourceTaskId,
                        ManagerAgentId,
                        ManagerAgentName,
                        StartedAt,
                        CompletedAt,
                        LastActiveTimestamp,
                        FinalReport,
                        ErrorMessage,
                        TotalTokenCount
                    FROM OrchestrationTask
                    WHERE LastActiveTimestamp >= @Since
                    ORDER BY LastActiveTimestamp
                    LIMIT {queryLimit}
                    """,
                    r => new WorkflowExportRecord
                    {
                        TaskId = r.GetString(r.GetOrdinal("Id")),
                        Title = r.GetString(r.GetOrdinal("Title")),
                        Status = r.GetString(r.GetOrdinal("Status")),
                        Mode = r.GetString(r.GetOrdinal("Mode")),
                        SubMode = r.GetString(r.GetOrdinal("SubMode")),
                        WorkspaceId = r.GetString(r.GetOrdinal("WorkspaceId")),
                        TraceId = r.GetString(r.GetOrdinal("TraceId")),
                        SourceSessionId = r.IsDBNull(r.GetOrdinal("SourceSessionId")) ? null : r.GetString(r.GetOrdinal("SourceSessionId")),
                        SourceTaskId = r.IsDBNull(r.GetOrdinal("SourceTaskId")) ? null : r.GetString(r.GetOrdinal("SourceTaskId")),
                        ManagerAgentId = r.IsDBNull(r.GetOrdinal("ManagerAgentId")) ? null : r.GetString(r.GetOrdinal("ManagerAgentId")),
                        ManagerAgentName = r.IsDBNull(r.GetOrdinal("ManagerAgentName")) ? null : r.GetString(r.GetOrdinal("ManagerAgentName")),
                        StartedAt = r.GetInt64(r.GetOrdinal("StartedAt")),
                        CompletedAt = r.IsDBNull(r.GetOrdinal("CompletedAt")) ? null : r.GetInt64(r.GetOrdinal("CompletedAt")),
                        LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveTimestamp")),
                        FinalReport = r.IsDBNull(r.GetOrdinal("FinalReport")) ? null : r.GetString(r.GetOrdinal("FinalReport")),
                        ErrorMessage = r.IsDBNull(r.GetOrdinal("ErrorMessage")) ? null : r.GetString(r.GetOrdinal("ErrorMessage")),
                        TotalTokenCount = r.GetInt64(r.GetOrdinal("TotalTokenCount")),
                    },
                    cmd => cmd.Parameters.AddWithValue("@Since", last));

                if (rows.Count == 0)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var hasMore = rows.Count > batchSize;
                var items = hasMore ? rows.Take(batchSize).ToArray() : rows.ToArray();
                total += items.Length;
                last = items[^1].LastActiveTimestamp + 1;

                await SendBatchAsync(clientId, requestId, hasMore, items, cancellationToken).ConfigureAwait(false);
                if (!hasMore)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }
            }

            logger.LogInformation("Workflow replay 完成：Since={Since}, Total={Total}", sinceTimestamp, total);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Workflow replay 失败");
            await sendAsync(clientId, PluginBusMessageFactory.CreateControlError(clientId, $"workflow replay failed: {ex.Message}"), cancellationToken).ConfigureAwait(false);
        }
    }

    private Task SendBatchAsync(string clientId, string? requestId, bool hasMore, WorkflowExportRecord[] items, CancellationToken cancellationToken)
    {
        var batch = new WorkflowExportBatch
        {
            BatchId = Guid.NewGuid().ToString("N"),
            HasMore = hasMore,
            Items = items
        };
        var payload = JsonSerializer.SerializeToElement(batch, WebSocketJsonContext.Default.WorkflowExportBatch);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.WorkflowTopic,
            Op = CortanaWsEndpoints.WorkflowHistoryBatchOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.WorkflowHistoryBatchOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }

    private Task SendCompletedAsync(string clientId, string? requestId, int total, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToElement(
            new WorkflowHistoryCompletedPayload { Total = total },
            WebSocketJsonContext.Default.WorkflowHistoryCompletedPayload);
        var message = JsonSerializer.Serialize(new PluginBusEventMessage
        {
            Type = "response",
            Protocol = CortanaWsEndpoints.PluginBusProtocol,
            Version = CortanaWsEndpoints.PluginBusVersion,
            Topic = CortanaWsEndpoints.WorkflowTopic,
            Op = CortanaWsEndpoints.WorkflowHistoryCompletedOperation,
            RequestId = requestId,
            Source = "host",
            Target = "plugin.memory",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            EventType = CortanaWsEndpoints.WorkflowHistoryCompletedOperation,
            Payload = payload
        }, WebSocketJsonContext.Default.PluginBusEventMessage);

        return sendAsync(clientId, message, cancellationToken);
    }
}
