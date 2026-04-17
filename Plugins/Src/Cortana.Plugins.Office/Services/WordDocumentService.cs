using System.IO.Compression;
using System.Xml.Linq;
using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Word 文档服务实现。
/// 通过 System.IO.Compression 和 System.Xml.Linq 直接操作 docx 包结构，保持 Native AOT 兼容。
/// </summary>
public sealed partial class WordDocumentService(ILogger<WordDocumentService> logger) : IWordDocumentService
{
    private static readonly XNamespace W = DocxXmlHelper.W;

    /// <inheritdoc/>
    public void CreateDocument(string outputPath, string? templatePath, string? title, string? author)
    {
        EnsureDirectoryExists(outputPath);

        if (templatePath is not null)
        {
            File.Copy(templatePath, outputPath, overwrite: true);
            logger.LogInformation("基于模板创建文档: {Template} -> {Output}", templatePath, outputPath);
            return;
        }

        // 创建最小有效 docx 包
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        DocxXmlHelper.WriteEntryText(archive, "[Content_Types].xml", DocxTemplate.ContentTypes);
        DocxXmlHelper.WriteEntryText(archive, "_rels/.rels", DocxTemplate.Relationships);
        DocxXmlHelper.WriteEntryText(archive, "word/document.xml", DocxTemplate.Document);
        DocxXmlHelper.WriteEntryText(archive, "docProps/core.xml", DocxTemplate.BuildCoreProperties(title, author));

        logger.LogInformation("创建空白文档: {Output}", outputPath);
    }

    /// <inheritdoc/>
    public DocumentOutlineResult GetOutline(string sourcePath, bool includeText, int maxParagraphs)
    {
        using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var doc = DocxXmlHelper.ReadEntryXml(archive, "word/document.xml");
        var body = doc.Root!.Element(W + "body")!;
        var paragraphs = body.Elements(W + "p").ToList();

        int limit = maxParagraphs > 0 ? Math.Min(maxParagraphs, paragraphs.Count) : paragraphs.Count;
        var items = new List<ParagraphInfo>(limit);

        for (int i = 0; i < limit; i++)
        {
            var p = paragraphs[i];
            var text = DocxXmlHelper.GetParagraphText(p);
            items.Add(new ParagraphInfo
            {
                Index = i,
                Style = DocxXmlHelper.GetParagraphStyle(p),
                TextPreview = includeText ? Truncate(text, 200) : Truncate(text, 50),
                IsEmpty = string.IsNullOrWhiteSpace(text)
            });
        }

        int tablesCount = body.Elements(W + "tbl").Count();
        int imagesCount = body.Descendants(W + "drawing").Count();

        logger.LogInformation("读取文档大纲: {Source}, 段落={Count}, 表格={Tables}", sourcePath, items.Count, tablesCount);

        return new DocumentOutlineResult
        {
            Paragraphs = items,
            TablesCount = tablesCount,
            ImagesCount = imagesCount
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

    /// <summary>
    /// 复制源文件到输出路径并以更新模式打开 ZIP 包。
    /// 返回文档 XML、归档和文件流，调用方需负责 dispose。
    /// </summary>
    internal static (XDocument Doc, ZipArchive Archive, FileStream Stream) OpenForEdit(string sourcePath, string outputPath)
    {
        EnsureDirectoryExists(outputPath);
        File.Copy(sourcePath, outputPath, overwrite: true);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite);
        var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        var doc = DocxXmlHelper.ReadEntryXml(archive, "word/document.xml");

        return (doc, archive, stream);
    }

    /// <summary>确保输出文件所在目录存在。</summary>
    private static void EnsureDirectoryExists(string filePath)
    {
        var dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
    }

    /// <summary>截断文本到指定长度。</summary>
    private static string Truncate(string text, int maxLength) =>
        text.Length <= maxLength ? text : string.Concat(text.AsSpan(0, maxLength), "...");
}
