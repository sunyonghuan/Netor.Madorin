using System.Globalization;
using System.Text.Json.Nodes;

namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 把模型传来的松散 JSON 值收成 Native 生成代码能 GetInt32/GetBoolean/GetString 的形态。
/// </summary>
internal static class NativeJsonValueCoercion
{
    public static JsonNode Coerce(JsonNode? value, string? jsonType)
    {
        var type = NormalizeType(jsonType);
        return type switch
        {
            "integer" => CoerceInteger(value),
            "number" => CoerceNumber(value),
            "boolean" => CoerceBoolean(value),
            "array" => value as JsonArray ?? new JsonArray(),
            "object" => value as JsonObject ?? new JsonObject(),
            _ => CoerceString(value)
        };
    }

    public static JsonNode Default(string? jsonType)
    {
        return NormalizeType(jsonType) switch
        {
            "integer" => JsonValue.Create(0),
            "number" => JsonValue.Create(0d),
            "boolean" => JsonValue.Create(false),
            "array" => new JsonArray(),
            "object" => new JsonObject(),
            _ => JsonValue.Create(string.Empty)
        };
    }

    private static string NormalizeType(string? jsonType)
        => jsonType?.Trim().ToLowerInvariant() switch
        {
            "integer" or "int" or "int32" or "int64" or "long" => "integer",
            "number" or "double" or "float" or "decimal" => "number",
            "boolean" or "bool" => "boolean",
            "array" => "array",
            "object" => "object",
            _ => "string"
        };

    private static JsonNode CoerceInteger(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<long>(out var number)) return JsonValue.Create(number);
            if (jsonValue.TryGetValue<double>(out var real)) return JsonValue.Create((long)real);
            if (jsonValue.TryGetValue<string>(out var text)
                && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
            {
                return JsonValue.Create(parsed);
            }
        }

        return JsonValue.Create(0L);
    }

    private static JsonNode CoerceNumber(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<double>(out var real)) return JsonValue.Create(real);
            if (jsonValue.TryGetValue<long>(out var number)) return JsonValue.Create((double)number);
            if (jsonValue.TryGetValue<string>(out var text)
                && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                return JsonValue.Create(parsed);
            }
        }

        return JsonValue.Create(0d);
    }

    private static JsonNode CoerceBoolean(JsonNode? value)
    {
        if (value is JsonValue jsonValue)
        {
            if (jsonValue.TryGetValue<bool>(out var flag)) return JsonValue.Create(flag);
            if (jsonValue.TryGetValue<string>(out var text)
                && bool.TryParse(text, out var parsed))
            {
                return JsonValue.Create(parsed);
            }

            if (jsonValue.TryGetValue<long>(out var number)) return JsonValue.Create(number != 0);
        }

        return JsonValue.Create(false);
    }

    private static JsonNode CoerceString(JsonNode? value)
    {
        if (value is null)
        {
            return JsonValue.Create(string.Empty);
        }

        if (value is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text))
        {
            return JsonValue.Create(text ?? string.Empty);
        }

        return JsonValue.Create(value.ToJsonString());
    }
}
