using System.Text.Json.Serialization;

namespace Netor.Cortana.Networks.Proxy;

public sealed record OpenAiModelsResponse(
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("data")] IReadOnlyList<OpenAiModelEntry> Data);

public sealed record OpenAiModelEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("owned_by")] string OwnedBy);

public sealed record OpenAiChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] IReadOnlyList<OllamaMessage>? Messages,
    [property: JsonPropertyName("stream")] bool? Stream,
    [property: JsonPropertyName("temperature")] double? Temperature = null,
    [property: JsonPropertyName("max_tokens")] int? MaxTokens = null,
    [property: JsonPropertyName("top_p")] double? TopP = null);

public sealed record OpenAiChatResponse(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice> Choices,
    [property: JsonPropertyName("usage")] OpenAiUsage? Usage = null);

public sealed record OpenAiChoice(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("message")] OllamaMessage? Message = null,
    [property: JsonPropertyName("delta")] OllamaMessage? Delta = null,
    [property: JsonPropertyName("finish_reason")] string? FinishReason = null);

public sealed record OpenAiUsage(
    [property: JsonPropertyName("prompt_tokens")] long PromptTokens,
    [property: JsonPropertyName("completion_tokens")] long CompletionTokens,
    [property: JsonPropertyName("total_tokens")] long TotalTokens);

public sealed record OpenAiStreamChunk(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("object")] string Object,
    [property: JsonPropertyName("created")] long Created,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("choices")] IReadOnlyList<OpenAiChoice> Choices);
