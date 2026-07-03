using System.Text.Json;

using Microsoft.Extensions.Logging;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Networks;

/// <summary>
/// 处理 PluginBus workflow 历史回放请求，按 WorkTasks 任务级粒度分页查询、组包响应。
/// 与 <see cref="PluginBusConversationHistoryDispatcher"/> 同模式，但走独立的 workflow topic。
/// 原始协议设计见 Docs/已完成功能规划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.5；
/// WorkTasks 换表修复见 Docs/已完成功能规划/长期记忆事件链路修复。
/// </summary>
internal sealed class PluginBusWorkflowHistoryDispatcher(
    CortanaDbContext db,
    AgentService agentService,
    ILogger logger,
    Func<string, string, CancellationToken, Task> sendAsync)
{
    /// <summary>
    /// 按时间戳分页回放 Workflow 任务，并以 workflow.history.batch/completed 响应发送给指定客户端。
    /// </summary>
    /// <param name="clientId">接收回放数据的客户端标识。</param>
    /// <param name="requestId">原始回放请求标识。</param>
    /// <param name="sinceTimestamp">回放起始时间戳（基于 WorkTasks.LastActiveAt）。</param>
    /// <param name="batchSize">每批最多回放的任务数量。</param>
    /// <param name="cancellationToken">取消查询或发送的令牌。</param>
    public async Task ReplayAsync(string clientId, string? requestId, long sinceTimestamp, int batchSize, CancellationToken cancellationToken)
    {
        try
        {
            var total = 0;
            var lastTimestamp = sinceTimestamp;
            var lastId = string.Empty;
            while (true)
            {
                var queryLimit = batchSize + 1;
                var rows = db.Query(
                    $"""
                    SELECT
                        Id,
                        Title,
                        WorkspaceId,
                        SessionId,
                        SourceTaskId,
                        AgentName,
                        CreatedAt,
                        CompletedAt,
                        LastActiveAt,
                        FinalReport
                    FROM WorkTasks
                    WHERE (LastActiveAt > @Since OR (LastActiveAt = @Since AND Id > @LastId))
                      AND CompletedAt IS NOT NULL
                      AND ErrorMessage IS NULL
                      AND IFNULL(FinalReport, '') <> ''
                    ORDER BY LastActiveAt, Id
                    LIMIT {queryLimit}
                    """,
                    r => new WorkflowExportRecord
                    {
                        TaskId = r.GetString(r.GetOrdinal("Id")),
                        Title = r.GetString(r.GetOrdinal("Title")),
                        Status = "completed",
                        Mode = "workflow",
                        SubMode = "work",
                        WorkspaceId = r.GetString(r.GetOrdinal("WorkspaceId")),
                        TraceId = string.Empty,
                        SourceSessionId = r.GetString(r.GetOrdinal("SessionId")),
                        SourceTaskId = r.IsDBNull(r.GetOrdinal("SourceTaskId")) ? null : r.GetString(r.GetOrdinal("SourceTaskId")),
                        ManagerAgentId = r.GetString(r.GetOrdinal("AgentName")),
                        ManagerAgentName = ResolveAgentDisplayName(r.GetString(r.GetOrdinal("AgentName"))),
                        StartedAt = r.GetInt64(r.GetOrdinal("CreatedAt")),
                        CompletedAt = r.GetInt64(r.GetOrdinal("CompletedAt")),
                        LastActiveTimestamp = r.GetInt64(r.GetOrdinal("LastActiveAt")),
                        FinalReport = r.IsDBNull(r.GetOrdinal("FinalReport")) ? null : r.GetString(r.GetOrdinal("FinalReport")),
                        ErrorMessage = null,
                        TotalTokenCount = 0,
                        AllowMemoryIngest = ResolveAllowMemoryIngest(r.GetString(r.GetOrdinal("AgentName"))),
                    },
                    cmd =>
                    {
                        cmd.Parameters.AddWithValue("@Since", lastTimestamp);
                        cmd.Parameters.AddWithValue("@LastId", lastId);
                    });

                if (rows.Count == 0)
                {
                    await SendCompletedAsync(clientId, requestId, total, cancellationToken).ConfigureAwait(false);
                    break;
                }

                var hasMore = rows.Count > batchSize;
                var items = hasMore ? rows.Take(batchSize).ToArray() : rows.ToArray();
                total += items.Length;
                lastTimestamp = items[^1].LastActiveTimestamp;
                lastId = items[^1].TaskId;

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

    private string ResolveAgentDisplayName(string agentName)
    {
        return agentService.GetByName(agentName)?.Name ?? agentName;
    }

    private bool ResolveAllowMemoryIngest(string agentName)
    {
        return agentService.GetByName(agentName)?.AllowWorkflowMemory ?? true;
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
