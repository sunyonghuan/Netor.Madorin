using System.Text.Json;
using System.Text.Json.Serialization;

namespace Netor.Madorin.Plugin.PluginBus;

[JsonSerializable(typeof(PluginBusEventEnvelope))]
[JsonSerializable(typeof(PluginBusSubscribeFrame))]
[JsonSerializable(typeof(PluginBusInboundFrame))]
[JsonSerializable(typeof(JsonElement))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal sealed partial class PluginBusClientJsonContext : JsonSerializerContext;
