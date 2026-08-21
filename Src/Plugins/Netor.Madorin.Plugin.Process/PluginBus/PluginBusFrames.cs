using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Madorin.Plugin.PluginBus;

internal sealed record PluginBusEventEnvelope
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "event";

    [JsonPropertyName("op")]
    public string Op { get; init; } = string.Empty;

    [JsonPropertyName("sourcePluginId")]
    public string? SourcePluginId { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}

internal sealed record PluginBusSubscribeFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "subscribe";

    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = "cortana.plugin-bus";

    [JsonPropertyName("version")]
    public string Version { get; init; } = "1.4.0";

    [JsonPropertyName("topics")]
    public IReadOnlyList<string>? Topics { get; init; }

    [JsonPropertyName("subscribedOps")]
    public IReadOnlyList<string>? SubscribedOps { get; init; }
}

internal sealed record PluginBusInboundFrame
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("op")]
    public string? Op { get; init; }

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; init; }
}
