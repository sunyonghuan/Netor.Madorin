using System.Text.Encodings.Web;
using System.Text.Json;
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
[JsonSerializable(typeof(PluginBusSubscribeFrame))]
[JsonSerializable(typeof(PluginBusReplayFrame))]
[JsonSerializable(typeof(PluginBusReplayPayload))]
[JsonSerializable(typeof(MemoryContextSupplyRequest))]
[JsonSerializable(typeof(MemoryContextSupplyPackage))]
[JsonSerializable(typeof(MemoryContextSupplyError))]
[JsonSerializable(typeof(McpObservationSourceFacts))]
[JsonSerializable(typeof(FragmentExtractedEventPayload))]
[JsonSerializable(typeof(ProcessingFailedEventPayload))]
[JsonSerializable(typeof(AbstractionCreatedEventPayload))]
internal sealed partial class MemoryInternalJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 中文明文序列化实例，避免中文被转义为 \uXXXX。
    /// </summary>
    public static MemoryInternalJsonContext Chinese { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
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

/// <summary>PluginBus 订阅帧。</summary>
internal sealed class PluginBusSubscribeFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "subscribe";
    [JsonPropertyName("topics")] public string[] Topics { get; init; } = [];
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = string.Empty;
    [JsonPropertyName("version")] public string Version { get; init; } = string.Empty;

    /// <summary>
    /// 阶段 5B Phase 4 新增：客户端声明的 capability token 列表（如 "workflow.v1" / "memory.v1"）。
    /// 宿主在校验 workflow topic 订阅时检查此字段；缺失时按降级处理（决策 5B-D）。
    /// 详见 docs/未来版本策划/多智能体编排模式策划/04-实施阶段.md §5B.4 / Phase 4 实施计划 §5.2。
    /// </summary>
    [JsonPropertyName("capabilities")]
    public string[]? Capabilities { get; init; }
}

/// <summary>PluginBus 历史回放请求帧。</summary>
internal sealed class PluginBusReplayFrame
{
    [JsonPropertyName("type")] public string Type { get; init; } = "request";
    [JsonPropertyName("protocol")] public string Protocol { get; init; } = "cortana.plugin-bus";
    [JsonPropertyName("version")] public string Version { get; init; } = "1.0.0";
    [JsonPropertyName("topic")] public string Topic { get; init; } = "conversation";
    [JsonPropertyName("op")] public string Op { get; init; } = "conversation.history.replay";
    [JsonPropertyName("requestId")] public string RequestId { get; init; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("payload")] public PluginBusReplayPayload Payload { get; init; } = new();
}

/// <summary>PluginBus 历史回放请求载荷。</summary>
internal sealed class PluginBusReplayPayload
{
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
