using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>创建文档操作结果。</summary>
public sealed record CreateDocumentResult
{
    [JsonPropertyName("document_path")] public string DocumentPath { get; init; } = string.Empty;
    [JsonPropertyName("created_from_template")] public bool CreatedFromTemplate { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>文档大纲读取结果。</summary>
public sealed record DocumentOutlineResult
{
    [JsonPropertyName("paragraphs")] public List<ParagraphInfo> Paragraphs { get; init; } = [];
    [JsonPropertyName("tables_count")] public int TablesCount { get; init; }
    [JsonPropertyName("images_count")] public int ImagesCount { get; init; }
}

/// <summary>另存为操作结果。</summary>
public sealed record SaveAsResult
{
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
    [JsonPropertyName("file_size")] public long FileSize { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
}
