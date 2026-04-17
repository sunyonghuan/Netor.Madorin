namespace Cortana.Plugins.Office.Infrastructure;

/// <summary>
/// 最小有效 xlsx 包所需的 XML 模板。
/// xlsx 文件本质是包含 XML 文件的 ZIP 包（OPC 包），
/// 这里定义创建空白工作簿所需的最小结构。
/// </summary>
internal static class XlsxTemplate
{
    /// <summary>包内容类型定义 [Content_Types].xml。</summary>
    public const string ContentTypes = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types">
          <Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/>
          <Default Extension="xml" ContentType="application/xml"/>
          <Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/>
          <Override PartName="/xl/styles.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml"/>
          <Override PartName="/xl/sharedStrings.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml"/>
          <Override PartName="/docProps/core.xml" ContentType="application/vnd.openxmlformats-package.core-properties+xml"/>
        </Types>
        """;

    /// <summary>包关系定义 _rels/.rels。</summary>
    public const string Relationships = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
          <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/>
          <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties" Target="docProps/core.xml"/>
        </Relationships>
        """;

    /// <summary>最小样式表 xl/styles.xml。</summary>
    public const string Styles = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
          <fonts count="1"><font><sz val="11"/><name val="Calibri"/></font></fonts>
          <fills count="2"><fill><patternFill patternType="none"/></fill><fill><patternFill patternType="gray125"/></fill></fills>
          <borders count="1"><border><left/><right/><top/><bottom/><diagonal/></border></borders>
          <cellStyleXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0"/></cellStyleXfs>
          <cellXfs count="1"><xf numFmtId="0" fontId="0" fillId="0" borderId="0" xfId="0"/></cellXfs>
        </styleSheet>
        """;

    /// <summary>空共享字符串表 xl/sharedStrings.xml。</summary>
    public const string SharedStrings = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" count="0" uniqueCount="0"/>
        """;

    /// <summary>
    /// 生成工作簿主文件 xl/workbook.xml。
    /// </summary>
    /// <param name="sheetEntries">每项为 (name, rId) 元组。</param>
    public static string BuildWorkbook(IReadOnlyList<(string Name, string RId)> sheetEntries)
    {
        var sheets = string.Join("\n    ",
            sheetEntries.Select((s, i) =>
                $"<sheet name=\"{EscapeXml(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"{s.RId}\"/>"));

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                {sheets}
              </sheets>
            </workbook>
            """;
    }

    /// <summary>
    /// 生成工作簿关系文件 xl/_rels/workbook.xml.rels。
    /// </summary>
    /// <param name="sheetEntries">每项为 (rId, target) 元组。</param>
    public static string BuildWorkbookRels(IReadOnlyList<(string RId, string Target)> sheetEntries)
    {
        var rels = string.Join("\n  ",
            sheetEntries.Select(s =>
                $"<Relationship Id=\"{s.RId}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"{s.Target}\"/>"));

        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              {rels}
              <Relationship Id="rIdStyles" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles" Target="styles.xml"/>
              <Relationship Id="rIdSS" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings" Target="sharedStrings.xml"/>
            </Relationships>
            """;
    }

    /// <summary>
    /// 生成空白工作表 XML。
    /// </summary>
    public static string BuildEmptySheet()
    {
        return """
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              <sheetData/>
            </worksheet>
            """;
    }

    /// <summary>
    /// 生成核心属性 XML（docProps/core.xml）。
    /// </summary>
    public static string BuildCoreProperties(string? author)
    {
        var now = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <cp:coreProperties xmlns:cp="http://schemas.openxmlformats.org/package/2006/metadata/core-properties"
                               xmlns:dc="http://purl.org/dc/elements/1.1/"
                               xmlns:dcterms="http://purl.org/dc/terms/"
                               xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <dc:creator>{EscapeXml(author ?? string.Empty)}</dc:creator>
              <dcterms:created xsi:type="dcterms:W3CDTF">{now}</dcterms:created>
              <dcterms:modified xsi:type="dcterms:W3CDTF">{now}</dcterms:modified>
            </cp:coreProperties>
            """;
    }

    /// <summary>
    /// 生成工作表的 Content-Type Override 条目。
    /// </summary>
    public static string BuildSheetContentTypeOverride(int sheetIndex)
    {
        return $"<Override PartName=\"/xl/worksheets/sheet{sheetIndex}.xml\" " +
               "ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>";
    }

    /// <summary>转义 XML 特殊字符，防止注入。</summary>
    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
}
