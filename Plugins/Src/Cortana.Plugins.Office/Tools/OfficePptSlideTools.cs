using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// PowerPoint 幻灯片操作工具：添加幻灯片、删除幻灯片。
/// </summary>
[Tool]
public sealed class OfficePptSlideTools(
    IPowerPointService pptService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficePptSlideTools> logger)
{
    /// <summary>在指定位置插入新幻灯片。</summary>
    [Tool(Name = "office_ppt_add_slide",
        Description = "在演示文稿中插入新幻灯片。insert_index 为 -1 或省略时追加到末尾；layout_name 为空字符串时使用默认版式；title、body 为空字符串时不填充对应占位符。")]
    public string AddSlide(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "插入位置索引，-1 表示追加到末尾")] int insertIndex,
        [Parameter(Description = "版式名称，空字符串使用默认版式")] string layoutName,
        [Parameter(Description = "幻灯片标题，空字符串表示不设置")] string title,
        [Parameter(Description = "幻灯片正文，空字符串表示不设置")] string body,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = pptService.AddSlide(validSource!, validOutput!, insertIndex,
                ToolParameterHelper.IsEmpty(layoutName) ? null : layoutName,
                ToolParameterHelper.IsEmpty(title) ? null : title,
                ToolParameterHelper.IsEmpty(body) ? null : body);

            logger.LogInformation("添加幻灯片: 索引={Index}, 版式={Layout}, 源={Source}",
                result.SlideIndex, result.LayoutName, validSource);

            return ToolResult.Ok($"幻灯片添加成功，当前共 {result.SlideCount} 张。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.AddSlideResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "添加幻灯片失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"添加幻灯片失败: {ex.Message}");
        }
    }

    /// <summary>删除指定索引的幻灯片。</summary>
    [Tool(Name = "office_ppt_delete_slide",
        Description = "删除演示文稿中指定索引的幻灯片。使用前建议先调用 list_slides 确认目标索引。删除后索引自动重排。")]
    public string DeleteSlide(
        [Parameter(Description = "源文件路径（.pptx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.pptx）")] string outputPath,
        [Parameter(Description = "要删除的幻灯片索引（从 0 开始）")] int slideIndex,
        [Parameter(Description = "是否要求目标幻灯片非空：0=允许删除空幻灯片，1=空幻灯片返回错误")] int requireNonEmpty,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = pptService.DeleteSlide(validSource!, validOutput!, slideIndex,
                ToolParameterHelper.IsTrue(requireNonEmpty));

            logger.LogInformation("删除幻灯片: 索引={Index}, 剩余={Count}", result.DeletedIndex, result.SlideCount);

            return ToolResult.Ok($"幻灯片删除成功，剩余 {result.SlideCount} 张。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.DeleteSlideResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (ArgumentException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return ToolResult.Fail(ErrorCodes.ContentNotFound, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "删除幻灯片失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"删除幻灯片失败: {ex.Message}");
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