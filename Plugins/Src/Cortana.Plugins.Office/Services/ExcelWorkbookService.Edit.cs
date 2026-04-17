using System.Xml.Linq;
using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Excel 工作簿服务实现（编辑操作部分）：区域写入、行插入/删除、新增工作表。
/// </summary>
public sealed partial class ExcelWorkbookService
{
    /// <inheritdoc/>
    public WriteRangeResult WriteRange(
        string sourcePath, string outputPath, string sheetName, string startCell, string[][] values)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            string sheetPath = ResolveSheetPath(archive, sheetName);
            var sheetDoc = XlsxXmlHelper.ReadEntryXml(archive, sheetPath);
            var sheetData = XlsxXmlHelper.GetSheetData(sheetDoc);

            var (startCol, startRow) = XlsxXmlHelper.ParseCellRef(startCell);
            int changedCount = 0;
            int maxCol = startCol;

            for (int r = 0; r < values.Length; r++)
            {
                int currentRow = startRow + r;
                var rowElement = XlsxXmlHelper.GetOrCreateRow(sheetData, currentRow);

                for (int c = 0; c < values[r].Length; c++)
                {
                    int currentCol = startCol + c;
                    string cellRef = XlsxXmlHelper.BuildCellRef(currentCol, currentRow);
                    var cell = XlsxXmlHelper.GetOrCreateCell(rowElement, cellRef);

                    XlsxXmlHelper.SetCellValue(cell, values[r][c]);
                    changedCount++;

                    if (currentCol > maxCol)
                        maxCol = currentCol;
                }
            }

            XlsxXmlHelper.WriteEntryXml(archive, sheetPath, sheetDoc);

            int endRow = startRow + values.Length - 1;
            string endCell = XlsxXmlHelper.BuildCellRef(maxCol, endRow);

            logger.LogInformation("写入区域: {Sheet}!{Start}:{End}, 单元格数={Count}",
                sheetName, startCell, endCell, changedCount);

