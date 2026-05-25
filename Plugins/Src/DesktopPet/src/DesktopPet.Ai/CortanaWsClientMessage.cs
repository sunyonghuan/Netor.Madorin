using System.Text.Json.Serialization;

namespace DesktopPet.Ai;

public sealed record CortanaWsClientMessage
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("data")]
    public string? Data { get; init; }

    [JsonPropertyName("attachments")]
    public IReadOnlyList<CortanaWsAttachment>? Attachments { get; init; }
}
