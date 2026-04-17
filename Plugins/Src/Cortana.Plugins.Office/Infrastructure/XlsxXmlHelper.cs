using System.IO.Compression;
using System.Xml.Linq;

namespace Cortana.Plugins.Office.Infrastructure;

/// <summary>
/// xlsx ZIP 包和 SpreadsheetML 元素操作辅助工具。
/// 封装底层的 ZIP 条目读写、单元格地址解析和工作表 XML 构造逻辑。
/// </summary>
internal static class XlsxXmlHelper
{
    /// <summary>SpreadsheetML 主命名空间。</summary>
    public static readonly XNamespace Ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    /// <summary>关系命名空间 (r:)。</summary>
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    // ─── ZIP 条目操作（复用 DocxXmlHelper 的模式） ───

    /// <summary>读取 ZIP 包中指定条目的 XML 文档。</summary>
    public static XDocument ReadEntryXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"工作簿包中缺少必要条目: {entryPath}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>尝试读取 ZIP 包中指定条目的 XML 文档，不存在时返回 null。</summary>
    public static XDocument? TryReadEntryXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>将 XML 文档写回 ZIP 包指定条目（先删后建，避免残留数据）。</summary>
    public static void WriteEntryXml(ZipArchive archive, string entryPath, XDocument doc)
    {
        archive.GetEntry(entryPath)?.Delete();
        var newEntry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var stream = newEntry.Open();
        doc.Save(stream);
    }

    /// <summary>向 ZIP 包写入纯文本条目。</summary>
    public static void WriteEntryText(ZipArchive archive, string entryPath, string content)
    {
        var entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        using var writer = new StreamWriter(entry.Open());
        writer.Write(content);
    }

    // ─── 单元格地址解析 ───

    /// <summary>
    /// 将列字母转换为从 1 开始的列号。例如 A=1, Z=26, AA=27。
    /// </summary>
    public static int ColumnLetterToNumber(string column)
    {
        int result = 0;
        foreach (char c in column.ToUpperInvariant())
        {
            result = result * 26 + (c - 'A' + 1);
        }
        return result;
    }

    /// <summary>
    /// 将从 1 开始的列号转换为列字母。例如 1=A, 26=Z, 27=AA。
    /// </summary>
    public static string ColumnNumberToLetter(int column)
    {
        var result = string.Empty;
        while (column > 0)
        {
            column--;
            result = (char)('A' + column % 26) + result;
            column /= 26;
        }
        return result;
    }

    /// <summary>
    /// 解析单元格地址（如 "B3"），返回 (列号, 行号)，均从 1 开始。
    /// </summary>
    public static (int Column, int Row) ParseCellRef(string cellRef)
    {
        int i = 0;
        while (i < cellRef.Length && char.IsLetter(cellRef[i])) i++;

        if (i == 0 || i == cellRef.Length)
            throw new ArgumentException($"无效的单元格地址: {cellRef}");

        string colPart = cellRef[..i];
        if (!int.TryParse(cellRef[i..], out int row) || row < 1)
            throw new ArgumentException($"无效的单元格地址: {cellRef}");

        return (ColumnLetterToNumber(colPart), row);
    }

    /// <summary>
    /// 构造单元格地址字符串。列号和行号均从 1 开始。
    /// </summary>
    public static string BuildCellRef(int column, int row) =>
        $"{ColumnNumberToLetter(column)}{row}";

    /// <summary>
    /// 解析区域地址（如 "A1:C5"），返回起始和结束的 (列号, 行号)。
    /// 如果是单个单元格（如 "A1"），起始和结束相同。
    /// </summary>
    public static (int StartCol, int StartRow, int EndCol, int EndRow) ParseRangeRef(string rangeRef)
    {
        var parts = rangeRef.Split(':');
        var (sc, sr) = ParseCellRef(parts[0].Trim());

        if (parts.Length == 1)
            return (sc, sr, sc, sr);

        var (ec, er) = ParseCellRef(parts[1].Trim());
        return (Math.Min(sc, ec), Math.Min(sr, er), Math.Max(sc, ec), Math.Max(sr, er));
    }

    // ─── 工作表 XML 操作 ───

    /// <summary>
    /// 从工作表 XML 中获取 sheetData 元素。
    /// </summary>
    public static XElement GetSheetData(XDocument sheetDoc) =>
        sheetDoc.Root!.Element(Ns + "sheetData")
            ?? throw new InvalidOperationException("工作表 XML 缺少 sheetData 元素。");

