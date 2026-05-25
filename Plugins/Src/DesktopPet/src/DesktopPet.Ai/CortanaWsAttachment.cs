using System.Text.Json.Serialization;

namespace DesktopPet.Ai;

public sealed record CortanaWsAttachment
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;
}
