using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Excel 工作簿级操作工具：创建工作簿、列出工作表、读取区域、另存为。
/// </summary>
[Tool]
public sealed class OfficeExcelWorkbookTools(
    IExcelWorkbookService excelService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeExcelWorkbookTools> logger)
{
    /// <summary>创建空白 xlsx 工作簿或基于模板复制创建。</summary>
    [Tool(Name = "office_excel_create_workbook",
        Description = "创建空白 xlsx 工作簿或基于模板复制创建。sheet_names 为 JSON 数组或逗号分隔的工作表名称，空字符串时默认创建 Sheet1。")]
    public string CreateWorkbook(
        [Parameter(Description = "输出文件路径（.xlsx）")] string outputPath,
        [Parameter(Description = "模板文件路径，空字符串表示创建空白工作簿")] string templatePath,
        [Parameter(Description = "工作表名称列表，JSON 数组或逗号分隔，空字符串时默认 Sheet1")] string sheetNames,
        [Parameter(Description = "文档作者，空字符串表示不设置")] string author,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。");

        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}");

        string? validTemplate = null;
        if (!ToolParameterHelper.IsEmpty(templatePath))
        {
            validTemplate = pathGuard.ValidatePath(templatePath);
            if (validTemplate is null)
                return ToolResult.Fail(ErrorCodes.PathForbidden, "模板路径不在允许目录内。");
            if (!File.Exists(validTemplate))
                return ToolResult.Fail(ErrorCodes.FileNotFound, $"模板文件不存在: {validTemplate}");
        }

        try
        {
            var sheets = ToolParameterHelper.ParseArray(sheetNames);
            var result = excelService.CreateWorkbook(validOutput, validTemplate,
                sheets,
                ToolParameterHelper.IsEmpty(author) ? null : author);

            return ToolResult.Ok("工作簿创建成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.CreateWorkbookResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建工作簿失败: {Output}", validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"创建工作簿失败: {ex.Message}");
        }
    }

    /// <summary>读取工作簿中的工作表列表。</summary>
    [Tool(Name = "office_excel_list_sheets",
        Description = "读取 xlsx 工作簿中的工作表列表，返回名称、状态和已用区域等信息。include_dimensions 为 1 时计算每个工作表的行列范围。")]
    public string ListSheets(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "是否计算每个工作表的已用区域：0=否，1=是")] int includeDimensions,
        [Parameter(Description = "最大返回工作表数，0 表示返回全部")] int maxSheets)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"文件不存在: {validSource}");

        try
        {
            var result = excelService.ListSheets(validSource, ToolParameterHelper.IsTrue(includeDimensions), maxSheets);
            return ToolResult.Ok("工作表列表读取成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.ListSheetsResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取工作表列表失败: {Source}", validSource);
            return ToolResult.Fail(ErrorCodes.InternalError, $"读取工作表列表失败: {ex.Message}");
        }
    }

    /// <summary>读取指定工作表中的单元格区域。</summary>
    [Tool(Name = "office_excel_read_range",
        Description = "读取指定工作表中的单元格区域。range_ref 使用 A1 表示法（如 A1:C5）。include_formula 为 1 时公式单元格返回 =formula 文本。")]
    public string ReadRange(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "工作表名称")] string sheetName,
        [Parameter(Description = "区域地址，A1 表示法（如 A1:C5 或 A1）")] string rangeRef,
        [Parameter(Description = "是否返回公式文本：0=返回计算值，1=返回公式")] int includeFormula)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"文件不存在: {validSource}");

        if (ToolParameterHelper.IsEmpty(sheetName))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "sheetName 不能为空。");
        if (ToolParameterHelper.IsEmpty(rangeRef))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rangeRef 不能为空。");

        try
        {
            var result = excelService.ReadRange(validSource, sheetName, rangeRef,
                ToolParameterHelper.IsTrue(includeFormula));
            return ToolResult.Ok("区域读取成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.ReadRangeResult));
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
            logger.LogError(ex, "读取区域失败: {Source}", validSource);
            return ToolResult.Fail(ErrorCodes.InternalError, $"读取区域失败: {ex.Message}");
        }
    }

    /// <summary>将工作簿复制保存到新路径。</summary>
    [Tool(Name = "office_excel_save_as",
        Description = "将 xlsx 工作簿复制保存到新路径，不修改内容。source_path 和 output_path 不能相同。")]
    public string SaveAs(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.xlsx）")] string outputPath,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"源文件不存在: {validSource}");

        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。");
        if (string.Equals(validSource, validOutput, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "输出路径不能与源文件相同。");
        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}");

        try
        {
            var result = excelService.SaveAs(validSource, validOutput);
            return ToolResult.Ok("另存为成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.SaveAsResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "另存为失败: {Source} -> {Output}", validSource, validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"另存为失败: {ex.Message}");
        }
    }
}
