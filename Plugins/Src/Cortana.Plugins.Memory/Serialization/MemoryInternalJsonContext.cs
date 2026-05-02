using System.Text.Json.Serialization;
using Cortana.Plugins.Memory.Models;
using Cortana.Plugins.Memory.Processing;

namespace Cortana.Plugins.Memory.Serialization;

/// <summary>
/// 记忆插件内部使用的 JSON 源生成上下文。
/// 集中声明 Storage / Service / Processing 层会经过 <c>JsonSerializer</c> 处理的所有具体类型，
/// 让序列化在 Native AOT 与 Trim 模式下也保持安全。
/// </summary>
[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Default)]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(IReadOnlyList<string>))]
[JsonSerializable(typeof(MemoryFragment))]
[JsonSerializable(typeof(MemoryAbstraction))]
[JsonSerializable(typeof(MemoryProcessingResult))]
[JsonSerializable(typeof(RecallBudgetSnapshot))]
[JsonSerializable(typeof(RecallPolicySnapshot))]
[JsonSerializable(typeof(ConversationFeedSubscribeFrame))]
[JsonSerializable(typeof(ConversationFeedReplayFrame))]
[JsonSerializable(typeof(McpObservationSourceFacts))]
[JsonSerializable(typeof(FragmentExtractedEventPayload))]
[JsonSerializable(typeof(ProcessingFailedEventPayload))]
[JsonSerializable(typeof(AbstractionCreatedEventPayload))]
internal sealed partial class MemoryInternalJsonContext : JsonSerializerContext
{
}

/// <summary>召回时记录到 <c>RecallLog.BudgetJson</c> 的预算快照。</summary>
internal sealed class RecallBudgetSnapshot
{
    public int MaxWindowCount { get; init; }
    public int MaxMemoryCount { get; init; }
    public double MinimumConfidence { get; init; }
}

/// <summary>召回时记录到 <c>RecallLog.AppliedPolicyJson</c> 的策略快照。</summary>
internal sealed class RecallPolicySnapshot
{
    public bool IncludeCandidateMemories { get; init; }
    public string Ranking { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public bool SupportsPendingCandidateOnQueryMatch { get; init; }
}

/// <summary>conversation-feed 订阅帧。</summary>
internal sealed class ConversationFeedSubscribeFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "subscribe";
    [JsonPropertyName("topics")] public string[] Topics { get; init; } = [];
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;
}

/// <summary>conversation-feed 回放请求帧。</summary>
internal sealed class ConversationFeedReplayFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "replay";
    [JsonPropertyName("sinceTimestamp")] public long SinceTimestamp { get; init; }
    [JsonPropertyName("batchSize")] public int BatchSize { get; init; }
}

/// <summary>MCP 显式写入 observation 时记录的来源快照。</summary>
internal sealed class McpObservationSourceFacts
{
    public string Source { get; init; } = string.Empty;
    public string AgentId { get; init; } = string.Empty;
    public string? WorkspaceId { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string TurnId { get; init; } = string.Empty;
    public string MessageId { get; init; } = string.Empty;
    public string Role { get; init; } = string.Empty;
    public string Content { get; init; } = string.Empty;
    public long CreatedTimestamp { get; init; }
}

/// <summary>记忆数据处理时记录的"长期记忆抽取完成"事件载荷。</summary>
internal sealed class FragmentExtractedEventPayload
{
    public string FragmentId { get; init; } = string.Empty;
    public string ObservationId { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
}

/// <summary>记忆数据处理失败事件载荷。</summary>
internal sealed class ProcessingFailedEventPayload
{
    public string ObservationId { get; init; } = string.Empty;
    public string Error { get; init; } = string.Empty;
    public string TraceId { get; init; } = string.Empty;
}

/// <summary>抽象记忆生成事件载荷。</summary>
internal sealed class AbstractionCreatedEventPayload
{
    public string AbstractionId { get; init; } = string.Empty;
    public string SupportingMemoryIdsJson { get; init; } = string.Empty;
}