    /// <summary>
    /// 从 sheetData 中获取指定行号的行元素，不存在时返回 null。
    /// </summary>
    public static XElement? FindRow(XElement sheetData, int rowNumber) =>
        sheetData.Elements(Ns + "row")
            .FirstOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn == rowNumber);

    /// <summary>
    /// 获取或创建指定行号的行元素，按行号顺序插入。
    /// </summary>
    public static XElement GetOrCreateRow(XElement sheetData, int rowNumber)
    {
        var existing = FindRow(sheetData, rowNumber);
        if (existing is not null) return existing;

        var newRow = new XElement(Ns + "row", new XAttribute("r", rowNumber));

        // 按行号顺序插入
        var after = sheetData.Elements(Ns + "row")
            .LastOrDefault(r => int.TryParse(r.Attribute("r")?.Value, out int rn) && rn < rowNumber);

        if (after is not null)
            after.AddAfterSelf(newRow);
        else
            sheetData.AddFirst(newRow);

        return newRow;
    }

    /// <summary>
    /// 获取或创建指定行中的单元格元素。
    /// </summary>
    public static XElement GetOrCreateCell(XElement row, string cellRef)
    {
        var existing = row.Elements(Ns + "c")
            .FirstOrDefault(c => string.Equals(c.Attribute("r")?.Value, cellRef, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) return existing;

        var newCell = new XElement(Ns + "c", new XAttribute("r", cellRef));

        // 按列顺序插入
        var (newCol, _) = ParseCellRef(cellRef);
        var after = row.Elements(Ns + "c")
            .LastOrDefault(c =>
            {
                var r = c.Attribute("r")?.Value;
                if (r is null) return false;
                var (col, _) = ParseCellRef(r);
                return col < newCol;
            });

        if (after is not null)
            after.AddAfterSelf(newCell);
        else
            row.AddFirst(newCell);

        return newCell;
    }

    /// <summary>
    /// 设置单元格的值。根据内容自动判断类型：
    /// 以 = 开头为公式，可解析为数字则写入数值，否则写入内联字符串。
    /// </summary>
    public static void SetCellValue(XElement cell, string? value)
    {
        // 清除旧值
        cell.Element(Ns + "v")?.Remove();
        cell.Element(Ns + "f")?.Remove();
        cell.Element(Ns + "is")?.Remove();
        cell.Attribute("t")?.Remove();

        if (string.IsNullOrEmpty(value))
            return;

        // 公式
        if (value.StartsWith('='))
        {
            cell.Add(new XElement(Ns + "f", value[1..]));
            return;
        }

        // 布尔值
        if (value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            cell.SetAttributeValue("t", "b");
            cell.Add(new XElement(Ns + "v", value.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0"));
            return;
        }

        // 数值
        if (double.TryParse(value, System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture, out _))
        {
            cell.Add(new XElement(Ns + "v", value));
            return;
        }

        // 内联字符串
        cell.SetAttributeValue("t", "inlineStr");
        cell.Add(new XElement(Ns + "is",
            new XElement(Ns + "t",
                new XAttribute(XNamespace.Xml + "space", "preserve"),
                value)));
    }

    /// <summary>
    /// 读取单元格的文本值，支持内联字符串、共享字符串、数值和公式。
    /// </summary>
    /// <param name="cell">单元格元素。</param>
    /// <param name="sharedStrings">共享字符串列表，可为 null。</param>
    /// <param name="includeFormula">为 true 时，公式单元格返回 =formula 而非计算值。</param>
    public static string GetCellValue(XElement cell, string[]? sharedStrings, bool includeFormula)
    {
        var type = cell.Attribute("t")?.Value;

        // 公式优先
        if (includeFormula)
        {
            var formula = cell.Element(Ns + "f")?.Value;
            if (!string.IsNullOrEmpty(formula))
                return "=" + formula;
        }

        // 内联字符串
        if (type == "inlineStr")
        {
            return cell.Element(Ns + "is")?.Element(Ns + "t")?.Value ?? string.Empty;
        }

        var v = cell.Element(Ns + "v")?.Value;
        if (v is null)
            return string.Empty;

        // 共享字符串引用
        if (type == "s" && sharedStrings is not null && int.TryParse(v, out int index) && index < sharedStrings.Length)
        {
            return sharedStrings[index];
        }

        // 布尔值
        if (type == "b")
        {
            return v == "1" ? "true" : "false";
        }

        return v;
    }

    /// <summary>
    /// 从共享字符串表 XML 加载字符串数组。
    /// </summary>
    public static string[] LoadSharedStrings(XDocument sstDoc)
    {
        return sstDoc.Root!.Elements(Ns + "si")
            .Select(si => si.Element(Ns + "t")?.Value ?? string.Concat(si.Descendants(Ns + "t").Select(t => t.Value)))
            .ToArray();
    }

    /// <summary>
    /// 获取工作表中已使用的最大行号和最大列号。
    /// </summary>
    public static (int MaxRow, int MaxCol) GetUsedDimension(XElement sheetData)
    {
        int maxRow = 0;
        int maxCol = 0;

        foreach (var row in sheetData.Elements(Ns + "row"))
        {
            if (int.TryParse(row.Attribute("r")?.Value, out int rn) && rn > maxRow)
                maxRow = rn;

            foreach (var cell in row.Elements(Ns + "c"))
            {
                var cr = cell.Attribute("r")?.Value;
                if (cr is null) continue;
                var (col, _) = ParseCellRef(cr);
                if (col > maxCol)
                    maxCol = col;
            }
        }

        return (maxRow, maxCol);
    }
}
