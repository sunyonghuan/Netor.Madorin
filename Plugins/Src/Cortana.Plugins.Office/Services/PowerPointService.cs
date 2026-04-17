using System.IO.Compression;
using System.Xml.Linq;
using Cortana.Plugins.Office.Infrastructure;
using Cortana.Plugins.Office.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// PowerPoint 演示文稿服务实现。
/// 通过 System.IO.Compression 和 System.Xml.Linq 直接操作 pptx 包结构，保持 Native AOT 兼容。
/// </summary>
public sealed partial class PowerPointService(ILogger<PowerPointService> logger) : IPowerPointService
{
    private static readonly XNamespace P = PptxXmlHelper.P;
    private static readonly XNamespace A = PptxXmlHelper.A;
    private static readonly XNamespace R = PptxXmlHelper.R;

    /// <inheritdoc/>
    public CreatePresentationResult CreatePresentation(string outputPath, string? templatePath, string? title, string? author)
    {
        EnsureDirectoryExists(outputPath);

        if (templatePath is not null)
        {
            File.Copy(templatePath, outputPath, overwrite: true);
            var slideCount = CountSlides(outputPath);
            logger.LogInformation("基于模板创建演示文稿: {Template} -> {Output}", templatePath, outputPath);
            return new CreatePresentationResult
            {
                PresentationPath = outputPath,
                SlideCount = slideCount,
                CreatedFromTemplate = true,
                OutputPath = outputPath
            };
        }

        // 创建最小有效 pptx 包
        using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create);

        PptxXmlHelper.WriteEntryText(archive, "[Content_Types].xml", PptxTemplate.ContentTypes);
        PptxXmlHelper.WriteEntryText(archive, "_rels/.rels", PptxTemplate.Relationships);
        PptxXmlHelper.WriteEntryText(archive, "ppt/presentation.xml", PptxTemplate.Presentation);
        PptxXmlHelper.WriteEntryText(archive, "ppt/_rels/presentation.xml.rels", PptxTemplate.PresentationRels);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideMasters/slideMaster1.xml", PptxTemplate.SlideMaster);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideMasters/_rels/slideMaster1.xml.rels", PptxTemplate.SlideMasterRels);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideLayouts/slideLayout1.xml", PptxTemplate.LayoutTitleAndContent);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideLayouts/slideLayout2.xml", PptxTemplate.LayoutBlank);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideLayouts/_rels/slideLayout1.xml.rels", PptxTemplate.SlideLayoutRels);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slideLayouts/_rels/slideLayout2.xml.rels", PptxTemplate.SlideLayoutRels);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slides/slide1.xml", PptxTemplate.BlankSlide);
        PptxXmlHelper.WriteEntryText(archive, "ppt/slides/_rels/slide1.xml.rels", PptxTemplate.BlankSlideRels);
        PptxXmlHelper.WriteEntryText(archive, "docProps/core.xml", PptxTemplate.BuildCoreProperties(title, author));

        logger.LogInformation("创建空白演示文稿: {Output}", outputPath);

        return new CreatePresentationResult
        {
            PresentationPath = outputPath,
            SlideCount = 1,
            CreatedFromTemplate = false,
            OutputPath = outputPath
        };
    }

    /// <inheritdoc/>
    public ListSlidesResult ListSlides(string sourcePath, bool includeNotes, int maxSlides)
    {
        using var stream = File.OpenRead(sourcePath);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var slideEntries = GetOrderedSlideEntries(archive);
        int limit = maxSlides > 0 ? Math.Min(maxSlides, slideEntries.Count) : slideEntries.Count;
        var slides = new List<SlideInfo>(limit);

        for (int i = 0; i < limit; i++)
        {
            var slidePath = slideEntries[i];
            var slideDoc = PptxXmlHelper.ReadEntryXml(archive, slidePath);

            var titleShape = PptxXmlHelper.FindTitlePlaceholder(slideDoc);
            var bodyShape = PptxXmlHelper.FindBodyPlaceholder(slideDoc);
            var layoutName = PptxXmlHelper.GetSlideLayoutName(archive, slidePath);
            var hasNotes = PptxXmlHelper.HasNotesSlide(archive, slidePath);

            slides.Add(new SlideInfo
            {
                Index = i,
                LayoutName = layoutName,
                TitlePreview = Truncate(titleShape is not null ? PptxXmlHelper.GetShapeText(titleShape) : "", 100),
                BodyPreview = Truncate(bodyShape is not null ? PptxXmlHelper.GetShapeText(bodyShape) : "", 100),
                HasNotes = hasNotes
            });
        }

        logger.LogInformation("读取幻灯片列表: {Source}, 幻灯片数={Count}", sourcePath, slides.Count);

        return new ListSlidesResult
        {
            Slides = slides,
            SlideCount = slideEntries.Count
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

    // ─── 内部共用方法 ───

    /// <summary>
    /// 获取按 presentation.xml 中 sldIdLst 顺序排列的幻灯片包内路径列表。
    /// </summary>
    internal static List<string> GetOrderedSlideEntries(ZipArchive archive)
    {
        var presDoc = PptxXmlHelper.ReadEntryXml(archive, "ppt/presentation.xml");
        var relsDoc = PptxXmlHelper.ReadEntryXml(archive, "ppt/_rels/presentation.xml.rels");

        // 建立 rId → target 映射
        var relMap = new Dictionary<string, string>();
        foreach (var rel in relsDoc.Root!.Elements(PptxXmlHelper.Rels + "Relationship"))
        {
            var id = rel.Attribute("Id")?.Value;
            var target = rel.Attribute("Target")?.Value;
            if (id is not null && target is not null)
                relMap[id] = target.StartsWith("ppt/") ? target : "ppt/" + target;
        }

        // 按 sldIdLst 顺序收集
        var result = new List<string>();
        var sldIdLst = presDoc.Root!.Element(P + "sldIdLst");
        if (sldIdLst is not null)
        {
            foreach (var sldId in sldIdLst.Elements(P + "sldId"))
            {
                var rId = sldId.Attribute(R + "id")?.Value;
                if (rId is not null && relMap.TryGetValue(rId, out var target))
                    result.Add(target);
            }
        }

        return result;
    }

    /// <summary>
    /// 复制源文件到输出路径并以更新模式打开 ZIP 包。
    /// 调用方需负责 dispose 返回的 Archive 和 Stream。
    /// </summary>
    internal static (ZipArchive Archive, FileStream Stream) OpenForEdit(string sourcePath, string outputPath)
    {
        EnsureDirectoryExists(outputPath);
        File.Copy(sourcePath, outputPath, overwrite: true);

        var stream = new FileStream(outputPath, FileMode.Open, FileAccess.ReadWrite);
        var archive = new ZipArchive(stream, ZipArchiveMode.Update, leaveOpen: true);
        return (archive, stream);
    }

    /// <summary>统计演示文稿中的幻灯片数量。</summary>
    private static int CountSlides(string path)
    {
        using var stream = File.OpenRead(path);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return GetOrderedSlideEntries(archive).Count;
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
