using System.IO.Compression;
using System.Xml.Linq;
using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Excel 工作簿服务实现。
/// 通过 System.IO.Compression 和 System.Xml.Linq 直接操作 xlsx 包结构，保持 Native AOT 兼容。
/// </summary>
public sealed partial class ExcelWorkbookService(ILogger<ExcelWorkbookService> logger) : IExcelWorkbookService
{
    private static readonly XNamespace Ns = XlsxXmlHelper.Ns;

    /// <inheritdoc/>
    public CreateWorkbookResult CreateWorkbook(
        string outputPath, string? templatePath, string[]? sheetNames, string? author)
    {
        EnsureDirectoryExists(outputPath);

        if (templatePath is not null)
        {
            File.Copy(templatePath, outputPath, overwrite: true);
            // 统计模板中的工作表数
            int count = CountSheets(outputPath);
            logger.LogInformation("基于模板创建工作簿: {Template} -> {Output}", templatePath, outputPath);

            return new CreateWorkbookResult
            {
                WorkbookPath = outputPath,
                SheetCount = count,
                CreatedFromTemplate = true,
                OutputPath = outputPath
            };
        }

        // 确定初始工作表
        var sheets = sheetNames is { Length: > 0 } ? sheetNames : ["Sheet1"];

        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        // 构建工作表条目
        var sheetEntries = new List<(string Name, string RId)>();
        var relEntries = new List<(string RId, string Target)>();
        var contentTypeOverrides = new List<string>();

        for (int i = 0; i < sheets.Length; i++)
        {
            string rId = $"rId{i + 1}";
            string target = $"worksheets/sheet{i + 1}.xml";
            sheetEntries.Add((sheets[i], rId));
            relEntries.Add((rId, target));
            contentTypeOverrides.Add(XlsxTemplate.BuildSheetContentTypeOverride(i + 1));

            XlsxXmlHelper.WriteEntryText(archive, $"xl/{target}", XlsxTemplate.BuildEmptySheet());
        }

        // Content_Types 需要追加工作表的 Override
        var contentTypes = XlsxTemplate.ContentTypes.TrimEnd();
        var insertPos = contentTypes.LastIndexOf("</Types>", StringComparison.Ordinal);
        var updatedContentTypes = string.Concat(
            contentTypes.AsSpan(0, insertPos),
            string.Join("\n  ", contentTypeOverrides),
            "\n",
            contentTypes.AsSpan(insertPos));

        XlsxXmlHelper.WriteEntryText(archive, "[Content_Types].xml", updatedContentTypes);
        XlsxXmlHelper.WriteEntryText(archive, "_rels/.rels", XlsxTemplate.Relationships);
        XlsxXmlHelper.WriteEntryText(archive, "xl/workbook.xml", XlsxTemplate.BuildWorkbook(sheetEntries));
        XlsxXmlHelper.WriteEntryText(archive, "xl/_rels/workbook.xml.rels", XlsxTemplate.BuildWorkbookRels(relEntries));
        XlsxXmlHelper.WriteEntryText(archive, "xl/styles.xml", XlsxTemplate.Styles);
        XlsxXmlHelper.WriteEntryText(archive, "xl/sharedStrings.xml", XlsxTemplate.SharedStrings);
        XlsxXmlHelper.WriteEntryText(archive, "docProps/core.xml", XlsxTemplate.BuildCoreProperties(author));

        logger.LogInformation("创建空白工作簿: {Output}, 工作表数={Count}", outputPath, sheets.Length);

        return new CreateWorkbookResult
        {
            WorkbookPath = outputPath,
            SheetCount = sheets.Length,
            CreatedFromTemplate = false,
            OutputPath = outputPath
        };
    }

    /// <inheritdoc/>
    public ListSheetsResult ListSheets(string sourcePath, bool includeDimensions, int maxSheets)
    {
        using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var (sheetInfos, activeSheet) = ReadSheetList(archive, includeDimensions, maxSheets);

        logger.LogInformation("读取工作簿: {Source}, 工作表数={Count}", sourcePath, sheetInfos.Count);

        return new ListSheetsResult
        {
            Sheets = sheetInfos,
            ActiveSheet = activeSheet,
            SheetCount = sheetInfos.Count
        };
    }

    /// <inheritdoc/>
    public ReadRangeResult ReadRange(string sourcePath, string sheetName, string rangeRef, bool includeFormula)
    {
        using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        string sheetPath = ResolveSheetPath(archive, sheetName);
        var sheetDoc = XlsxXmlHelper.ReadEntryXml(archive, sheetPath);
        var sheetData = XlsxXmlHelper.GetSheetData(sheetDoc);

        // 加载共享字符串
        var sstDoc = XlsxXmlHelper.TryReadEntryXml(archive, "xl/sharedStrings.xml");
        string[]? sharedStrings = sstDoc is not null ? XlsxXmlHelper.LoadSharedStrings(sstDoc) : null;

        var (startCol, startRow, endCol, endRow) = XlsxXmlHelper.ParseRangeRef(rangeRef);
        int totalRows = endRow - startRow + 1;
        int totalCols = endCol - startCol + 1;

        var values = new string[totalRows][];
        for (int r = 0; r < totalRows; r++)
        {
            values[r] = new string[totalCols];
            var row = XlsxXmlHelper.FindRow(sheetData, startRow + r);
            for (int c = 0; c < totalCols; c++)
            {
                string cellRef = XlsxXmlHelper.BuildCellRef(startCol + c, startRow + r);
                var cell = row?.Elements(Ns + "c")
                    .FirstOrDefault(x => string.Equals(x.Attribute("r")?.Value, cellRef, StringComparison.OrdinalIgnoreCase));

                values[r][c] = cell is not null
                    ? XlsxXmlHelper.GetCellValue(cell, sharedStrings, includeFormula)
                    : string.Empty;
            }
        }

        string normalizedRange = $"{XlsxXmlHelper.BuildCellRef(startCol, startRow)}:{XlsxXmlHelper.BuildCellRef(endCol, endRow)}";
        logger.LogInformation("读取区域: {Sheet}!{Range}, 行={Rows}, 列={Cols}",
            sheetName, normalizedRange, totalRows, totalCols);

        return new ReadRangeResult
        {
            SheetName = sheetName,
            RangeRef = normalizedRange,
            Rows = totalRows,
            Columns = totalCols,
            Values = values
        };
    }

