using System.Text.Json.Serialization;

namespace Netor.Cortana.Entitys;

/// <summary>
/// Entitys 层 JSON 源生成器上下文（AOT 兼容）。
/// </summary>
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Dictionary<string, string?>))]
internal partial class EntityJsonContext : JsonSerializerContext;
