using System.Text.Json;
using Cortana.Plugins.Office.Models;
using Cortana.Plugins.Office.Security;
using Cortana.Plugins.Office.Services;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.Office.Tools;

/// <summary>
/// Word 文档级操作工具：创建文档、读取大纲、另存为。
/// </summary>
[Tool]
public sealed class OfficeWordDocumentTools(
    IWordDocumentService wordService,
    IDocumentPathGuard pathGuard,
    ILogger<OfficeWordDocumentTools> logger)
{
    /// <summary>创建空白 Word 文档或基于模板复制创建。</summary>
    [Tool(Name = "office_word_create_document",
        Description = "创建空白 Word 文档或基于模板复制创建。output_path 必填；template_path 为空字符串时创建空白文档；title、author 为空字符串时不设置；overwrite 为 1 时允许覆盖已存在文件。")]
    public string CreateDocument(
        [Parameter(Description = "输出文件路径（.docx）")] string outputPath,
        [Parameter(Description = "模板文件路径，空字符串表示创建空白文档")] string templatePath,
        [Parameter(Description = "文档标题，空字符串表示不设置")] string title,
        [Parameter(Description = "文档作者，空字符串表示不设置")] string author,
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
            wordService.CreateDocument(validOutput, validTemplate,
                ToolParameterHelper.IsEmpty(title) ? null : title,
                ToolParameterHelper.IsEmpty(author) ? null : author);

            var result = new CreateDocumentResult
            {
                DocumentPath = validOutput,
                CreatedFromTemplate = validTemplate is not null,
                OutputPath = validOutput
            };
            return ToolResult.Ok("文档创建成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.CreateDocumentResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "创建文档失败: {Output}", validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"创建文档失败: {ex.Message}");
        }
    }

    /// <summary>读取 Word 文档结构摘要。</summary>
    [Tool(Name = "office_word_get_outline",
        Description = "读取 Word 文档结构摘要，返回段落列表（含索引、样式、文本预览）以及表格和图片数量。调用编辑工具前建议先获取大纲以确定目标索引。")]
    public string GetOutline(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "是否包含段落文本预览：0=仅50字摘要，1=200字预览")] int includeText,
        [Parameter(Description = "最大返回段落数，0 表示返回全部")] int maxParagraphs)
    {
        var validSource = pathGuard.ValidatePath(sourcePath);
        if (validSource is null)
            return ToolResult.Fail(ErrorCodes.PathForbidden, "源文件路径不在允许目录内。");
        if (!File.Exists(validSource))
            return ToolResult.Fail(ErrorCodes.FileNotFound, $"文件不存在: {validSource}");

        try
        {
            var result = wordService.GetOutline(validSource, ToolParameterHelper.IsTrue(includeText), maxParagraphs);
            return ToolResult.Ok("文档大纲读取成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.DocumentOutlineResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "读取文档大纲失败: {Source}", validSource);
            return ToolResult.Fail(ErrorCodes.InternalError, $"读取文档大纲失败: {ex.Message}");
        }
    }

    /// <summary>将文档复制保存到新路径。</summary>
    [Tool(Name = "office_word_save_as",
        Description = "将 Word 文档复制保存到新路径，不修改内容。source_path 和 output_path 不能相同。")]
    public string SaveAs(
        [Parameter(Description = "源文件路径（.docx）")] string sourcePath,
        [Parameter(Description = "输出文件路径（.docx）")] string outputPath,
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
            var result = wordService.SaveAs(validSource, validOutput);
            return ToolResult.Ok("另存为成功。",
                JsonSerializer.Serialize(result, PluginJsonContext.Default.SaveAsResult));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "另存为失败: {Source} -> {Output}", validSource, validOutput);
            return ToolResult.Fail(ErrorCodes.InternalError, $"另存为失败: {ex.Message}");
        }
    }
}
