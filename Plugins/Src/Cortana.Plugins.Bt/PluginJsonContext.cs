using System.Text.Json.Serialization;

namespace Cortana.Plugins.Bt;

/// <summary>
/// 插件使用的 Json 源码生成上下文。
/// 所有需要序列化的类型都必须在这里显式注册，避免 AOT 运行时走反射路径。
/// </summary>
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(BtApiResult))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PluginJsonContext : JsonSerializerContext;
