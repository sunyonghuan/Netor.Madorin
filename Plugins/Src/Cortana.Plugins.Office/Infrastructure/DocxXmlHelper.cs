using System.IO.Compression;
using System.Xml.Linq;

namespace Cortana.Plugins.Office.Infrastructure;

/// <summary>
/// docx ZIP 包和 Open XML 元素操作辅助工具。
/// 封装底层的 ZIP 条目读写和 Word XML 元素构造逻辑。
/// </summary>
internal static class DocxXmlHelper
{
    /// <summary>Word 主命名空间 (w:)。</summary>
    public static readonly XNamespace W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    /// <summary>读取 ZIP 包中指定条目的 XML 文档。</summary>
    public static XDocument ReadEntryXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"文档包中缺少必要条目: {entryPath}");
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

    /// <summary>提取段落的全部文本内容（合并所有 Run 中的文本）。</summary>
    public static string GetParagraphText(XElement paragraph)
    {
        var texts = paragraph.Descendants(W + "t");
        return string.Concat(texts.Select(t => t.Value));
    }

    /// <summary>获取段落样式名称，无样式时返回 null。</summary>
    public static string? GetParagraphStyle(XElement paragraph)
    {
        return paragraph
            .Element(W + "pPr")?
            .Element(W + "pStyle")?
            .Attribute(W + "val")?.Value;
    }

    /// <summary>创建包含纯文本的段落元素。</summary>
    public static XElement CreateParagraph(string text)
    {
        return new XElement(W + "p",
            new XElement(W + "r",
                new XElement(W + "t",
                    new XAttribute(XNamespace.Xml + "space", "preserve"),
                    text)));
    }

    /// <summary>
    /// 创建简单表格元素。
    /// headers 用于首行表头，cells 按行优先顺序填充剩余行。
    /// </summary>
    public static XElement CreateTable(int rows, int columns, string[]? headers, string[]? cells)
    {
        var tblBorders = new XElement(W + "tblBorders",
            CreateBorder("top"), CreateBorder("left"),
            CreateBorder("bottom"), CreateBorder("right"),
            CreateBorder("insideH"), CreateBorder("insideV"));

        var table = new XElement(W + "tbl",
            new XElement(W + "tblPr",
                new XElement(W + "tblW",
                    new XAttribute(W + "w", "0"),
                    new XAttribute(W + "type", "auto")),
                tblBorders));

        int startRow = 0;
        if (headers is { Length: > 0 })
        {
            table.Add(CreateTableRow(columns, headers));
            startRow = 1;
        }

        int cellIndex = 0;
        for (int r = startRow; r < rows; r++)
        {
            var rowCells = new string[columns];
            for (int c = 0; c < columns; c++)
            {
                rowCells[c] = cells is not null && cellIndex < cells.Length
                    ? cells[cellIndex++]
                    : string.Empty;
            }
            table.Add(CreateTableRow(columns, rowCells));
        }

        return table;
    }

    /// <summary>创建表格行元素。</summary>
    private static XElement CreateTableRow(int columns, string[] cellValues)
    {
        var row = new XElement(W + "tr");
        for (int c = 0; c < columns; c++)
        {
            var text = c < cellValues.Length ? cellValues[c] : string.Empty;
            row.Add(new XElement(W + "tc",
                new XElement(W + "p",
                    new XElement(W + "r",
                        new XElement(W + "t",
                            new XAttribute(XNamespace.Xml + "space", "preserve"),
                            text)))));
        }
        return row;
    }

    /// <summary>创建表格边框子元素。</summary>
    private static XElement CreateBorder(string name)
    {
        return new XElement(W + name,
            new XAttribute(W + "val", "single"),
            new XAttribute(W + "sz", "4"),
            new XAttribute(W + "space", "0"),
            new XAttribute(W + "color", "auto"));
    }
}
