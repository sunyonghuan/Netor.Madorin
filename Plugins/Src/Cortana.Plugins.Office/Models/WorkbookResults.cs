using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>创建工作簿操作结果。</summary>
public sealed record CreateWorkbookResult
{
    [JsonPropertyName("workbook_path")] public string WorkbookPath { get; init; } = string.Empty;
    [JsonPropertyName("sheet_count")] public int SheetCount { get; init; }
    [JsonPropertyName("created_from_template")] public bool CreatedFromTemplate { get; init; }
    [JsonPropertyName("output_path")] public string OutputPath { get; init; } = string.Empty;
}

/// <summary>工作表列表读取结果。</summary>
public sealed record ListSheetsResult
{
    [JsonPropertyName("sheets")] public List<SheetInfo> Sheets { get; init; } = [];
    [JsonPropertyName("active_sheet")] public string? ActiveSheet { get; init; }
    [JsonPropertyName("sheet_count")] public int SheetCount { get; init; }
}

/// <summary>读取区域操作结果。</summary>
public sealed record ReadRangeResult
{
    [JsonPropertyName("sheet_name")] public string SheetName { get; init; } = string.Empty;
    [JsonPropertyName("range_ref")] public string RangeRef { get; init; } = string.Empty;
    [JsonPropertyName("rows")] public int Rows { get; init; }
    [JsonPropertyName("columns")] public int Columns { get; init; }
    [JsonPropertyName("values")] public string[][] Values { get; init; } = [];
}
