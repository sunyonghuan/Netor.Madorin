using System.Text.Json.Serialization;

using Netor.Madorin.Plugin.Process.Settings;

namespace Netor.Madorin.Plugin.Process.Protocol;

/// <summary>
/// AOT 安全的 <see cref="System.Text.Json.Serialization.JsonSerializerContext"/>，
/// 用于 Process SDK 内置协议类型的序列化。
/// <para>
/// 工具参数和返回值使用插件自己生成的 JsonContext，不在这里注册。
/// </para>
/// </summary>
[JsonSerializable(typeof(HostRequest))]
[JsonSerializable(typeof(HostResponse))]
[JsonSerializable(typeof(PluginInfoData))]
[JsonSerializable(typeof(PluginSettingData))]
[JsonSerializable(typeof(RequiredHostCapabilityData))]
[JsonSerializable(typeof(ToolInfoData))]
[JsonSerializable(typeof(ParameterInfoData))]
[JsonSerializable(typeof(InitConfig))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public sealed partial class ProcessProtocolJsonContext : JsonSerializerContext;
