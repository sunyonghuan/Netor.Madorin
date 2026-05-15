using System.Text.Json;
using System.Text.Json.Serialization;

using Netor.Cortana.Entitys;
using Netor.Cortana.Entitys.Memory;
using Netor.Cortana.Entitys.ModelCapability;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 消息 JSON 源生成器上下文（AOT 兼容）。
/// </summary>
[JsonSerializable(typeof(WsMessage))]
[JsonSerializable(typeof(PluginBusControlMessage))]
[JsonSerializable(typeof(PluginBusEventMessage))]
[JsonSerializable(typeof(ConversationExportBatch))]
[JsonSerializable(typeof(ConversationHistoryCompletedPayload))]
[JsonSerializable(typeof(ConversationExportRecord))]
[JsonSerializable(typeof(ConversationTurnStartedArgs))]
[JsonSerializable(typeof(ConversationUserMessageArgs))]
[JsonSerializable(typeof(ConversationAssistantDeltaArgs))]
[JsonSerializable(typeof(ConversationTurnCompletedArgs))]
[JsonSerializable(typeof(ModelCapabilityRequest))]
[JsonSerializable(typeof(ModelCapabilityResponse))]
[JsonSerializable(typeof(ModelCapabilityError))]
[JsonSerializable(typeof(ModelCapabilityConnectedMessage))]
[JsonSerializable(typeof(MemoryContextSupplyRequest))]
[JsonSerializable(typeof(MemoryContextSupplyPackage))]
[JsonSerializable(typeof(MemoryContextSupplyError))]
// 阶段 2B 新增：Workflow 任务事件 Args
[JsonSerializable(typeof(WorkflowTaskStartedArgs))]
[JsonSerializable(typeof(WorkflowStepCompletedArgs))]
[JsonSerializable(typeof(WorkflowTaskCompletedArgs))]
[JsonSerializable(typeof(WorkflowTaskFailedArgs))]
[JsonSerializable(typeof(WorkflowTaskTitleUpdatedArgs))]
[JsonSerializable(typeof(WorkflowExportBatch))]
[JsonSerializable(typeof(WorkflowExportRecord))]
[JsonSerializable(typeof(WorkflowHistoryCompletedPayload))]
internal partial class WebSocketJsonContext : JsonSerializerContext;

/// <summary>
/// WebSocket JSON 消息（替代匿名类型，AOT 兼容）。
/// </summary>
internal sealed record WsMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }
}

/// <summary>
/// 内部插件总线控制消息。
/// </summary>
internal sealed record PluginBusControlMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("clientId")]
    public string? ClientId { get; init; }

    [JsonPropertyName("topics")]
    public string[]? Topics { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}

/// <summary>
/// 内部插件总线事件消息。
/// </summary>
internal sealed record PluginBusEventMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("requestId")]
    public string? RequestId { get; init; }

    [JsonPropertyName("op")]
    public string? Op { get; init; }

    [JsonPropertyName("source")]
    public string? Source { get; init; }

    [JsonPropertyName("target")]
    public string? Target { get; init; }

    [JsonPropertyName("timestamp")]
    public long? Timestamp { get; init; }

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>
/// 历史回放批次负载（conversation.history.batch）。
/// </summary>
internal sealed record ConversationExportBatch
{
    [JsonPropertyName("batchId")] public string BatchId { get; init; } = string.Empty;
    [JsonPropertyName("hasMore")] public bool HasMore { get; init; }
    [JsonPropertyName("items")] public ConversationExportRecord[] Items { get; init; } = Array.Empty<ConversationExportRecord>();
}

/// <summary>
/// 历史导出消息记录。
/// </summary>
internal sealed record ConversationExportRecord
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("agentId")] public string? AgentId { get; init; }
    [JsonPropertyName("workspaceId")] public string? WorkspaceId { get; init; }
    [JsonPropertyName("sessionId")] public string SessionId { get; init; } = string.Empty;
    [JsonPropertyName("turnId")] public string? TurnId { get; init; }
    [JsonPropertyName("messageId")] public string? MessageId { get; init; }
    [JsonPropertyName("eventType")] public string? EventType { get; init; }
    [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;
    [JsonPropertyName("content")] public string? Content { get; init; }
    [JsonPropertyName("createdTimestamp")] public long CreatedTimestamp { get; init; }
    [JsonPropertyName("providerId")] public string? ProviderId { get; init; }
    [JsonPropertyName("providerName")] public string? ProviderName { get; init; }
    [JsonPropertyName("agentName")] public string? AgentName { get; init; }
    [JsonPropertyName("modelId")] public string? ModelId { get; init; }
    [JsonPropertyName("modelName")] public string? ModelName { get; init; }
    [JsonPropertyName("traceId")] public string? TraceId { get; init; }
}

// ════════════════════════════════════════════════════════════════════════
// 阶段 2B 新增：Workflow 历史回放数据契约
// 详见 docs/未来版本策划/多智能体编排模式策划/07-事件分流与插件兼容设计.md §3.5
// ════════════════════════════════════════════════════════════════════════

/// <summary>
/// Workflow 历史回放批次负载（workflow.history.batch）。
/// </summary>
internal sealed record WorkflowExportBatch
{
    [JsonPropertyName("batchId")] public string BatchId { get; init; } = string.Empty;
    [JsonPropertyName("hasMore")] public bool HasMore { get; init; }
    [JsonPropertyName("items")] public WorkflowExportRecord[] Items { get; init; } = Array.Empty<WorkflowExportRecord>();
}

/// <summary>
/// Workflow 任务级历史导出记录（一条记录代表一个完整任务）。
/// 阶段 2B 起使用任务级粒度而非消息级，与文档 §3.5 对齐。
/// </summary>
internal sealed record WorkflowExportRecord
{
    [JsonPropertyName("taskId")] public string TaskId { get; init; } = string.Empty;
    [JsonPropertyName("title")] public string Title { get; init; } = string.Empty;
    [JsonPropertyName("status")] public string Status { get; init; } = string.Empty;
    [JsonPropertyName("mode")] public string Mode { get; init; } = string.Empty;
    [JsonPropertyName("subMode")] public string SubMode { get; init; } = string.Empty;
    [JsonPropertyName("workspaceId")] public string WorkspaceId { get; init; } = string.Empty;
    [JsonPropertyName("traceId")] public string TraceId { get; init; } = string.Empty;
    [JsonPropertyName("sourceSessionId")] public string? SourceSessionId { get; init; }
    [JsonPropertyName("sourceTaskId")] public string? SourceTaskId { get; init; }
    [JsonPropertyName("managerAgentId")] public string? ManagerAgentId { get; init; }
    [JsonPropertyName("managerAgentName")] public string? ManagerAgentName { get; init; }
    [JsonPropertyName("startedAt")] public long StartedAt { get; init; }
    [JsonPropertyName("completedAt")] public long? CompletedAt { get; init; }
    [JsonPropertyName("lastActiveTimestamp")] public long LastActiveTimestamp { get; init; }
    [JsonPropertyName("finalReport")] public string? FinalReport { get; init; }
    [JsonPropertyName("errorMessage")] public string? ErrorMessage { get; init; }
    [JsonPropertyName("totalTokenCount")] public long TotalTokenCount { get; init; }
}

/// <summary>
/// Workflow 历史回放完成响应载荷（workflow.history.completed）。
/// </summary>
internal sealed record WorkflowHistoryCompletedPayload
{
    [JsonPropertyName("total")] public int Total { get; init; }
}
