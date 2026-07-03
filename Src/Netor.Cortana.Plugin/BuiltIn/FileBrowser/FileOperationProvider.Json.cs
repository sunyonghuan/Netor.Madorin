using System.Globalization;
using System.Text.Json;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

public sealed partial class FileOperationProvider
{
    /// <summary>
    /// 构造错误响应的 JSON 字符串。
    /// </summary>
    /// <param name="tool">工具名称。</param>
    /// <param name="path">当前操作路径。</param>
    /// <param name="errorMessage">错误消息。</param>
    /// <param name="targetPath">可选的目标路径。</param>
    /// <returns>错误响应对应的 JSON 字符串。</returns>
    private static string BuildErrorJson(string tool, string path, string errorMessage, string? targetPath = null)
        => BuildResponseJson(new FileOperator.FileToolResult
        {
            Tool = tool,
            Success = false,
            Path = path,
            TargetPath = targetPath,
            Error = errorMessage,
            Message = $"错误：{errorMessage}"
        });

    /// <summary>
    /// 将 <see cref="FileOperator.FileToolResult"/> 序列化为 JSON 字符串。
    /// </summary>
    /// <param name="response">待序列化的响应对象。</param>
    /// <returns>序列化后的 JSON 字符串。</returns>
    private static string BuildResponseJson(FileOperator.FileToolResult response)
    {
        var itemsJson = response.Items is null
            ? "null"
            : $"[{string.Join(",", response.Items.Select(BuildItemJson))}]";

        return $"{{\"tool\":{Json(response.Tool)},\"success\":{Bool(response.Success)},\"path\":{Json(response.Path)},\"targetPath\":{Json(response.TargetPath)},\"message\":{Json(response.Message)},\"error\":{Json(response.Error)},\"backupPath\":{Json(response.BackupPath)},\"operation\":{Json(response.Operation)},\"bytesWritten\":{response.BytesWritten?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"startLine\":{response.StartLine?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"endLine\":{response.EndLine?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"changedLineCount\":{response.ChangedLineCount?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"successCount\":{response.SuccessCount?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"failCount\":{response.FailCount?.ToString(CultureInfo.InvariantCulture) ?? "null"},\"items\":{itemsJson}}}";
    }

    /// <summary>
    /// 将单个子项响应序列化为 JSON 字符串。
    /// </summary>
    /// <param name="response">待序列化的子项响应对象。</param>
    /// <returns>序列化后的 JSON 字符串。</returns>
    private static string BuildItemJson(FileOperator.FileToolResult response)
        => BuildResponseJson(response);

    /// <summary>
    /// 将字符串值转换为 JSON 字符串字面量。
    /// </summary>
    /// <param name="value">待转换的字符串值。</param>
    /// <returns>转换后的 JSON 字符串或 <c>null</c> 文本。</returns>
    private static string Json(string? value)
        => value is null ? "null" : $"\"{EscapeJson(value)}\"";

    /// <summary>
    /// 将布尔值转换为 JSON 布尔字面量。
    /// </summary>
    /// <param name="value">待转换的布尔值。</param>
    /// <returns><c>true</c> 或 <c>false</c> 的 JSON 文本。</returns>
    private static string Bool(bool value) => value ? "true" : "false";

    /// <summary>
    /// 对 JSON 字符串中的特殊字符进行转义。
    /// </summary>
    /// <param name="value">待转义的字符串。</param>
    /// <returns>完成转义后的字符串。</returns>
    private static string EscapeJson(string value)
        => value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\t", "\\t", StringComparison.Ordinal);
}
