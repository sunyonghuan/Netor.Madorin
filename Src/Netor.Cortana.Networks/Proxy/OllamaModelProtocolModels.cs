using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Cortana.Networks.Proxy;

public sealed record OllamaTagsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<OllamaModelInfo> Models);

public sealed record OllamaModelInfo(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("modified_at")] DateTimeOffset ModifiedAt,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("digest")] string Digest,
    [property: JsonPropertyName("details")] OllamaModelDetails Details);

public sealed record OllamaModelDetails(
    [property: JsonPropertyName("parent_model")] string ParentModel,
    [property: JsonPropertyName("format")] string Format,
    [property: JsonPropertyName("family")] string Family,
    [property: JsonPropertyName("families")] IReadOnlyList<string> Families,
    [property: JsonPropertyName("parameter_size")] string ParameterSize,
    [property: JsonPropertyName("quantization_level")] string QuantizationLevel);

public sealed record OllamaShowRequest(
    [property: JsonPropertyName("model")] string Model);

public sealed record OllamaShowResponse(
    [property: JsonPropertyName("license")] string License,
    [property: JsonPropertyName("modelfile")] string Modelfile,
    [property: JsonPropertyName("parameters")] string Parameters,
    [property: JsonPropertyName("template")] string Template,
    [property: JsonPropertyName("details")] OllamaModelDetails Details,
    [property: JsonPropertyName("model_info")] Dictionary<string, JsonElement>? ModelInfo = null,
    [property: JsonPropertyName("tensors")] JsonElement? Tensors = null,
    [property: JsonPropertyName("capabilities")] IReadOnlyList<string>? Capabilities = null,
    [property: JsonPropertyName("modified_at")] string? ModifiedAt = null);

public sealed record OllamaRunningModelsResponse(
    [property: JsonPropertyName("models")] IReadOnlyList<OllamaModelInfo> Models);
