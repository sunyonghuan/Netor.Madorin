using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>新增幻灯片操作结果。</summary>
public sealed record AddSlideResult
{
    [JsonPropertyName("slide_index")] public int SlideIndex { get; init; }
    [JsonPropertyName("layout_name")] public string LayoutName { get; init; } = string.Empty;
    [JsonPropertyName("slide_count")] public int SlideCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>更新幻灯片标题操作结果。</summary>
public sealed record UpdateSlideTitleResult
{
    [JsonPropertyName("slide_index")] public int SlideIndex { get; init; }
    [JsonPropertyName("old_title")] public string? OldTitle { get; init; }
    [JsonPropertyName("new_title")] public string NewTitle { get; init; } = string.Empty;
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>更新幻灯片正文操作结果。</summary>
public sealed record UpdateSlideBodyResult
{
    [JsonPropertyName("slide_index")] public int SlideIndex { get; init; }
    [JsonPropertyName("paragraph_count")] public int ParagraphCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>删除幻灯片操作结果。</summary>
public sealed record DeleteSlideResult
{
    [JsonPropertyName("deleted_index")] public int DeletedIndex { get; init; }
    [JsonPropertyName("slide_count")] public int SlideCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>更新幻灯片备注操作结果。</summary>
public sealed record UpdateSlideNotesResult
{
    [JsonPropertyName("slide_index")] public int SlideIndex { get; init; }
    [JsonPropertyName("paragraph_count")] public int ParagraphCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}
