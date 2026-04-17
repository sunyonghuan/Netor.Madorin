using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>
/// 段落摘要信息，用于文档大纲返回。
/// </summary>
public sealed record ParagraphInfo
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("style")] public string? Style { get; init; }
    [JsonPropertyName("text_preview")] public string TextPreview { get; init; } = string.Empty;
    [JsonPropertyName("is_empty")] public bool IsEmpty { get; init; }
}
