using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 兼容协议 JSON 源生成上下文，保持 Native AOT 兼容。
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OllamaVersionResponse))]
[JsonSerializable(typeof(OllamaTagsResponse))]
[JsonSerializable(typeof(OllamaModelInfo))]
[JsonSerializable(typeof(OllamaModelDetails))]
[JsonSerializable(typeof(OllamaMessage))]
[JsonSerializable(typeof(OllamaChatRequest))]
[JsonSerializable(typeof(OllamaGenerateRequest))]
[JsonSerializable(typeof(OllamaRequestOptions))]
[JsonSerializable(typeof(OllamaChatResponse))]
[JsonSerializable(typeof(OllamaGenerateResponse))]
[JsonSerializable(typeof(OllamaShowRequest))]
[JsonSerializable(typeof(OllamaShowResponse))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(OllamaRunningModelsResponse))]
[JsonSerializable(typeof(OllamaErrorResponse))]
[JsonSerializable(typeof(OpenAiModelsResponse))]
[JsonSerializable(typeof(OpenAiModelEntry))]
[JsonSerializable(typeof(OpenAiChatRequest))]
[JsonSerializable(typeof(OpenAiChatResponse))]
[JsonSerializable(typeof(OpenAiChoice))]
[JsonSerializable(typeof(OpenAiUsage))]
[JsonSerializable(typeof(OpenAiStreamChunk))]
internal partial class OllamaProxyJsonContext : JsonSerializerContext;