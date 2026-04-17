using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>
/// 幻灯片摘要信息，用于演示文稿枚举返回。
/// </summary>
public sealed record SlideInfo
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("layout_name")] public string? LayoutName { get; init; }
    [JsonPropertyName("title_preview")] public string TitlePreview { get; init; } = string.Empty;
    [JsonPropertyName("body_preview")] public string BodyPreview { get; init; } = string.Empty;
    [JsonPropertyName("has_notes")] public bool HasNotes { get; init; }
}
