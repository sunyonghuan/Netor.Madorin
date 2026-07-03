using System.Text.Json.Serialization;

using Netor.Cortana.AI.Hitl.Models;

namespace Netor.Cortana.AI.Hitl.Json;

/// <summary>
/// HITL 公共模型的 AOT JSON 源生成上下文。
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(HitlPendingRequestSnapshot))]
public partial class HitlJsonContext : JsonSerializerContext;
