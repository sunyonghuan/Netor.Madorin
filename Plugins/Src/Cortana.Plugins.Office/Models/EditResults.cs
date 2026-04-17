using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>插入段落操作结果。</summary>
public sealed record InsertParagraphResult
{
    [JsonPropertyName("inserted_index")] public int InsertedIndex { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>删除段落操作结果。</summary>
public sealed record DeleteParagraphResult
{
    [JsonPropertyName("deleted_index")] public int DeletedIndex { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>文本替换操作结果。</summary>
public sealed record ReplaceTextResult
{
    [JsonPropertyName("replaced_count")] public int ReplacedCount { get; init; }
    [JsonPropertyName("sample_before")] public string? SampleBefore { get; init; }
    [JsonPropertyName("sample_after")] public string? SampleAfter { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>插入表格操作结果。</summary>
public sealed record InsertTableResult
{
    [JsonPropertyName("table_index")] public int TableIndex { get; init; }
    [JsonPropertyName("rows")] public int Rows { get; init; }
    [JsonPropertyName("columns")] public int Columns { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}
