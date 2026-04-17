using System.Text.Json.Serialization;

namespace Cortana.Plugins.Office.Models;

/// <summary>
/// 工作表摘要信息，用于工作簿枚举返回。
/// </summary>
public sealed record SheetInfo
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("state")] public string State { get; init; } = "visible";
    [JsonPropertyName("used_range")] public string? UsedRange { get; init; }
    [JsonPropertyName("row_count")] public int RowCount { get; init; }
    [JsonPropertyName("column_count")] public int ColumnCount { get; init; }
}
