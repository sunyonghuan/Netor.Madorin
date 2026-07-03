using System.Text.Json.Serialization;

namespace Netor.Cortana.Plugin.BuiltIn.FileBrowser;

/// <summary>
/// 文件浏览器内置工具 JSON 源生成上下文，供 AOT 环境下的 AI 工具参数反序列化使用。
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(List<FileOperator.BatchWriteFile>))]
[JsonSerializable(typeof(FileOperator.BatchWriteFile))]
internal partial class FileBrowserToolJsonContext : JsonSerializerContext;