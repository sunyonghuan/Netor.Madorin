using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Excel 工作表管理工具：新增工作表。
/// </summary>
[Tool]
public sealed class OfficeExcelSheetTools(
    IExcelWorkbookService excelService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeExcelSheetTools> logger)
{
    /// <summary>在工作簿中新增工作表。</summary>
    [Tool(Name = "office_excel_add_sheet",
        Description = "在 xlsx 工作簿中新增工作表。sheet_name 在同一工作簿内必须唯一。position_index 为 0 时追加到末尾，大于 0 时插入到指定位置之前。")]
    public string AddSheet(
        [Parameter(Description = "源文件路径（.xlsx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.xlsx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "新工作表名称")] string sheetName,
        [Parameter(Description = "插入位置：0=追加到末尾，>0 表示插入到该序号之前")] int positionIndex,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (ToolParameterHelper.IsEmpty(sheetName))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "sheetName 不能为空。");

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
            var result = excelService.AddSheet(validSource, validOutput, sheetName, positionIndex);
            return ToolResult.Ok($"工作表 {sheetName} 创建成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.AddSheetResult));
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "新增工作表失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"新增工作表失败: {ex.Message}");
        }
    }
}
