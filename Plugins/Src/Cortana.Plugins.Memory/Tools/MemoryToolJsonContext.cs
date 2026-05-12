using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cortana.Plugins.Memory.Models;

namespace Cortana.Plugins.Memory.Tools;

[JsonSerializable(typeof(MemoryToolResult))]
[JsonSerializable(typeof(MemoryRecallResult))]
[JsonSerializable(typeof(MemorySupplyResult))]
[JsonSerializable(typeof(MemoryStatusResult))]
[JsonSerializable(typeof(MemoryAddNoteResult))]
[JsonSerializable(typeof(MemoryListRecentResult))]
[JsonSerializable(typeof(MemorySettingsResult))]
[JsonSerializable(typeof(MemoryRecordTurnResult))]
[JsonSerializable(typeof(MemoryScopeResult))]
internal sealed partial class MemoryToolJsonContext : JsonSerializerContext
{
    /// <summary>
    /// 中文明文序列化实例，避免中文被转义为 \uXXXX。
    /// </summary>
    public static MemoryToolJsonContext Chinese { get; } = new(new JsonSerializerOptions
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    });
}
