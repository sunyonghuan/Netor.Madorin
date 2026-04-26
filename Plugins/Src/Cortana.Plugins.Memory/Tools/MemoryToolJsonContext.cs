using System.Text.Json.Serialization;
using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Tools;

[JsonSerializable(typeof(MemoryToolResult))]
[JsonSerializable(typeof(MemoryRecallResult))]
[JsonSerializable(typeof(MemorySupplyResult))]
[JsonSerializable(typeof(MemoryStatusResult))]
[JsonSerializable(typeof(MemoryAddNoteResult))]
[JsonSerializable(typeof(MemoryListRecentResult))]
internal sealed partial class MemoryToolJsonContext : JsonSerializerContext
{
}
