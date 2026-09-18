using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Netor.Madorin.Plugin.Native;

/// <summary>
/// 在发给插件子进程之前，把模型参数收成生成代码认识的 snake_case 对象。
/// 兼容 argsJson 外壳、camelCase 键、缺键和字符串数字。
/// </summary>
public static class NativeToolArgumentBinder
{
    public static string Normalize(string? argsJson, IReadOnlyList<NativeToolParameter>? parameters)
    {
        var node = ParseNode(argsJson);
        node = Unwrap(node);
        if (node is not JsonObject obj)
        {
            obj = new JsonObject();
        }

        if (parameters is not { Count: > 0 })
        {
            return obj.ToJsonString();
        }

        var rewritten = new JsonObject();
        foreach (var parameter in parameters)
        {
            if (string.IsNullOrWhiteSpace(parameter.Name))
            {
                continue;
            }

            rewritten[parameter.Name] = TryFind(obj, parameter.Name, out var found)
                ? NativeJsonValueCoercion.Coerce(found, parameter.Type)
                : NativeJsonValueCoercion.Default(parameter.Type);
        }

        foreach (var property in obj)
        {
            if (!rewritten.ContainsKey(property.Key))
            {
                rewritten[property.Key] = property.Value?.DeepClone();
            }
        }

        return rewritten.ToJsonString();
    }

    internal static string FromFunctionArguments(
        IEnumerable<KeyValuePair<string, object?>> arguments,
        IReadOnlyList<NativeToolParameter>? parameters)
    {
        var obj = new JsonObject();
        foreach (var pair in arguments)
        {
            if (string.IsNullOrWhiteSpace(pair.Key)) continue;
            obj[pair.Key] = ToNode(pair.Value);
        }

        return Normalize(obj.ToJsonString(), parameters);
    }

    private static JsonNode Unwrap(JsonNode? node)
    {
        if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            node = ParseNode(text);
        }

        if (node is JsonObject obj && TryFind(obj, "argsJson", out var nested) && nested is not null)
        {
            if (nested is JsonValue nestedValue && nestedValue.TryGetValue<string>(out var nestedText))
            {
                return ParseNode(nestedText) ?? new JsonObject();
            }

            if (nested is JsonObject nestedObject)
            {
                return nestedObject;
            }
        }

        return node ?? new JsonObject();
    }

    private static JsonNode? ParseNode(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(json);
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static bool TryFind(JsonObject obj, string name, out JsonNode? value)
    {
        if (obj.TryGetPropertyValue(name, out value))
        {
            return true;
        }

        var camel = ToCamelCase(name);
        foreach (var property in obj)
        {
            if (string.Equals(property.Key, name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(property.Key, camel, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string ToCamelCase(string snakeOrName)
    {
        if (string.IsNullOrEmpty(snakeOrName) || !snakeOrName.Contains('_'))
        {
            return snakeOrName;
        }

        var parts = snakeOrName.Split('_', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return snakeOrName;
        return parts[0] + string.Concat(parts.Skip(1).Select(static part =>
            char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static JsonNode? ToNode(object? value)
    {
        return value switch
        {
            null => null,
            JsonNode node => node.DeepClone(),
            JsonElement element => JsonNode.Parse(element.GetRawText()),
            string text => JsonValue.Create(text),
            bool flag => JsonValue.Create(flag),
            byte or sbyte or short or ushort or int or uint or long or ulong => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
            float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
            _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture))
        };
    }
}
