using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Word 段落和文本操作工具：插入段落、删除段落、文本替换。
/// </summary>
[Tool]
public sealed class OfficeWordParagraphTools(
    IWordDocumentService wordService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeWordParagraphTools> logger)
{
    /// <summary>在指定段落前后插入新段落。</summary>
    [Tool(Name = "office_word_insert_paragraph",
        Description = "在指定段落前后插入新段落。使用前请先调用 get_outline 获取段落索引。position 仅允许 before 或 after。")]
    public string InsertParagraph(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.docx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "锚点段落索引（从 0 开始）")] int anchorIndex,
        [Parameter(Description = "插入位置：before 或 after")] string position,
        [Parameter(Description = "段落文本内容")] string text,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (position is not ("before" or "after"))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "position 必须为 before 或 after。");

        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = wordService.InsertParagraph(validSource!, validOutput!, anchorIndex, position, text);
            return ToolResult.Ok("段落插入成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.InsertParagraphResult));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return ToolResult.Fail(ErrorCodes.InvalidArgument, ex.Message);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "插入段落失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"插入段落失败: {ex.Message}");
        }
    }

    /// <summary>删除指定索引的段落。</summary>
    [Tool(Name = "office_word_delete_paragraph",
        Description = "删除指定索引的段落。使用前请先调用 get_outline 确认目标段落。require_non_empty 为 1 时仅删除非空段落。")]
    public string DeleteParagraph(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.docx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "要删除的段落索引（从 0 开始）")] int paragraphIndex,
        [Parameter(Description = "是否要求目标段落非空：0=允许删除空段落，1=空段落返回错误")] int requireNonEmpty,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = wordService.DeleteParagraph(validSource!, validOutput!, paragraphIndex,
                ToolParameterHelper.IsTrue(requireNonEmpty));
            return ToolResult.Ok("段落删除成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.DeleteParagraphResult));
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
            logger.LogError(ex, "删除段落失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"删除段落失败: {ex.Message}");
        }
    }

    /// <summary>按文本匹配执行替换。</summary>
    [Tool(Name = "office_word_replace_text",
        Description = "按文本匹配执行替换。V1 仅支持单个 Run 内的文本匹配。match_case 为 1 时区分大小写；replace_all 为 1 时替换全部匹配。")]
    public string ReplaceText(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.docx），不能与源文件相同")] string outputPath,
        [Parameter(Description = "搜索文本")] string searchText,
        [Parameter(Description = "替换文本")] string replaceText,
        [Parameter(Description = "是否区分大小写：0=不区分，1=区分")] int matchCase,
        [Parameter(Description = "是否替换全部匹配：0=仅替换首个，1=替换全部")] int replaceAll,
        [Parameter(Description = "最大替换数量，0 表示不限制")] int maxReplaceCount,
        [Parameter(Description = "是否覆盖已存在文件：0=否，1=是")] int overwrite)
    {
        if (ToolParameterHelper.IsEmpty(searchText))
            return ToolResult.Fail(ErrorCodes.InvalidArgument, "searchText 不能为空。");

        var (validSource, validOutput, error) = ValidateSourceAndOutput(sourcePath, outputPath, overwrite);
        if (error is not null) return error;

        try
        {
            var result = wordService.ReplaceText(validSource!, validOutput!, searchText, replaceText,
                ToolParameterHelper.IsTrue(matchCase), ToolParameterHelper.IsTrue(replaceAll), maxReplaceCount);

            if (result.ReplacedCount == 0)
                return ToolResult.Fail(ErrorCodes.ContentNotFound, $"未找到匹配文本: {searchText}");

            return ToolResult.Ok($"替换完成，共替换 {result.ReplacedCount} 处。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.ReplaceTextResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "文本替换失败");
            return ToolResult.Fail(ErrorCodes.InternalError, $"文本替换失败: {ex.Message}");
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
