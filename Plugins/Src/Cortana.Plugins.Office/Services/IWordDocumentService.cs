using Cortana.Plugins.Office.Models;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// Word 文档服务接口。
/// 提供 docx 文件的创建、读取、编辑和另存操作。
/// 实现层直接操作 ZIP 包和 XML，不依赖 Office 进程。
/// </summary>
public interface IWordDocumentService
{
    /// <summary>创建空白或基于模板的 Word 文档。</summary>
    void CreateDocument(string outputPath, string? templatePath, string? title, string? author);

    /// <summary>读取文档结构摘要，包括段落列表、表格和图片数量。</summary>
    DocumentOutlineResult GetOutline(string sourcePath, bool includeText, int maxParagraphs);

    /// <summary>在指定段落前后插入新段落。</summary>
    InsertParagraphResult InsertParagraph(string sourcePath, string outputPath, int anchorIndex, string position, string text);

    /// <summary>按文本匹配替换文档中的内容。</summary>
    ReplaceTextResult ReplaceText(string sourcePath, string outputPath,
        string searchText, string replaceText, bool matchCase, bool replaceAll, int maxReplaceCount);

    /// <summary>删除指定索引的段落。</summary>
    DeleteParagraphResult DeleteParagraph(string sourcePath, string outputPath, int paragraphIndex, bool requireNonEmpty);

    /// <summary>在指定段落后插入简单表格。</summary>
    InsertTableResult InsertTable(string sourcePath, string outputPath,
        int anchorIndex, int rows, int columns, string[]? headers, string[]? cells);

    /// <summary>复制文档到新路径，不做内容修改。</summary>
    SaveAsResult SaveAs(string sourcePath, string outputPath);
}
