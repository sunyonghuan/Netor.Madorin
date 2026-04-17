using System.IO.Compression;
using System.Xml.Linq;
using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;
using static Cortana.Plugins.Office.Infrastructure.PptxXmlHelper;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// PowerPoint 演示文稿服务实现（编辑操作部分）：
/// 幻灯片增删、标题/正文/备注替换。
/// </summary>
public sealed partial class PowerPointService
{
    /// <inheritdoc/>
    public AddSlideResult AddSlide(string sourcePath, string outputPath, int insertIndex,
        string? layoutName, string? title, string? body)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var presDoc = PptxXmlHelper.ReadEntryXml(archive, "ppt/presentation.xml");
            var presRels = PptxXmlHelper.ReadEntryXml(archive, "ppt/_rels/presentation.xml.rels");

            var slideEntries = GetOrderedSlideEntries(archive);
            int slideCount = slideEntries.Count;

            // 验证插入位置
            if (insertIndex < 0) insertIndex = slideCount;
            if (insertIndex > slideCount)
                throw new ArgumentOutOfRangeException(nameof(insertIndex),
                    $"插入索引 {insertIndex} 超出范围 [0, {slideCount}]");

            // 解析版式：优先按名称匹配，否则选取"标题和内容"版式，再否则选第一个
            string targetLayout = ResolveLayoutName(archive, layoutName, presRels, slideEntries[0]);

            // 生成新幻灯片文件路径和关系 ID
            int newSlideNum = slideCount + 1;
            int newSlideId = 256 + newSlideNum; // 最小有效 sldId 从 256 开始
            string slideFileName = $"slide{newSlideNum}.xml";
            string slidePath = $"ppt/slides/{slideFileName}";
            string slideRelsPath = $"ppt/slides/_rels/{slideFileName}.rels";
            string rId = $"rIdSlide{newSlideNum}";

            // 构建新幻灯片 XML
            bool hasTitle = !string.IsNullOrEmpty(title);
            bool hasBody = !string.IsNullOrEmpty(body);
            var slideXml = PptxXmlHelper.BuildSlideXml(hasTitle, hasBody);
            var slideDoc = slideXml;

            // 填充标题和正文占位符（如果提供）
            if (hasTitle)
            {
                var titleShape = PptxXmlHelper.FindTitlePlaceholder(slideDoc);
                if (titleShape is not null)
                    PptxXmlHelper.SetShapeText(titleShape, title!);
            }
            if (hasBody)
            {
                var bodyShape = PptxXmlHelper.FindBodyPlaceholder(slideDoc);
                if (bodyShape is not null)
                    PptxXmlHelper.SetShapeText(bodyShape, body!);
            }

            // 写入最终幻灯片 XML 和关系，确保标题/正文修改持久化到包内。
            PptxXmlHelper.WriteEntryXml(archive, slidePath, slideDoc);
            PptxXmlHelper.WriteEntryText(archive, slideRelsPath, PptxXmlHelper.BuildSlideRels("../slideLayouts/" + targetLayout));

            // 更新 presentation.xml：插入 sldId 到 sldIdLst
            var sldIdLst = presDoc.Root!.Element(P + "sldIdLst")!;
            var newSldId = new XElement(P + "sldId",
                new XAttribute("id", newSlideId.ToString()),
                new XAttribute(R + "id", rId));

            if (insertIndex >= slideCount)
                sldIdLst.Add(newSldId); // 追加到末尾
            else
            {
                // 找到 insertIndex 对应幻灯片的 rId，在其前面插入
                var targetSlidePath = slideEntries[insertIndex];
                var targetRels = PptxXmlHelper.ReadEntryXml(archive, "ppt/_rels/presentation.xml.rels");
                var targetRId = FindSlideRId(targetRels, targetSlidePath);
                var targetSldId = sldIdLst.Elements(P + "sldId")
                    .FirstOrDefault(s => s.Attribute(R + "id")?.Value == targetRId);
                targetSldId?.AddBeforeSelf(newSldId);
            }

            PptxXmlHelper.WriteEntryXml(archive, "ppt/presentation.xml", presDoc);

            // 更新 presentation.xml.rels：添加新幻灯片关系
            var newRel = new XElement(PptxXmlHelper.Rels + "Relationship",
                new XAttribute("Id", rId),
                new XAttribute("Type", PptxXmlHelper.RelTypeSlide),
                new XAttribute("Target", $"slides/{slideFileName}"));
            presRels.Root!.Add(newRel);
            PptxXmlHelper.WriteEntryXml(archive, "ppt/_rels/presentation.xml.rels", presRels);

