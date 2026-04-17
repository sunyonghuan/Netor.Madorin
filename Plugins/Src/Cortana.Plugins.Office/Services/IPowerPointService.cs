using Cortana.Plugins.Office.Models;

namespace Cortana.Plugins.Office.Services;

/// <summary>
/// PowerPoint 演示文稿服务接口。
/// 提供 pptx 文件的创建、读取、编辑和另存操作。
/// 实现层直接操作 ZIP 包和 XML，不依赖 Office 进程。
/// </summary>
public interface IPowerPointService
{
    /// <summary>创建空白或基于模板的演示文稿。</summary>
    CreatePresentationResult CreatePresentation(string outputPath, string? templatePath, string? title, string? author);

    /// <summary>读取演示文稿幻灯片摘要列表。</summary>
    ListSlidesResult ListSlides(string sourcePath, bool includeNotes, int maxSlides);

    /// <summary>在指定位置插入新幻灯片。</summary>
    AddSlideResult AddSlide(string sourcePath, string outputPath, int insertIndex,
        string? layoutName, string? title, string? body);

    /// <summary>替换指定幻灯片的标题占位符文本。</summary>
    UpdateSlideTitleResult UpdateSlideTitle(string sourcePath, string outputPath, int slideIndex, string title);

    /// <summary>替换指定幻灯片的正文占位符文本。</summary>
    UpdateSlideBodyResult UpdateSlideBody(string sourcePath, string outputPath, int slideIndex, string body);

    /// <summary>删除指定索引的幻灯片。</summary>
    DeleteSlideResult DeleteSlide(string sourcePath, string outputPath, int slideIndex, bool requireNonEmpty);

    /// <summary>设置或替换指定幻灯片的备注文本。</summary>
    UpdateSlideNotesResult UpdateSlideNotes(string sourcePath, string outputPath, int slideIndex, string notes);

    /// <summary>复制演示文稿到新路径，不做内容修改。</summary>
    SaveAsResult SaveAs(string sourcePath, string outputPath);
}
