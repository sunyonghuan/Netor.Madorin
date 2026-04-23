using System.Text.Json.Serialization;

namespace ReminderPlugin;

[JsonSerializable(typeof(ReminderItem))]
[JsonSerializable(typeof(List<ReminderItem>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(WsClientMessage))]
[JsonSerializable(typeof(WsServerMessage))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PluginJsonContext : JsonSerializerContext;

public sealed record WsClientMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }
}

public sealed record WsServerMessage
{
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("data")] public string? Data { get; init; }
    [JsonPropertyName("clientId")] public string? ClientId { get; init; }
}
