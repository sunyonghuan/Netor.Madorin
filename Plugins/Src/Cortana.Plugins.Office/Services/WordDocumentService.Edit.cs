using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Word 文档服务实现（编辑操作部分）：段落插入/删除、文本替换、表格插入。
/// </summary>
public sealed partial class WordDocumentService
{
    /// <inheritdoc/>
    public InsertParagraphResult InsertParagraph(
        string sourcePath, string outputPath, int anchorIndex, string position, string text)
    {
        var (doc, archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var body = doc.Root!.Element(W + "body")!;
            var paragraphs = body.Elements(W + "p").ToList();

            if (anchorIndex < 0 || anchorIndex >= paragraphs.Count)
                throw new ArgumentOutOfRangeException(nameof(anchorIndex),
                    $"段落索引 {anchorIndex} 超出范围 [0, {paragraphs.Count - 1}]");

            var newParagraph = DocxXmlHelper.CreateParagraph(text);
            var anchor = paragraphs[anchorIndex];

            if (position == "before")
                anchor.AddBeforeSelf(newParagraph);
            else
                anchor.AddAfterSelf(newParagraph);

            DocxXmlHelper.WriteEntryXml(archive, "word/document.xml", doc);

            int insertedIndex = position == "before" ? anchorIndex : anchorIndex + 1;
            logger.LogInformation("插入段落: 索引={Index}, 位置={Position}", insertedIndex, position);

            return new InsertParagraphResult
            {
                InsertedIndex = insertedIndex,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public ReplaceTextResult ReplaceText(
        string sourcePath, string outputPath, string searchText, string replaceText,
        bool matchCase, bool replaceAll, int maxReplaceCount)
    {
        int replacedCount = 0;
        string? sampleBefore = null;
        string? sampleAfter = null;

        var (doc, archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var body = doc.Root!.Element(W + "body")!;
            var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int limit = maxReplaceCount > 0 ? maxReplaceCount : int.MaxValue;

            // 遍历所有文本节点执行替换（V1 仅支持单 Run 内匹配）
            foreach (var t in body.Descendants(W + "t"))
            {
                if (replacedCount >= limit) break;
                if (!replaceAll && replacedCount >= 1) break;

                var original = t.Value;
                if (!original.Contains(searchText, comparison)) continue;

                sampleBefore ??= original;

                var replaced = original.Replace(searchText, replaceText, comparison);
                sampleAfter ??= replaced;
                t.Value = replaced;
                replacedCount++;
            }

            if (replacedCount > 0)
                DocxXmlHelper.WriteEntryXml(archive, "word/document.xml", doc);
        }

        // 无匹配时清理未修改的输出文件
        if (sampleBefore is null)
        {
            try { File.Delete(outputPath); } catch { /* 尽力清理 */ }
        }

        logger.LogInformation("文本替换: '{Search}' -> '{Replace}', 替换数={Count}", searchText, replaceText, replacedCount);

        return new ReplaceTextResult
        {
            ReplacedCount = replacedCount,
            SampleBefore = sampleBefore,
            SampleAfter = sampleAfter,
            OutputPath = outputPath
        };
    }

    /// <inheritdoc/>
    public DeleteParagraphResult DeleteParagraph(
        string sourcePath, string outputPath, int paragraphIndex, bool requireNonEmpty)
    {
        var (doc, archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var body = doc.Root!.Element(W + "body")!;
            var paragraphs = body.Elements(W + "p").ToList();

            if (paragraphIndex < 0 || paragraphIndex >= paragraphs.Count)
                throw new ArgumentOutOfRangeException(nameof(paragraphIndex),
                    $"段落索引 {paragraphIndex} 超出范围 [0, {paragraphs.Count - 1}]");

            var target = paragraphs[paragraphIndex];

            if (requireNonEmpty && string.IsNullOrWhiteSpace(DocxXmlHelper.GetParagraphText(target)))
                throw new InvalidOperationException("目标段落为空，requireNonEmpty 模式下不允许删除空段落。");

            target.Remove();
            DocxXmlHelper.WriteEntryXml(archive, "word/document.xml", doc);

            logger.LogInformation("删除段落: 索引={Index}", paragraphIndex);

            return new DeleteParagraphResult
            {
                DeletedIndex = paragraphIndex,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public InsertTableResult InsertTable(
        string sourcePath, string outputPath, int anchorIndex,
        int rows, int columns, string[]? headers, string[]? cells)
    {
        var (doc, archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var body = doc.Root!.Element(W + "body")!;
            var paragraphs = body.Elements(W + "p").ToList();

            if (anchorIndex < 0 || anchorIndex >= paragraphs.Count)
                throw new ArgumentOutOfRangeException(nameof(anchorIndex),
                    $"段落索引 {anchorIndex} 超出范围 [0, {paragraphs.Count - 1}]");

            var table = DocxXmlHelper.CreateTable(rows, columns, headers, cells);
            paragraphs[anchorIndex].AddAfterSelf(table);

            DocxXmlHelper.WriteEntryXml(archive, "word/document.xml", doc);

            int tableIndex = body.Elements(W + "tbl").ToList().IndexOf(table);
            logger.LogInformation("插入表格: {Rows}x{Cols}, 锚点={Anchor}", rows, columns, anchorIndex);

            return new InsertTableResult
            {
                TableIndex = tableIndex,
                Rows = rows,
                Columns = columns,
                OutputPath = outputPath
            };
        }
    }
}
