using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// PowerPoint 幻灯片内容编辑工具：更新标题、更新正文、更新备注。
/// </summary>
[Tool]
public sealed class OfficePptElementTools(
    IPowerPointService pptService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficePptElementTools> logger)
{
    /// <summary>替换指定幻灯片的标题占位符文本。</summary>
    [Tool(Name = "office_ppt_update_slide_title",
        Description = "替换指定幻灯片的标题占位符文本。当目标幻灯片不存在标题占位符时返回错误。")]
    public string UpdateSlideTitle(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "幻灯片索引（从 0 开始）")] int slideIndex,
        [Parameter(Description = "新标题文本")] string title,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = pptService.UpdateSlideTitle(validSource!, validOutput!, slideIndex, title);

            logger.LogInformation("更新幻灯片标题: 索引={Index}, 新标题='{Title}'",
                slideIndex, title.Length > 50 ? title[..50] + "..." : title);

            return ToolResult.Ok($"标题更新成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.UpdateSlideTitleResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新幻灯片标题失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"更新幻灯片标题失败: {ex.Message}");
        }
    }

    /// <summary>替换指定幻灯片的正文占位符文本。</summary>
    [Tool(Name = "office_ppt_update_slide_body",
        Description = "替换指定幻灯片的正文占位符文本。body 中的 \\n 分隔多个段落。当目标幻灯片不存在正文占位符时返回错误。")]
    public string UpdateSlideBody(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "幻灯片索引（从 0 开始）")] int slideIndex,
        [Parameter(Description = "新正文文本，\\n 分隔段落")] string body,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = pptService.UpdateSlideBody(validSource!, validOutput!, slideIndex, body);

            logger.LogInformation("更新幻灯片正文: 索引={Index}, 段落数={Count}", slideIndex, result.ParagraphCount);

            return ToolResult.Ok($"正文更新成功，共 {result.ParagraphCount} 段。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.UpdateSlideBodyResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新幻灯片正文失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"更新幻灯片正文失败: {ex.Message}");
        }
    }

    /// <summary>设置或替换指定幻灯片的备注文本。</summary>
    [Tool(Name = "office_ppt_update_slide_notes",
        Description = "设置或替换指定幻灯片的备注文本。notes 中的 \\n 分隔多个段落。幻灯片无备注页时自动创建。")]
    public string UpdateSlideNotes(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "幻灯片索引（从 0 开始）")] int slideIndex,
        [Parameter(Description = "备注文本，\\n 分隔段落")] string notes,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = pptService.UpdateSlideNotes(validSource!, validOutput!, slideIndex, notes);

            logger.LogInformation("更新幻灯片备注: 索引={Index}, 段落数={Count}", slideIndex, result.ParagraphCount);

            return ToolResult.Ok($"备注更新成功，共 {result.ParagraphCount} 段。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.UpdateSlideNotesResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "更新幻灯片备注失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"更新幻灯片备注失败: {ex.Message}");
        }
    }

    /// <summary>校验源文件和输出路径的通用逻辑。</summary>
    private (string? ValidSource, string? ValidOutput, string? Error) ValidateSourceAndOutput(
        string sourcePath, string outputPath, int overwrite)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return (null, null, ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。"));
        if (!File.Exists(validSource))
            return (null, null, ToolResult.Fail(ErrorCodes.FileNotFound, $"源文件不存在: {validSource}"));

        var validOutput = pathGuard.ValidatePath(outputPath);
        if (validOutput is null)
            return (null, null, ToolResult.Fail(ErrorCodes.PathForbidden, "输出路径不在允许目录内。"));
        if (string.Equals(validSource, validOutput, StringComparison.OrdinalIgnoreCase))
            return (null, null, ToolResult.Fail(ErrorCodes.InvalidArgument, "输出路径不能与源文件相同。"));
        if (!ToolParameterHelper.IsTrue(overwrite) && File.Exists(validOutput))
            return (null, null, ToolResult.Fail(ErrorCodes.ConflictExists, $"文件已存在: {validOutput}"));

        return (validSource, validOutput, null);
    }
}