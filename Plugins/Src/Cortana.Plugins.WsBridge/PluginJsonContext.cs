using System.Text.Json.Serialization;

using Cortana.Plugins.WsBridge.Models;

namespace Cortana.Plugins.WsBridge;

/// <summary>
/// 插件 JSON 源码生成上下文。
/// 所有需要序列化的类型必须在此显式注册，保持 Native AOT 兼容。
/// </summary>
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(BridgeEnvelope))]
[JsonSerializable(typeof(BridgeConfig))]
[JsonSerializable(typeof(BridgeSessionInfo))]
[JsonSerializable(typeof(List<BridgeSessionInfo>))]
[JsonSerializable(typeof(CortanaClientMessage))]
[JsonSerializable(typeof(CortanaServerMessage))]
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class PluginJsonContext : JsonSerializerContext;