    /// <inheritdoc/>
    public SaveAsResult SaveAs(string sourcePath, string outputPath)
    {
        EnsureDirectoryExists(outputPath);
        File.Copy(sourcePath, outputPath, overwrite: true);
        var fileSize = new FileInfo(outputPath).Length;

        logger.LogInformation("另存为: {Source} -> {Output}, 大小={Size}", sourcePath, outputPath, fileSize);

        return new SaveAsResult
        {
            OutputPath = outputPath,
            FileSize = fileSize,
            ChangedCount = 0
        };
    }

    // ─── 内部辅助方法 ───

    /// <summary>
    /// 复制源文件到输出路径并以更新模式打开 ZIP 包。
    /// 返回归档和文件流，调用方需负责 dispose。
    /// </summary>
    internal static (ZipArchive Archive, FileStream Stream) OpenForEdit(string sourcePath, string outputPath)
    {
        EnsureDirectoryExists(outputPath);
        File.Copy(sourcePath, outputPath, overwrite: true);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite);
        var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);

        return (archive, stream);
    }

    /// <summary>根据工作表名称解析其在 ZIP 包内的路径。</summary>
    internal static string ResolveSheetPath(ZipArchive archive, string sheetName)
    {
        var wbDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/workbook.xml");
        var ns = XlsxXmlHelper.Ns;
        var rNs = XlsxXmlHelper.R;

        var sheet = wbDoc.Root!
            .Element(ns + "sheets")?
            .Elements(ns + "sheet")
            .FirstOrDefault(s => string.Equals(s.Attribute("name")?.Value, sheetName, StringComparison.Ordinal))
            ?? throw new KeyNotFoundException($"工作表不存在: {sheetName}");

        var rId = sheet.Attribute(rNs + "id")?.Value
            ?? throw new InvalidOperationException($"工作表 {sheetName} 缺少关系 ID。");

        var relsDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/_rels/workbook.xml.rels");
        var relsNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");

        var target = relsDoc.Root!
            .Elements(relsNs + "Relationship")
            .FirstOrDefault(r => r.Attribute("Id")?.Value == rId)?
            .Attribute("Target")?.Value
            ?? throw new InvalidOperationException($"找不到关系 {rId} 对应的工作表文件。");

        // target 可能是相对路径如 "worksheets/sheet1.xml"
        return target.StartsWith("xl/", StringComparison.OrdinalIgnoreCase)
            ? target
            : $"xl/{target}";
    }

    /// <summary>从已打开的归档中读取工作表列表。</summary>
    private static (List<SheetInfo> Sheets, string? ActiveSheet) ReadSheetList(
        ZipArchive archive, bool includeDimensions, int maxSheets)
    {
        var wbDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/workbook.xml");
        var ns = XlsxXmlHelper.Ns;

        var sheetElements = wbDoc.Root!
            .Element(ns + "sheets")?
            .Elements(ns + "sheet")
            .ToList() ?? [];

        int limit = maxSheets > 0 ? Math.Min(maxSheets, sheetElements.Count) : sheetElements.Count;
        var result = new List<SheetInfo>(limit);
        string? activeSheet = null;

        for (int i = 0; i < limit; i++)
        {
            var el = sheetElements[i];
            string name = el.Attribute("name")?.Value ?? $"Sheet{i + 1}";
            string state = el.Attribute("state")?.Value ?? "visible";

            activeSheet ??= name; // 第一个工作表默认为活动

            int rowCount = 0;
            int colCount = 0;
            string? usedRange = null;

            if (includeDimensions)
            {
                try
                {
                    string path = ResolveSheetPath(archive, name);
                    var sheetDoc = XlsxXmlHelper.ReadEntryXml(archive, path);
                    var sheetData = XlsxXmlHelper.GetSheetData(sheetDoc);
                    var (maxRow, maxCol) = XlsxXmlHelper.GetUsedDimension(sheetData);
                    rowCount = maxRow;
                    colCount = maxCol;
                    if (maxRow > 0 && maxCol > 0)
                        usedRange = $"A1:{XlsxXmlHelper.BuildCellRef(maxCol, maxRow)}";
                }
                catch
                {
                    // 无法解析维度时忽略
                }
            }

            result.Add(new SheetInfo
            {
                Index = i,
                Name = name,
                State = state,
                UsedRange = usedRange,
                RowCount = rowCount,
                ColumnCount = colCount
            });
        }

        return (result, activeSheet);
    }

    /// <summary>统计工作簿中的工作表数量。</summary>
    private static int CountSheets(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var wbDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/workbook.xml");
        return wbDoc.Root!
            .Element(XlsxXmlHelper.Ns + "sheets")?
            .Elements(XlsxXmlHelper.Ns + "sheet")
            .Count() ?? 0;
    }

    /// <summary>确保输出文件所在目录存在。</summary>
    private static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }
}
