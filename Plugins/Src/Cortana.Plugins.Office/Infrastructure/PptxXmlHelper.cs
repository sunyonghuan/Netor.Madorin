using System.IO.Compression;
using System.Xml.Linq;

namespace Cortana.Plugins.Office.Infrastructure;

/// <summary>
/// pptx ZIP 包和 PresentationML 元素操作辅助工具。
/// 封装底层的 ZIP 条目读写、占位符查找和幻灯片 XML 构造逻辑。
/// </summary>
internal static class PptxXmlHelper
{
    /// <summary>PresentationML 命名空间 (p:)。</summary>
    public static readonly XNamespace P = "http://schemas.openxmlformats.org/presentationml/2006/main";

    /// <summary>DrawingML 命名空间 (a:)。</summary>
    public static readonly XNamespace A = "http://schemas.openxmlformats.org/drawingml/2006/main";

    /// <summary>关系命名空间 (r:)。</summary>
    public static readonly XNamespace R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    /// <summary>OPC 关系命名空间。</summary>
    public static readonly XNamespace Rels = "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>OPC 内容类型命名空间。</summary>
    public static readonly XNamespace CT = "http://schemas.openxmlformats.org/package/2006/content-types";

    // ─── 关系类型常量 ───

    public const string RelTypeSlide = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide";
    public const string RelTypeSlideMaster = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideMaster";
    public const string RelTypeSlideLayout = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/slideLayout";
    public const string RelTypeNotesSlide = "http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide";

    // ─── ZIP 条目操作 ───