            // 更新 [Content_Types].xml：添加幻灯片内容类型（如果尚未注册）
            var ctDoc = PptxXmlHelper.ReadEntryXml(archive, "[Content_Types].xml");
            var slideOverride = ctDoc.Root!.Elements()
                .FirstOrDefault(e => e.Attribute("PartName")?.Value == $"/{slidePath}");
            if (slideOverride is null)
            {
                ctDoc.Root.Add(new XElement(CT + "Override",
                    new XAttribute("PartName", $"/{slidePath}"),
                    new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.presentationml.slide+xml")));
                PptxXmlHelper.WriteEntryXml(archive, "[Content_Types].xml", ctDoc);
            }

            int insertedIndex = insertIndex >= slideCount ? slideCount : insertIndex;
            logger.LogInformation("添加幻灯片: 索引={Index}, 版式={Layout}, 源={Source}", insertedIndex, targetLayout, sourcePath);

            return new AddSlideResult
            {
                SlideIndex = insertedIndex,
                LayoutName = targetLayout,
                SlideCount = slideCount + 1,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public UpdateSlideTitleResult UpdateSlideTitle(string sourcePath, string outputPath, int slideIndex, string title)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var slideEntries = GetOrderedSlideEntries(archive);
            int slideCount = slideEntries.Count;

            if (slideIndex < 0 || slideIndex >= slideCount)
                throw new ArgumentOutOfRangeException(nameof(slideIndex),
                    $"幻灯片索引 {slideIndex} 超出范围 [0, {slideCount - 1}]");

            var slidePath = slideEntries[slideIndex];
            var slideDoc = PptxXmlHelper.ReadEntryXml(archive, slidePath);

            var titleShape = PptxXmlHelper.FindTitlePlaceholder(slideDoc);
            if (titleShape is null)
                throw new InvalidOperationException($"幻灯片 {slideIndex} 不包含标题占位符。");

            var oldTitle = PptxXmlHelper.GetShapeText(titleShape);
            PptxXmlHelper.SetShapeText(titleShape, title);
            PptxXmlHelper.WriteEntryXml(archive, slidePath, slideDoc);

            logger.LogInformation("更新幻灯片标题: 索引={Index}, 旧标题='{Old}', 新标题='{New}'",
                slideIndex, Truncate(oldTitle, 50), Truncate(title, 50));

            return new UpdateSlideTitleResult
            {
                SlideIndex = slideIndex,
                OldTitle = string.IsNullOrEmpty(oldTitle) ? null : oldTitle,
                NewTitle = title,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public UpdateSlideBodyResult UpdateSlideBody(string sourcePath, string outputPath, int slideIndex, string body)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var slideEntries = GetOrderedSlideEntries(archive);
            int slideCount = slideEntries.Count;

            if (slideIndex < 0 || slideIndex >= slideCount)
                throw new ArgumentOutOfRangeException(nameof(slideIndex),
                    $"幻灯片索引 {slideIndex} 超出范围 [0, {slideCount - 1}]");

            var slidePath = slideEntries[slideIndex];
            var slideDoc = PptxXmlHelper.ReadEntryXml(archive, slidePath);

            var bodyShape = PptxXmlHelper.FindBodyPlaceholder(slideDoc);
            if (bodyShape is null)
                throw new InvalidOperationException($"幻灯片 {slideIndex} 不包含正文占位符。");

            PptxXmlHelper.SetShapeText(bodyShape, body);

            int paragraphCount = string.IsNullOrEmpty(body) ? 0 :
                body.Split('\n', StringSplitOptions.TrimEntries).Count(t => !string.IsNullOrEmpty(t));

            PptxXmlHelper.WriteEntryXml(archive, slidePath, slideDoc);

            logger.LogInformation("更新幻灯片正文: 索引={Index}, 段落数={Count}", slideIndex, paragraphCount);

            return new UpdateSlideBodyResult
            {
                SlideIndex = slideIndex,
                ParagraphCount = paragraphCount,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public DeleteSlideResult DeleteSlide(string sourcePath, string outputPath, int slideIndex, bool requireNonEmpty)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var slideEntries = GetOrderedSlideEntries(archive);
            int slideCount = slideEntries.Count;

            if (slideIndex < 0 || slideIndex >= slideCount)
                throw new ArgumentOutOfRangeException(nameof(slideIndex),
                    $"幻灯片索引 {slideIndex} 超出范围 [0, {slideCount - 1}]");

            if (slideCount <= 1)
                throw new ArgumentException("演示文稿仅剩一张幻灯片，无法删除。", nameof(slideIndex));

            var slidePath = slideEntries[slideIndex];
            var slideDoc = PptxXmlHelper.ReadEntryXml(archive, slidePath);

            // 检查是否非空（当 requireNonEmpty 为 true 时）
            if (requireNonEmpty)
            {
                var titleShape = PptxXmlHelper.FindTitlePlaceholder(slideDoc);
                var bodyShape = PptxXmlHelper.FindBodyPlaceholder(slideDoc);
                var titleText = titleShape is not null ? PptxXmlHelper.GetShapeText(titleShape) : "";
                var bodyText = bodyShape is not null ? PptxXmlHelper.GetShapeText(bodyShape) : "";
                if (string.IsNullOrWhiteSpace(titleText) && string.IsNullOrWhiteSpace(bodyText))
                    throw new InvalidOperationException($"幻灯片 {slideIndex} 为空，不允许删除。");
            }

            // 从 presentation.xml 的 sldIdLst 中移除对应条目
            var presDoc = PptxXmlHelper.ReadEntryXml(archive, "ppt/presentation.xml");
            var presRels = PptxXmlHelper.ReadEntryXml(archive, "ppt/_rels/presentation.xml.rels");
            var targetRId = FindSlideRId(presRels, slidePath);

            var sldIdLst = presDoc.Root!.Element(P + "sldIdLst")!;
            var targetSldId = sldIdLst.Elements(P + "sldId")
                .FirstOrDefault(s => s.Attribute(R + "id")?.Value == targetRId);
            targetSldId?.Remove();
            PptxXmlHelper.WriteEntryXml(archive, "ppt/presentation.xml", presDoc);

            // 从 presentation.xml.rels 中移除幻灯片关系
            var slideRel = presRels.Root!.Elements()
                .FirstOrDefault(r => r.Attribute("Id")?.Value == targetRId);
            slideRel?.Remove();
            PptxXmlHelper.WriteEntryXml(archive, "ppt/_rels/presentation.xml.rels", presRels);

            // 删除幻灯片文件及其关系文件
            PptxXmlHelper.DeleteEntry(archive, slidePath);
            PptxXmlHelper.DeleteEntry(archive, slidePath.Replace("ppt/slides/", "ppt/slides/_rels/") + ".rels");

            // 删除备注页（如果存在）
            var notesPath = slidePath.Replace("ppt/slides/", "ppt/notesSlides/") + ".xml";
            var notesRelsPath = slidePath.Replace("ppt/slides/", "ppt/notesSlides/_rels/") + ".xml.rels";
            PptxXmlHelper.DeleteEntry(archive, notesPath);
            PptxXmlHelper.DeleteEntry(archive, notesRelsPath);

            logger.LogInformation("删除幻灯片: 索引={Index}, 剩余={Count}", slideIndex, slideCount - 1);

            return new DeleteSlideResult
            {
                DeletedIndex = slideIndex,
                SlideCount = slideCount - 1,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    /// <inheritdoc/>
    public UpdateSlideNotesResult UpdateSlideNotes(string sourcePath, string outputPath, int slideIndex, string notes)
    {
        var (archive, stream) = OpenForEdit(sourcePath, outputPath);
        using (stream)
        using (archive)
        {
            var slideEntries = GetOrderedSlideEntries(archive);
            int slideCount = slideEntries.Count;

            if (slideIndex < 0 || slideIndex >= slideCount)
                throw new ArgumentOutOfRangeException(nameof(slideIndex),
                    $"幻灯片索引 {slideIndex} 超出范围 [0, {slideCount - 1}]");

            var slidePath = slideEntries[slideIndex];
            var slideRelsPath = PptxXmlHelper.GetSlideRelsPath(slidePath);

            // 检查是否已存在备注页
            var slideRels = PptxXmlHelper.ReadEntryXml(archive, slideRelsPath);
            var notesRel = slideRels.Root!.Elements()
                .FirstOrDefault(r => r.Attribute("Type")?.Value == PptxXmlHelper.RelTypeNotesSlide);

            string notesSlidePath;
            bool isNew;

            if (notesRel is not null)
            {
                // 已有备注页，直接更新
                var target = notesRel.Attribute("Target")?.Value;
                var slideDir = Path.GetDirectoryName(slidePath)!.Replace('\\', '/');
                notesSlidePath = PptxXmlHelper.ResolvePath(slideDir, target!);
                isNew = false;
            }
            else
            {
                // 创建新备注页
                notesSlidePath = slidePath.Replace("ppt/slides/", "ppt/notesSlides/").Replace(".xml", "Notes.xml");
                isNew = true;

                // 添加关系
                var newRId = "rIdNotes";
                slideRels.Root!.Add(new XElement(PptxXmlHelper.Rels + "Relationship",
                    new XAttribute("Id", newRId),
                    new XAttribute("Type", PptxXmlHelper.RelTypeNotesSlide),
                    new XAttribute("Target", notesSlidePath.Replace("ppt/", "../").Replace("\\", "/"))));
                PptxXmlHelper.WriteEntryXml(archive, slideRelsPath, slideRels);

                // 在 [Content_Types].xml 中注册备注页类型
                var ctDoc = PptxXmlHelper.ReadEntryXml(archive, "[Content_Types].xml");
                if (ctDoc.Root!.Elements().All(e => e.Attribute("PartName")?.Value != $"/{notesSlidePath}"))
                {
                    ctDoc.Root.Add(new XElement(CT + "Override",
                        new XAttribute("PartName", $"/{notesSlidePath}"),
                        new XAttribute("ContentType", "application/vnd.openxmlformats-officedocument.presentationml.notesSlide+xml")));
                    PptxXmlHelper.WriteEntryXml(archive, "[Content_Types].xml", ctDoc);
                }
            }

            // 构建或更新备注 XML
            var notesXml = PptxXmlHelper.BuildNotesXml(notes);
            PptxXmlHelper.WriteEntryXml(archive, notesSlidePath, notesXml);

            int paragraphCount = string.IsNullOrEmpty(notes) ? 0 :
                notes.Split('\n', StringSplitOptions.TrimEntries).Count(t => !string.IsNullOrEmpty(t));

            logger.LogInformation("更新幻灯片备注: 索引={Index}, 新建={IsNew}, 段落数={Count}",
                slideIndex, isNew, paragraphCount);

            return new UpdateSlideNotesResult
            {
                SlideIndex = slideIndex,
                ParagraphCount = paragraphCount,
                ChangedCount = 1,
                OutputPath = outputPath
            };
        }
    }

    // ─── 内部辅助方法 ───

    /// <summary>
    /// 解析版式名称，返回版式文件名（不含路径）。
    /// 优先级：按名称精确匹配 "标题和内容" → 第一个可用版式。
    /// </summary>
    private static string ResolveLayoutName(ZipArchive archive, string? layoutName,
        XDocument presRels, string referenceSlidePath)
    {
        if (!string.IsNullOrEmpty(layoutName))
        {
            // 搜索所有版式，找名称匹配的
            var slideDir = Path.GetDirectoryName(referenceSlidePath)!.Replace('\\', '/');
            var layouts = archive.Entries.Where(e =>
                e.FullName.Contains("ppt/slideLayouts/") && e.FullName.EndsWith(".xml")).ToList();

            foreach (var entry in layouts)
            {
                try
                {
                    var doc = PptxXmlHelper.ReadEntryXml(archive, entry.FullName);
                    var name = doc.Root?.Element(P + "cSld")?.Attribute("name")?.Value;
                    if (string.Equals(name, layoutName, StringComparison.Ordinal))
                    {
                        return Path.GetFileName(entry.FullName);
                    }
                }
                catch { /* 跳过无效条目 */ }
            }
        }

        // 回退到"标题和内容"版式（slideLayout1.xml）
        return "slideLayout1.xml";
    }

    /// <summary>
    /// 在 presentation.xml.rels 中查找指定幻灯片路径对应的 rId。
    /// </summary>
    private static string FindSlideRId(XDocument presRels, string slidePath)
    {
        var slideFileName = Path.GetFileName(slidePath);

        return presRels.Root!.Elements()
            .Where(r => r.Attribute("Type")?.Value == PptxXmlHelper.RelTypeSlide)
            .FirstOrDefault(r =>
            {
                var target = r.Attribute("Target")?.Value ?? "";
                return target.EndsWith(slideFileName);
            })?.Attribute("Id")?.Value
            ?? throw new InvalidOperationException($"未找到幻灯片 {slidePath} 的关系条目。");
    }
}