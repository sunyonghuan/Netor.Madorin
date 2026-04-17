using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// PowerPoint 演示文稿级操作工具：创建演示文稿、读取幻灯片列表、另存为。
/// </summary>
[Tool]
public sealed class OfficePptPresentationTools(
    IPowerPointService pptService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficePptPresentationTools> logger)
{
    /// <summary>创建空白 PowerPoint 演示文稿或基于模板复制创建。</summary>
    [Tool(Name = "office_ppt_create_presentation",
        Description = "创建空白 PowerPoint 演示文稿或基于模板复制创建。output_path 必填；template_path 为空字符串时创建空白演示文稿；title、author 为空字符串时不设置；overwrite 为 1 时允许覆盖已存在文件。")]
    public string CreatePresentation(
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "模板文件路径，空字符串表示创建空白演示文稿")] string templatePath,
        [Parameter(Description = "演示文稿标题，空字符串表示不设置")] string title,
        [Parameter(Description = "演示文稿作者，空字符串表示不设置")] string author,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。");

        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}");

        string? validTemplate = null;
        if (!ToolParameterHelper.IsEmpty(templatePath))
        {
            validTemplate = pathGuard.ValidatePath(templatePath);
            if (validTemplate is null)
                return ToolResult.Fail(ErrorCodes.PathForbidden, "模板路径不在允许目录内。");
            if (!File.Exists(validTemplate))
                return ToolResult.Fail(ErrorCodes.FileNotFound, $"模板文件不存在: {validTemplate}");
        }

        try
        {
            var result = pptService.CreatePresentation(validOutput, validTemplate,
                ToolParameterHelper.IsEmpty(title) ? null : title,
                ToolParameterHelper.IsEmpty(author) ? null : author);

            logger.LogInformation("创建演示文稿: {Output}, 幻灯片数={Count}, 来自模板={FromTemplate}",
                validOutput, result.SlideCount, result.CreatedFromTemplate);

            return ToolResult.Ok("演示文稿创建成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.CreatePresentationResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建演示文稿失败: {Output}", validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"创建演示文稿失败: {ex.Message}");
        }
    }

    /// <summary>读取演示文稿中的幻灯片摘要列表。</summary>
    [Tool(Name = "office_ppt_list_slides",
        Description = "读取演示文稿中的幻灯片摘要列表，返回每张幻灯片的索引、版式、标题预览、正文预览和备注状态。")]
    public string ListSlides(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "是否包含备注页信息：0=否，1=是")] int includeNotes,
        [Parameter(Description = "最大返回幻灯片数，0 表示返回全部")] int maxSlides)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"文件不存在: {validSource}");

        try
        {
            var result = pptService.ListSlides(validSource, ToolParameterHelper.IsTrue(includeNotes), maxSlides);

            logger.LogInformation("读取幻灯片列表: {Source}, 幻灯片数={Count}", validSource, result.SlideCount);

            return ToolResult.Ok("幻灯片列表读取成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.ListSlidesResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取幻灯片列表失败: {Source}", validSource);
            return ToolResult.Fail(ErrorCodes.InternalError, $"读取幻灯片列表失败: {ex.Message}");
        }
    }

    /// <summary>将演示文稿复制保存到新路径。</summary>
    [Tool(Name = "office_ppt_save_as",
        Description = "将演示文稿复制保存到新路径，不修改内容。source_path 和 output_path 不能相同。")]
    public string SaveAs(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"源文件不存在: {validSource}");

        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。");
        if (string.Equals(validSource, validOutput, StringComparison.OrdinalIgnoreCase))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "输出路径不能与源文件相同。");
        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}");

        try
        {
            var result = pptService.SaveAs(validSource, validOutput);

            logger.LogInformation("演示文稿另存为: {Source} -> {Output}, 大小={Size}",
                validSource, validOutput, result.FileSize);

            return ToolResult.Ok("另存为成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.SaveAsResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "演示文稿另存为失败: {Source} -> {Output}", validSource, validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"另存为失败: {ex.Message}");
        }
    }
}