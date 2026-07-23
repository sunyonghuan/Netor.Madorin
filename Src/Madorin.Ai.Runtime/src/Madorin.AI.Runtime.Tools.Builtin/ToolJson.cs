using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Madorin.AI.Runtime.Tools.Builtin;

internal static class ToolJson
{
    public static string Write(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            write(writer);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
