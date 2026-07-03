using System.Text.Json.Serialization;

namespace Netor.Cortana.Networks.Proxy;

public sealed record OllamaVersionResponse(
    [property: JsonPropertyName("version")] string Version);

public sealed record OllamaErrorResponse(
    [property: JsonPropertyName("error")] string Error);
