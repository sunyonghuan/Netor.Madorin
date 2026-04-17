using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Excel 行操作工具：写入区域、插入行、删除行。
/// </summary>
[Tool]
public sealed class OfficeExcelRowTools(
    IExcelWorkbookService excelService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeExcelRowTools> logger)
{
    /// <summary>从指定起始单元格写入二维数据块。</summary>
    [Tool(Name = "office_excel_write_range",
        Description = "从指定起始单元格写入二维数据块。values 为 JSON 二维数组，外层为行，内层为列。以 = 开头的字符串按公式写入。")]
    public string WriteRange(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.xlsx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "工作表名称")] string sheetName,
        [Parameter(Description = "起始单元格地址（如 A1）")] string startCell,
        [Parameter(Description = "二维数据，JSON 二维数组格式 [[\"a\",\"b\"],[\"c\",\"d\"]]")] string values,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (ToolParameterHelper.IsEmpty(sheetName))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "sheetName 不能为空。");
        if (ToolParameterHelper.IsEmpty(startCell))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "startCell 不能为空。");
        if (ToolParameterHelper.IsEmpty(values))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "values 不能为空。");

        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        // 解析二维数组
        string[][]? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize(values, PluginJsonContext.Default.StringArrayArray);
        }
        catch (JsonException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, $"values JSON 格式错误: {ex.Message}");
        }

        if (parsed is null || parsed.Length == 0)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "values 不能为空数组。");

        try
        {
            var result = excelService.WriteRange(validSource!, validOutput!, sheetName, startCell, parsed);
            return ToolResult.Ok($"写入完成，共 {result.ChangedCount} 个单元格。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.WriteRangeResult));
        }
        catch (KeyNotFoundException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "写入区域失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"写入区域失败: {ex.Message}");
        }
    }

    /// <summary>在指定行之前插入空行。</summary>
    [Tool(Name = "office_excel_insert_row",
        Description = "在指定行之前插入一个或多个空行。row_index 从 1 开始。row_count 默认 1，最大 1000。")]
    public string InsertRow(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.xlsx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "工作表名称")] string sheetName,
        [Parameter(Description = "插入位置行号（从 1 开始）")] int rowIndex,
        [Parameter(Description = "插入行数（1-1000）")] int rowCount,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (ToolParameterHelper.IsEmpty(sheetName))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "sheetName 不能为空。");
        if (rowIndex < 1)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rowIndex 必须 >= 1。");
        if (rowCount < 1 || rowCount > 1000)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rowCount 必须在 1-1000 之间。");

        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = excelService.InsertRow(validSource!, validOutput!, sheetName, rowIndex, rowCount);
            return ToolResult.Ok($"插入 {rowCount} 行成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.InsertRowResult));
        }
        catch (KeyNotFoundException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "插入行失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"插入行失败: {ex.Message}");
        }
    }

    /// <summary>删除指定工作表中的连续行。</summary>
    [Tool(Name = "office_excel_delete_row",
        Description = "删除指定工作表中的一个或多个连续行。row_index 从 1 开始。require_non_empty 为 1 时仅删除含数据的行。")]
    public string DeleteRow(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.xlsx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "工作表名称")] string sheetName,
        [Parameter(Description = "起始行号（从 1 开始）")] int rowIndex,
        [Parameter(Description = "删除行数（1-1000）")] int rowCount,
        [Parameter(Description = "是否要求目标行非空：0=允许删除空行，1=空行返回错误")] int requireNonEmpty,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (ToolParameterHelper.IsEmpty(sheetName))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "sheetName 不能为空。");
        if (rowIndex < 1)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rowIndex 必须 >= 1。");
        if (rowCount < 1 || rowCount > 1000)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rowCount 必须在 1-1000 之间。");

        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = excelService.DeleteRow(validSource!, validOutput!, sheetName, rowIndex, rowCount,
                ToolParameterHelper.IsTrue(requireNonEmpty));
            return ToolResult.Ok($"删除 {rowCount} 行成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.DeleteRowResult));
        }
        catch (KeyNotFoundException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除行失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"删除行失败: {ex.Message}");
        }
    }

    /// <summary>校验源文件和输出路径的通用逻辑。</summary>
    private (string? ValidSource, string? ValidOutput, string? Error) ValidateSourceAndOutput(
        string sourcePath, string outputPath, int overwrite)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return (null, null, ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。"));
        if (!File.Exists(validSource))
            return (null, null, ToolResult.Fail(ErrorCodes.FileNotFound, $"源文件不存在: {validSource}"));

        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return (null, null, ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。"));
        if (string.Equals(validSource, validOutput, StringComparison.OrdinalIgnoreCase))
            return (null, null, ToolResult.Fail(ErrorCodes.InvalidArgument, "输出路径不能与源文件相同。"));
        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return (null, null, ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}"));

        return (validSource, validOutput, null);
    }
}
