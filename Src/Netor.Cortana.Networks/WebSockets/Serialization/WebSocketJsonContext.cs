using System.Text.Json;
using System.Text.Json.Serialization;

using Netor.Cortana.Entitys;

namespace Netor.Cortana.Networks;

/// <summary>
/// WebSocket 消息 JSON 源生成器上下文（AOT 兼容）。
/// </summary>
[JsonSerializable(typeof(WsMessage))]
[JsonSerializable(typeof(ConversationFeedControlMessage))]
[JsonSerializable(typeof(ConversationFeedEventMessage))]
[JsonSerializable(typeof(ConversationExportBatch))]
[JsonSerializable(typeof(ConversationExportRecord))]
[JsonSerializable(typeof(ConversationTurnStartedArgs))]
[JsonSerializable(typeof(ConversationUserMessageArgs))]
[JsonSerializable(typeof(ConversationAssistantDeltaArgs))]
[JsonSerializable(typeof(ConversationTurnCompletedArgs))]
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
/// 内部对话事件 feed 的控制消息。
/// </summary>
internal sealed record ConversationFeedControlMessage
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
/// 内部对话事件 feed 的事件消息。
/// </summary>
internal sealed record ConversationFeedEventMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = string.Empty;

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("topic")]
    public string Topic { get; init; } = string.Empty;

    [JsonPropertyName("eventType")]
    public string EventType { get; init; } = string.Empty;

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

/// <summary>
/// 历史回放批次负载（conversation.export.batch）。
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
