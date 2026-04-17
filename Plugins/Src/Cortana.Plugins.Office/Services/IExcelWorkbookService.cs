using Cortana.Plugins.Office.Models;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Excel 工作簿服务接口。
/// 提供 xlsx 文件的创建、读取、编辑和另存操作。
/// 实现层直接操作 ZIP 包和 XML，不依赖 Office 进程。
/// </summary>
public interface IExcelWorkbookService
{
    /// <summary>创建空白或基于模板的 xlsx 工作簿。</summary>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="templatePath">模板路径，null 表示创建空白。</param>
    /// <param name="sheetNames">初始工作表名称列表，null 时默认 Sheet1。</param>
    /// <param name="author">作者，null 表示不设置。</param>
    CreateWorkbookResult CreateWorkbook(string outputPath, string? templatePath, string[]? sheetNames, string? author);

    /// <summary>读取工作簿中的工作表列表。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="includeDimensions">是否计算每个工作表的已用区域。</param>
    /// <param name="maxSheets">最大返回数，0 表示全部。</param>
    ListSheetsResult ListSheets(string sourcePath, bool includeDimensions, int maxSheets);

    /// <summary>读取指定工作表中的单元格区域。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="sheetName">工作表名称。</param>
    /// <param name="rangeRef">A1 格式的区域地址。</param>
    /// <param name="includeFormula">公式单元格是否返回公式文本。</param>
    ReadRangeResult ReadRange(string sourcePath, string sheetName, string rangeRef, bool includeFormula);

    /// <summary>从指定起始单元格写入二维数据块。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="sheetName">工作表名称。</param>
    /// <param name="startCell">起始单元格地址（如 A1）。</param>
    /// <param name="values">二维字符串数组，外层为行。</param>
    WriteRangeResult WriteRange(string sourcePath, string outputPath, string sheetName, string startCell, string[][] values);

    /// <summary>在指定行之前插入空行。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="sheetName">工作表名称。</param>
    /// <param name="rowIndex">从 1 开始的行号。</param>
    /// <param name="rowCount">插入行数。</param>
    InsertRowResult InsertRow(string sourcePath, string outputPath, string sheetName, int rowIndex, int rowCount);

    /// <summary>删除指定工作表中的连续行。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="sheetName">工作表名称。</param>
    /// <param name="rowIndex">从 1 开始的行号。</param>
    /// <param name="rowCount">删除行数。</param>
    /// <param name="requireNonEmpty">为 true 时，目标行全部为空则返回错误。</param>
    DeleteRowResult DeleteRow(string sourcePath, string outputPath, string sheetName,
        int rowIndex, int rowCount, bool requireNonEmpty);

    /// <summary>在工作簿中新增工作表。</summary>
    /// <param name="sourcePath">源文件路径。</param>
    /// <param name="outputPath">输出文件路径。</param>
    /// <param name="sheetName">新工作表名称。</param>
    /// <param name="positionIndex">插入位置，0 表示追加到末尾。</param>
    AddSheetResult AddSheet(string sourcePath, string outputPath, string sheetName, int positionIndex);

    /// <summary>复制工作簿到新路径，不做内容修改。</summary>
    SaveAsResult SaveAs(string sourcePath, string outputPath);
}
