namespace Cortana.Plugins.Office.Infrastructure;

/// <summary>
/// 最小有效 docx 包所需的 XML 模板。
/// docx 文件本质是包含 XML 文件的 ZIP 包，这里定义创建空白文档所需的最小结构。
/// </summary>
internal static class DocxTemplate
{
    /// <summary>包内容类型定义 [Content_Types].xml。</summary>
    public const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/word/document.xml" ContentType="application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
        </Types>
        """;

    /// <summary>包关系定义 _rels/.rels。</summary>
    public const string Relationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="word/document.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
        </Relationships>
        """;

    /// <summary>主文档内容 word/document.xml。</summary>
    public const string Document = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <w:document xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"
                    xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
          <w:body>
            <w:sectPr/>
          </w:body>
        </w:document>
        """;

    /// <summary>
    /// 生成核心属性 XML（docProps/core.xml），支持自定义标题和作者。
    /// </summary>
    public static string BuildCoreProperties(string? title, string? author)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                               xmlns:dc="http://purl.org/dc/elements/1.1/"
                               xmlns:dcterms="http://purl.org/dc/terms/"
                               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:title>{EscapeXml(title ?? string.Empty)}</dc:title>
              <dc:creator>{EscapeXml(author ?? string.Empty)}</dc:creator>
              <dcterms:created xsi:type="dcterms:W3CDTF">{now}</dcterms:created>
              <dcterms:modified xsi:type="dcterms:W3CDTF">{now}</dcterms:modified>
            </cp:coreProperties>
            """;
    }

    /// <summary>转义 XML 特殊字符，防止注入。</summary>
    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
