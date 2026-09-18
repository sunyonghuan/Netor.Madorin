using System.Text;
using System.Text.Json;

namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 把 Native 工具的 parameters 列表转成模型可消费的 JSON Schema。
/// 禁止再暴露单一 argsJson 字符串参数。
/// </summary>
internal static class NativeToolJsonSchema
{
    public static JsonElement Create(NativeToolInfo toolInfo)
    {
        ArgumentNullException.ThrowIfNull(toolInfo);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("type", "object");
            writer.WritePropertyName("properties");
            writer.WriteStartObject();

            var required = new List<string>();
            if (toolInfo.Parameters is { Count: > 0 })
            {
                foreach (var parameter in toolInfo.Parameters)
                {
                    if (string.IsNullOrWhiteSpace(parameter.Name))
                    {
                        continue;
                    }

                    writer.WritePropertyName(parameter.Name);
                    writer.WriteStartObject();
                    writer.WriteString("type", NormalizeType(parameter.Type));
                    if (!string.IsNullOrWhiteSpace(parameter.Description))
                    {
                        writer.WriteString("description", parameter.Description);
                    }

                    writer.WriteEndObject();
                    if (parameter.Required)
                    {
                        required.Add(parameter.Name);
                    }
                }
            }

            writer.WriteEndObject();
            if (required.Count > 0)
            {
                writer.WritePropertyName("required");
                writer.WriteStartArray();
                foreach (var name in required)
                {
                    writer.WriteStringValue(name);
                }

                writer.WriteEndArray();
            }

            writer.WriteBoolean("additionalProperties", true);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(stream.ToArray());
        return document.RootElement.Clone();
    }

    private static string NormalizeType(string? type)
    {
        return type?.Trim().ToLowerInvariant() switch
        {
            "integer" or "int" or "int32" or "int64" or "long" => "integer",
            "number" or "double" or "float" or "decimal" => "number",
            "boolean" or "bool" => "boolean",
            "array" => "array",
            "object" => "object",
            _ => "string"
        };
    }
}
