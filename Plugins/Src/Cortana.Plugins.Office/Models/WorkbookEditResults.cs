using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>写入区域操作结果。</summary>
public sealed record WriteRangeResult
{
    [JsonPropertyName("sheet_name")] public string SheetName { get; init; } = string.Empty;
    [JsonPropertyName("start_cell")] public string StartCell { get; init; } = string.Empty;
    [JsonPropertyName("end_cell")] public string EndCell { get; init; } = string.Empty;
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>插入行操作结果。</summary>
public sealed record InsertRowResult
{
    [JsonPropertyName("sheet_name")] public string SheetName { get; init; } = string.Empty;
    [JsonPropertyName("inserted_at")] public int InsertedAt { get; init; }
    [JsonPropertyName("row_count")] public int RowCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>删除行操作结果。</summary>
public sealed record DeleteRowResult
{
    [JsonPropertyName("sheet_name")] public string SheetName { get; init; } = string.Empty;
    [JsonPropertyName("deleted_at")] public int DeletedAt { get; init; }
    [JsonPropertyName("row_count")] public int RowCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>新增工作表操作结果。</summary>
public sealed record AddSheetResult
{
    [JsonPropertyName("sheet_name")] public string SheetName { get; init; } = string.Empty;
    [JsonPropertyName("position_index")] public int PositionIndex { get; init; }
    [JsonPropertyName("sheet_count")] public int SheetCount { get; init; }
    [JsonPropertyName("changed_count")] public int ChangedCount { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}
