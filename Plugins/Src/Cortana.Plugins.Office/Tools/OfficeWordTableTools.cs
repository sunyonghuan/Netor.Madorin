using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Word 表格操作工具。
/// </summary>
[Tool]
public sealed class OfficeWordTableTools(
    IWordDocumentService wordService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeWordTableTools> logger)
{
    /// <summary>在指定段落后插入简单表格。</summary>
    [Tool(Name = "office_word_insert_table",
        Description = "在指定段落后插入简单表格。headers 为 JSON 数组或逗号分隔的表头文本；cells 为 JSON 数组或逗号分隔的单元格内容，按行优先展开。")]
    public string InsertTable(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.docx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "锚点段落索引（从 0 开始），表格插入在此段落之后")] int anchorIndex,
        [Parameter(Description = "行数（1-100）")] int rows,
        [Parameter(Description = "列数（1-100）")] int columns,
        [Parameter(Description = "表头文本，JSON 数组或逗号分隔，空字符串表示无表头")] string headers,
        [Parameter(Description = "单元格内容，JSON 数组或逗号分隔，按行优先展开，空字符串表示全部为空")] string cells,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (rows < 1 || rows > 100 || columns < 1 || columns > 100)
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "rows 和 columns 必须在 1-100 之间。");

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
            var headerArray = ToolParameterHelper.ParseArray(headers);
            var cellArray = ToolParameterHelper.ParseArray(cells);

            var result = wordService.InsertTable(validSource, validOutput, anchorIndex, rows, columns, headerArray, cellArray);
            return ToolResult.Ok($"表格插入成功，{rows} 行 {columns} 列。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.InsertTableResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "插入表格失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"插入表格失败: {ex.Message}");
        }
    }
}