            return new WriteRangeResult
            {
                SheetName = sheetName,
                StartCell = startCell.ToUpperInvariant(),
                EndCell = endCell,
                ChangedCount = changedCount,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public InsertRowResult InsertRow(
        string sourcePath, string outputPath, string sheetName, int rowIndex, int rowCount)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            string sheetPath = ResolveSheetPath(archive, sheetName);
            var sheetDoc = XlsxXmlHelper.ReadEntryXml(archive, sheetPath);
            var sheetData = XlsxXmlHelper.GetSheetData(sheetDoc);

            // 将 rowIndex 及之后的行号全部后移 rowCount
            ShiftRowsDown(sheetData, rowIndex, rowCount);

            XlsxXmlHelper.WriteEntryXml(archive, sheetPath, sheetDoc);

            logger.LogInformation("插入行: {Sheet}, 位置={Row}, 数量={Count}", sheetName, rowIndex, rowCount);

            return new InsertRowResult
            {
                SheetName = sheetName,
                InsertedAt = rowIndex,
                RowCount = rowCount,
                ChangedCount = rowCount,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public DeleteRowResult DeleteRow(
        string sourcePath, string outputPath, string sheetName,
        int rowIndex, int rowCount, bool requireNonEmpty)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            string sheetPath = ResolveSheetPath(archive, sheetName);
            var sheetDoc = XlsxXmlHelper.ReadEntryXml(archive, sheetPath);
            var sheetData = XlsxXmlHelper.GetSheetData(sheetDoc);

            int endRow = rowIndex + rowCount - 1;

            // 检查非空约束
            if (requireNonEmpty)
            {
                bool allEmpty = true;
                for (int r = rowIndex; r <= endRow; r++)
                {
                    var row = XlsxXmlHelper.FindRow(sheetData, r);
                    if (row is not null && row.Elements(Ns + "c").Any(c => !IsEmptyCell(c)))
                    {
                        allEmpty = false;
                        break;
                    }
                }

                if (allEmpty)
                    throw new InvalidOperationException($"目标行 {rowIndex}-{endRow} 全部为空，requireNonEmpty 模式下不允许删除。");
            }

            // 删除目标范围内的行元素
            var toRemove = sheetData.Elements(Ns + "row")
                .Where(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn >= rowIndex && rn <= endRow)
                .ToList();

            foreach (var row in toRemove)
                row.Remove();

            // 将 endRow+1 及之后的行号全部前移 rowCount
            ShiftRowsUp(sheetData, endRow + 1, rowCount);

            XlsxXmlHelper.WriteEntryXml(archive, sheetPath, sheetDoc);

            logger.LogInformation("删除行: {Sheet}, 位置={Row}, 数量={Count}", sheetName, rowIndex, rowCount);

            return new DeleteRowResult
            {
                SheetName = sheetName,
                DeletedAt = rowIndex,
                RowCount = rowCount,
                ChangedCount = rowCount,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public AddSheetResult AddSheet(
        string sourcePath, string outputPath, string sheetName, int positionIndex)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            // 读取现有工作簿信息
            var wbDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/workbook.xml");
            var relsDoc = XlsxXmlHelper.ReadEntryXml(archive, "xl/_rels/workbook.xml.rels");
            var ctDoc = XlsxXmlHelper.ReadEntryXml(archive, "[Content_Types].xml");

            var sheetsElement = wbDoc.Root!.Element(Ns + "sheets")!;
            var existingSheets = sheetsElement.Elements(Ns + "sheet").ToList();

            // 检查名称重复
            if (existingSheets.Any(s => string.Equals(s.Attribute("name")?.Value, sheetName, StringComparison.Ordinal)))
                throw new ArgumentException($"工作表名称已存在: {sheetName}");

            // 确定新工作表编号
            int newSheetId = existingSheets
                .Select(s => int.TryParse(s.Attribute("sheetId")?.Value, out int id) ? id : 0)
                .DefaultIfEmpty(0)
                .Max() + 1;

            int newIndex = newSheetId;
            string newRId = $"rId{newIndex}";
            // 确保 rId 不重复
            var relsNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/relationships");
            var existingRIds = relsDoc.Root!.Elements(relsNs + "Relationship")
                .Select(r => r.Attribute("Id")?.Value)
                .ToHashSet();
            while (existingRIds.Contains(newRId))
            {
                newIndex++;
                newRId = $"rId{newIndex}";
            }

            string target = $"worksheets/sheet{newSheetId}.xml";

            // 创建空白工作表文件
            XlsxXmlHelper.WriteEntryText(archive, $"xl/{target}", XlsxTemplate.BuildEmptySheet());

            // 更新 workbook.xml
            var newSheet = new XElement(Ns + "sheet",
                new XAttribute("name", sheetName),
                new XAttribute("sheetId", newSheetId),
                new XAttribute(XlsxXmlHelper.R + "id", newRId));

            int insertAt = positionIndex > 0 && positionIndex <= existingSheets.Count
                ? positionIndex - 1
                : existingSheets.Count;

            if (insertAt < existingSheets.Count)
                existingSheets[insertAt].AddBeforeSelf(newSheet);
            else
                sheetsElement.Add(newSheet);

            XlsxXmlHelper.WriteEntryXml(archive, "xl/workbook.xml", wbDoc);

            // 更新 workbook.xml.rels
            relsDoc.Root!.Add(new XElement(relsNs + "Relationship",
                new XAttribute("Id", newRId),
                new XAttribute("Type", "http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet"),
                new XAttribute("Target", target)));
            XlsxXmlHelper.WriteEntryXml(archive, "xl/_rels/workbook.xml.rels", relsDoc);

            // 更新 [Content_Types].xml
            var ctNs = XNamespace.Get("http://schemas.openxmlformats.org/package/2006/content-types");
            ctDoc.Root!.Add(new XElement(ctNs + "Override",
                new XAttribute("PartName", $"/xl/{target}"),
                new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml")));
            XlsxXmlHelper.WriteEntryXml(archive, "[Content_Types].xml", ctDoc);

            int totalSheets = existingSheets.Count + 1;
            int finalPosition = insertAt + 1;

            logger.LogInformation("新增工作表: {Name}, 位置={Pos}, 总数={Total}", sheetName, finalPosition, totalSheets);

            return new AddSheetResult
            {
                SheetName = sheetName,
                PositionIndex = finalPosition,
                SheetCount = totalSheets,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    // ─── 行操作辅助方法 ───

    /// <summary>将指定行号及之后的所有行向下移动 offset 行。</summary>
    private static void ShiftRowsDown(XElement sheetData, int fromRow, int offset)
    {
        var rows = sheetData.Elements(XlsxXmlHelper.Ns + "row")
            .Where(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn >= fromRow)
            .OrderByDescending(r => int.Parse(r.Attribute("r")!.Value))
            .ToList();

        foreach (var row in rows)
        {
            int oldRowNum = int.Parse(row.Attribute("r")!.Value);
            int newRowNum = oldRowNum + offset;
            row.SetAttributeValue("r", newRowNum);

            // 更新行内所有单元格引用
            UpdateCellRefsInRow(row, oldRowNum, newRowNum);
        }
    }

    /// <summary>将指定行号及之后的所有行向上移动 offset 行。</summary>
    private static void ShiftRowsUp(XElement sheetData, int fromRow, int offset)
    {
        var rows = sheetData.Elements(XlsxXmlHelper.Ns + "row")
            .Where(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn >= fromRow)
            .OrderBy(r => int.Parse(r.Attribute("r")!.Value))
            .ToList();

        foreach (var row in rows)
        {
            int oldRowNum = int.Parse(row.Attribute("r")!.Value);
            int newRowNum = oldRowNum - offset;
            row.SetAttributeValue("r", newRowNum);

            UpdateCellRefsInRow(row, oldRowNum, newRowNum);
        }
    }

    /// <summary>更新行内所有单元格的行号引用。</summary>
    private static void UpdateCellRefsInRow(XElement row, int oldRowNum, int newRowNum)
    {
        foreach (var cell in row.Elements(XlsxXmlHelper.Ns + "c"))
        {
            var refAttr = cell.Attribute("r");
            if (refAttr?.Value is null) continue;

            // 将 "B5" 中的行号 5 替换为新行号
            var cellRef = refAttr.Value;
            int i = 0;
            while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;

            string colPart = cellRef[..i];
            refAttr.Value = $"{colPart}{newRowNum}";
        }
    }

    /// <summary>判断单元格是否为空。</summary>
    private static bool IsEmptyCell(XElement cell)
    {
        // 无值元素且无公式视为空
        var v = cell.Element(Ns + "v");
        var f = cell.Element(Ns + "f");
        var inlineStr = cell.Element(Ns + "is");

        if (f is not null) return false;
        if (inlineStr is not null) return string.IsNullOrEmpty(inlineStr.Element(Ns + "t")?.Value);
        if (v is null) return true;
        return string.IsNullOrEmpty(v.Value);
    }
}