    /// <summary>读取 ZIP 包中指定条目的 XML 文档。</summary>
    public static XDocument ReadEntryXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath)
            ?? throw new InvalidOperationException($"演示文稿包中缺少必要条目: {entryPath}");
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>尝试读取 ZIP 条目，不存在时返回 null。</summary>
    public static XDocument? TryReadEntryXml(ZipArchive archive, string entryPath)
    {
        var entry = archive.GetEntry(entryPath);
        if (entry is null) return null;
        using var stream = entry.Open();
        return XDocument.Load(stream);
    }

    /// <summary>将 XML 文档写回 ZIP 条目（先删后建，避免残留数据）。</summary>
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

    /// <summary>删除 ZIP 条目（如果存在）。</summary>
    public static void DeleteEntry(ZipArchive archive, string entryPath)
    {
        archive.GetEntry(entryPath)?.Delete();
    }

    // ─── 占位符查找 ───

    /// <summary>
    /// 在幻灯片 XML 中查找标题占位符形状。
    /// 匹配 type="title" 或 type="ctrTitle"，多个时取第一个匹配项。
    /// </summary>
    public static XElement? FindTitlePlaceholder(XDocument slideDoc)
    {
        return FindPlaceholderByTypes(slideDoc, ["title", "ctrTitle"]);
    }

    /// <summary>
    /// 在幻灯片 XML 中查找正文占位符形状。
    /// 匹配 type="body" 或 type="subTitle"，多个时取第一个匹配项。
    /// </summary>
    public static XElement? FindBodyPlaceholder(XDocument slideDoc)
    {
        return FindPlaceholderByTypes(slideDoc, ["body", "subTitle"]);
    }

    /// <summary>按占位符类型列表查找形状，返回第一个匹配项。</summary>
    private static XElement? FindPlaceholderByTypes(XDocument doc, string[] types)
    {
        var spTree = doc.Root?.Element(P + "cSld")?.Element(P + "spTree");
        if (spTree is null) return null;

        foreach (var sp in spTree.Elements(P + "sp"))
        {
            var ph = sp.Element(P + "nvSpPr")?.Element(P + "nvPr")?.Element(P + "ph");
            if (ph is null) continue;

            var type = ph.Attribute("type")?.Value;
            if (type is not null && types.Contains(type))
                return sp;
        }

        return null;
    }

    // ─── 文本操作 ───

    /// <summary>提取形状占位符中的全部纯文本（合并所有段落和 Run）。</summary>
    public static string GetShapeText(XElement shape)
    {
        var txBody = shape.Element(P + "txBody");
        if (txBody is null) return string.Empty;

        var parts = new List<string>();
        foreach (var p in txBody.Elements(A + "p"))
        {
            var text = string.Concat(p.Elements(A + "r").Select(r => r.Element(A + "t")?.Value ?? ""));
            parts.Add(text);
        }

        return string.Join("\n", parts).Trim();
    }

    /// <summary>
    /// 替换形状占位符中的文本，清除原有段落后写入新文本。
    /// 每个 \n 分隔的部分对应一个段落。保留 txBody 外层结构。
    /// </summary>
    public static void SetShapeText(XElement shape, string text)
    {
        var txBody = shape.Element(P + "txBody");
        if (txBody is null)
        {
            txBody = new XElement(P + "txBody",
                new XElement(A + "bodyPr"),
                new XElement(A + "lstStyle"));
            shape.Add(txBody);
        }

        // 移除现有段落
        txBody.Elements(A + "p").Remove();

        // 写入新段落
        foreach (var para in text.Split('\n'))
        {
            txBody.Add(new XElement(A + "p",
                new XElement(A + "r",
                    new XElement(A + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        para))));
        }
    }

    // ─── 幻灯片 XML 构造 ───

    /// <summary>
    /// 构建新幻灯片 XML，包含可选的标题和正文占位符。
    /// 占位符不设置显式位置，由关联版式控制布局。
    /// </summary>
    public static XDocument BuildSlideXml(bool hasTitlePlaceholder, bool hasBodyPlaceholder)
    {
        var spTree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", "")),
                new XElement(P + "cNvGrpSpPr"),
                new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr"));

        int nextId = 2;
        if (hasTitlePlaceholder)
            spTree.Add(BuildPlaceholderShape(nextId++, "Title", "title", null));
        if (hasBodyPlaceholder)
            spTree.Add(BuildPlaceholderShape(nextId, "Content", "body", "1"));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(P + "sld",
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "p", P.NamespaceName),
                new XElement(P + "cSld", spTree)));
    }

    /// <summary>构建备注页 XML，包含幻灯片图片占位符和备注文本占位符。</summary>
    public static XDocument BuildNotesXml(string text)
    {
        var spTree = new XElement(P + "spTree",
            new XElement(P + "nvGrpSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", "1"), new XAttribute("name", "")),
                new XElement(P + "cNvGrpSpPr"),
                new XElement(P + "nvPr")),
            new XElement(P + "grpSpPr"));

        // 幻灯片图片占位符
        spTree.Add(new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", "2"), new XAttribute("name", "Slide Image")),
                new XElement(P + "cNvSpPr",
                    new XElement(A + "spLocks",
                        new XAttribute("noGrp", "1"), new XAttribute("noRot", "1"), new XAttribute("noChangeAspect", "1"))),
                new XElement(P + "nvPr",
                    new XElement(P + "ph", new XAttribute("type", "sldImg")))),
            new XElement(P + "spPr")));

        // 备注文本占位符
        var txBody = new XElement(P + "txBody",
            new XElement(A + "bodyPr"),
            new XElement(A + "lstStyle"));
        foreach (var para in text.Split('\n'))
        {
            txBody.Add(new XElement(A + "p",
                new XElement(A + "r",
                    new XElement(A + "t",
                        new XAttribute(XNamespace.Xml + "space", "preserve"),
                        para))));
        }

        spTree.Add(new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", "3"), new XAttribute("name", "Notes Placeholder")),
                new XElement(P + "cNvSpPr",
                    new XElement(A + "spLocks", new XAttribute("noGrp", "1"))),
                new XElement(P + "nvPr",
                    new XElement(P + "ph", new XAttribute("type", "body"), new XAttribute("idx", "1")))),
            new XElement(P + "spPr"),
            txBody));

        return new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(P + "notes",
                new XAttribute(XNamespace.Xmlns + "a", A.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "r", R.NamespaceName),
                new XAttribute(XNamespace.Xmlns + "p", P.NamespaceName),
                new XElement(P + "cSld", spTree)));
    }

    /// <summary>构建幻灯片关系文件内容，指向版式路径。</summary>
    public static string BuildSlideRels(string layoutTarget)
    {
        return $"""
            <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="{RelTypeSlideLayout}" Target="{layoutTarget}"/>
            </Relationships>
            """;
    }

    // ─── 包结构导航 ───

    /// <summary>获取幻灯片关联版式的名称。</summary>
    public static string? GetSlideLayoutName(ZipArchive archive, string slideEntryPath)
    {
        var relsPath = GetSlideRelsPath(slideEntryPath);
        var relsDoc = TryReadEntryXml(archive, relsPath);
        if (relsDoc is null) return null;

        var layoutRel = relsDoc.Root!.Elements(Rels + "Relationship")
            .FirstOrDefault(r => r.Attribute("Type")?.Value == RelTypeSlideLayout);
        if (layoutRel is null) return null;

        var layoutTarget = layoutRel.Attribute("Target")?.Value;
        if (layoutTarget is null) return null;

        var slideDir = Path.GetDirectoryName(slideEntryPath)!.Replace('\\', '/');
        var layoutPath = ResolvePath(slideDir, layoutTarget);
        var layoutDoc = TryReadEntryXml(archive, layoutPath);
        return layoutDoc?.Root?.Element(P + "cSld")?.Attribute("name")?.Value;
    }

    /// <summary>检查幻灯片是否有备注页。</summary>
    public static bool HasNotesSlide(ZipArchive archive, string slideEntryPath)
    {
        var relsPath = GetSlideRelsPath(slideEntryPath);
        var relsDoc = TryReadEntryXml(archive, relsPath);
        if (relsDoc is null) return false;

        return relsDoc.Root!.Elements(Rels + "Relationship")
            .Any(r => r.Attribute("Type")?.Value == RelTypeNotesSlide);
    }

    /// <summary>获取幻灯片关系文件在包内的路径。</summary>
    public static string GetSlideRelsPath(string slideEntryPath)
    {
        var dir = Path.GetDirectoryName(slideEntryPath)!.Replace('\\', '/');
        return dir + "/_rels/" + Path.GetFileName(slideEntryPath) + ".rels";
    }

    /// <summary>将相对路径解析为包内绝对路径（处理 ../ 段）。</summary>
    public static string ResolvePath(string baseDir, string relativePath)
    {
        var parts = baseDir.Split('/').ToList();
        foreach (var segment in relativePath.Split('/'))
        {
            if (segment == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
            }
            else
            {
                parts.Add(segment);
            }
        }
        return string.Join("/", parts);
    }

    /// <summary>构建占位符形状元素（不含显式位置，由版式控制布局）。</summary>
    private static XElement BuildPlaceholderShape(int id, string name, string phType, string? phIdx)
    {
        var phAttrs = new List<XAttribute> { new("type", phType) };
        if (phIdx is not null) phAttrs.Add(new("idx", phIdx));

        return new XElement(P + "sp",
            new XElement(P + "nvSpPr",
                new XElement(P + "cNvPr", new XAttribute("id", id), new XAttribute("name", name)),
                new XElement(P + "cNvSpPr",
                    new XElement(A + "spLocks", new XAttribute("noGrp", "1"))),
                new XElement(P + "nvPr",
                    new XElement(P + "ph", phAttrs))),
            new XElement(P + "spPr"),
            new XElement(P + "txBody",
                new XElement(A + "bodyPr"),
                new XElement(A + "lstStyle"),
                new XElement(A + "p",
                    new XElement(A + "endParaRPr", new XAttribute("lang", "en-US")))));
    }
}
