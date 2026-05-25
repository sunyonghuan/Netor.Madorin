using System.Text.Json.Serialization;

namespace DesktopPet.Ai;

public sealed record CortanaWsServerMessage
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
