using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>创建演示文稿操作结果。</summary>
public sealed record CreatePresentationResult
{
    [JsonPropertyName("presentation_path")] public string PresentationPath { get; init; } = string.Empty;
    [JsonPropertyName("slide_count")] public int SlideCount { get; init; }
    [JsonPropertyName("created_from_template")] public bool CreatedFromTemplate { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>幻灯片列表读取结果。</summary>
public sealed record ListSlidesResult
{
    [JsonPropertyName("slides")] public List<SlideInfo> Slides { get; init; } = [];
    [JsonPropertyName("slide_count")] public int SlideCount { get; init; }
}
