using Microsoft.Extensions.AI;

using Netor.Cortana.AI.Persistence;
using Netor.Cortana.Entitys;

using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Netor.Cortana.AI;

/// <summary>
/// ChatMessageEntity 的扩展方法。
/// </summary>
public static class ChatMessageExtensions
{
    /// <summary>
    /// 将消息实体的 Role 字符串转换为 <see cref="ChatRole"/>。
    /// </summary>
    public static ChatRole ToChatRole(this ChatMessageEntity message)
    {
        return new ChatRole(message.Role);
    }

    /// <summary>
    /// 将运行时消息内容压平成可落库的文本快照，避免工具调用/结果因 Text 为空而丢失。
    /// </summary>
    public static string ToPersistedContent(this ChatMessage message)
    {
        return message.Text;
        return BuildPersistedContent(message.Text, message.Contents);
    }

    /// <summary>
    /// 将内容集合压平成文本，供数据库持久化和后续上下文恢复使用。
    /// </summary>
    public static string BuildPersistedContent(string? fallbackText, IList<AIContent>? contents)
    {
        return fallbackText ?? string.Empty;
        if (contents is null || contents.Count == 0)
        {
            return fallbackText ?? string.Empty;
        }

        var parts = new List<string>(contents.Count);

        foreach (var content in contents)
        {
            var rendered = RenderContent(content);
            if (!string.IsNullOrWhiteSpace(rendered))
            {
                parts.Add(rendered.Trim());
            }
        }

        if (parts.Count == 0)
        {
            return fallbackText ?? string.Empty;
        }

        return string.Join("\n\n", parts);
    }

    /// <summary>
    /// 将 AIContent 渲染为可读的文本字符串。
    /// 根据内容类型分发到对应的渲染方法。
    /// </summary>
    /// <param name="content">AI 内容实例</param>
    /// <returns>渲染后的文本，若无法渲染则返回 null</returns>
    private static string? RenderContent(AIContent content)
    {
        return content switch
        {
            TextContent text => text.Text,
            //TextReasoningContent reasoning => reasoning.Text,
            FunctionCallContent functionCall => RenderFunctionCall(functionCall),
            FunctionResultContent functionResult => RenderFunctionResult(functionResult),
            DataContent data => RenderDataContent(data),
            _ => content.ToString()
        };
    }

    /// <summary>
    /// 将函数调用内容渲染为格式化的文本字符串。
    /// 包含方法名称、调用ID和参数字典。
    /// </summary>
    /// <param name="functionCall">函数调用内容</param>
    /// <returns>格式化的文本表示</returns>
    private static string RenderFunctionCall(FunctionCallContent functionCall)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[工具调用]");
        builder.Append("名称: ").Append(functionCall.Name);

        if (!string.IsNullOrWhiteSpace(functionCall.CallId))
        {
            builder.AppendLine();
            builder.Append("调用ID: ").Append(functionCall.CallId);
        }

        builder.AppendLine();
        builder.Append("参数: ").Append(RenderValue(functionCall.Arguments));

        return builder.ToString();
    }

    /// <summary>
    /// 将函数执行结果渲染为格式化的文本字符串。
    /// 包含调用ID、异常信息或执行结果。
    /// </summary>
    /// <param name="functionResult">函数执行结果内容</param>
    /// <returns>格式化的文本表示</returns>
    private static string RenderFunctionResult(FunctionResultContent functionResult)
    {
        var builder = new StringBuilder();
        builder.AppendLine("[工具结果]");

        if (!string.IsNullOrWhiteSpace(functionResult.CallId))
        {
            builder.Append("调用ID: ").Append(functionResult.CallId).AppendLine();
        }

        if (functionResult.Exception is not null)
        {
            builder.Append("异常: ").Append(functionResult.Exception.Message);
            return builder.ToString();
        }

        builder.Append("结果: ").Append(RenderValue(functionResult.Result));
        return builder.ToString();
    }

    /// <summary>
    /// 将数据内容渲染为文本或二进制描述。
    /// 文本类型数据直接返回内容，二进制数据返回元信息描述。
    /// </summary>
    /// <param name="data">数据内容</param>
    /// <returns>渲染后的文本或描述字符串</returns>
    private static string RenderDataContent(DataContent data)
    {
        var descriptor = string.IsNullOrWhiteSpace(data.Name)
            ? data.MediaType
            : $"{data.Name} ({data.MediaType})";

        if (data.HasTopLevelMediaType("text")
            || string.Equals(data.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || data.MediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase))
        {
            var text = Encoding.UTF8.GetString(data.Data.Span);
            if (string.IsNullOrWhiteSpace(descriptor))
            {
                return text;
            }

            return $"[文件内容] {descriptor}\n{text}";
        }

        return $"[二进制内容] {descriptor}, {data.Data.Length.ToString(CultureInfo.InvariantCulture)} bytes";
    }

    /// <summary>
    /// 将任意类型的值渲染为统一的文本字符串表示。
    /// 支持常见原始类型、字典、枚举和 JSON 元素。
    /// </summary>
    /// <param name="value">待渲染的值</param>
    /// <returns>文本表示形式</returns>
    private static string RenderValue(object? value)
    {
        return value switch
        {
            null => "null",
            string text => text,
            JsonElement json => json.ValueKind == JsonValueKind.String ? json.GetString() ?? string.Empty : json.GetRawText(),
            bool boolean => boolean ? "true" : "false",
            byte number => number.ToString(CultureInfo.InvariantCulture),
            sbyte number => number.ToString(CultureInfo.InvariantCulture),
            short number => number.ToString(CultureInfo.InvariantCulture),
            ushort number => number.ToString(CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            uint number => number.ToString(CultureInfo.InvariantCulture),
            long number => number.ToString(CultureInfo.InvariantCulture),
            ulong number => number.ToString(CultureInfo.InvariantCulture),
            float number => number.ToString(CultureInfo.InvariantCulture),
            double number => number.ToString(CultureInfo.InvariantCulture),
            decimal number => number.ToString(CultureInfo.InvariantCulture),
            IDictionary<string, object?> dictionary => RenderDictionary(dictionary),
            IEnumerable enumerable when value is not string => RenderEnumerable(enumerable),
            _ => value.ToString() ?? string.Empty
        };
    }

    /// <summary>
    /// 将字符串键值对字典渲染为 JSON 风格的对象文本。
    /// 格式：{ "key1": value1, "key2": value2 }
    /// </summary>
    /// <param name="dictionary">字典实例</param>
    /// <returns>JSON 格式的字符串表示</returns>
    private static string RenderDictionary(IDictionary<string, object?> dictionary)
    {
        if (dictionary.Count == 0)
        {
            return "{}";
        }

        var builder = new StringBuilder();
        builder.Append('{');
        var index = 0;

        foreach (var pair in dictionary)
        {
            if (index++ > 0)
            {
                builder.Append(", ");
            }

            builder.Append(pair.Key).Append(": ").Append(RenderValue(pair.Value));
        }

        builder.Append('}');
        return builder.ToString();
    }

    /// <summary>
    /// 将可枚举集合渲染为逗号分隔的数组文本。
    /// 格式：[item1, item2, item3]
    /// </summary>
    /// <param name="values">可枚举集合</param>
    /// <returns>数组格式的字符串表示</returns>
    private static string RenderEnumerable(IEnumerable values)
    {
        var items = new List<string>();

        foreach (var item in values)
        {
            items.Add(RenderValue(item));
        }

        return $"[{string.Join(", ", items)}]";
    }

    // ──────────────────── 结构化内容持久化 ────────────────────

    /// <summary>
    /// 将 <see cref="ChatMessage.Contents"/> 转换为 AOT 安全的结构化 JSON 快照。
    /// 返回空字符串表示无结构化内容（仅文本），调用方可跳过落库。
    /// </summary>
    public static string BuildContentsJson(IList<AIContent>? contents)
    {
        if (contents is null || contents.Count == 0) return string.Empty;

        var list = new List<PersistedContent>(contents.Count);
        foreach (var c in contents)
        {
            var persisted = MapToPersisted(c);
            if (persisted is not null) list.Add(persisted);
        }

        if (list.Count == 0) return string.Empty;
        return JsonSerializer.Serialize(list, PersistedContentJsonContext.Default.ListPersistedContent);
    }

    /// <summary>
    /// 从数据库的 ContentsJson 还原出 AIContent 列表。解析失败或空字符串时返回 <c>null</c>。
    /// </summary>
    public static IList<AIContent>? ParseContentsJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        List<PersistedContent>? list;
        try
        {
            list = JsonSerializer.Deserialize(json, PersistedContentJsonContext.Default.ListPersistedContent);
        }
        catch (JsonException)
        {
            return null;
        }

        if (list is null || list.Count == 0) return null;

        var result = new List<AIContent>(list.Count);
        foreach (var p in list)
        {
            var content = MapFromPersisted(p);
            if (content is not null) result.Add(content);
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    /// 将运行时 AIContent 映射为可持久化的 PersistedContent 结构。
    /// 处理各种内容类型（文本、推理、函数调用、函数结果、数据等）。
    /// </summary>
    /// <param name="content">源 AI 内容</param>
    /// <returns>持久化内容结构，若无法映射则返回 null</returns>
    private static PersistedContent? MapToPersisted(AIContent content)
    {
        switch (content)
        {
            case TextContent text:
                return new PersistedContent { Kind = "text", Text = text.Text };

            case TextReasoningContent reasoning:
                return new PersistedContent
                {
                    Kind = "reasoning",
                    Text = reasoning.Text,
                    ProtectedData = reasoning.ProtectedData
                };

            case FunctionCallContent call:
                string? argsJson = null;
                if (call.Arguments is not null && call.Arguments.Count > 0)
                {
                    // 把参数值规范化为 JsonElement，避免 object 多态要求反射
                    try
                    {
                        var normalized = new Dictionary<string, JsonElement>(call.Arguments.Count, StringComparer.Ordinal);
                        foreach (var kv in call.Arguments)
                        {
                            normalized[kv.Key] = NormalizeToJsonElement(kv.Value);
                        }
                        argsJson = JsonSerializer.Serialize(
                            normalized,
                            PersistedArgumentsJsonContext.Default.DictionaryStringJsonElement);
                    }
                    catch (Exception)
                    {
                        argsJson = null; // 序列化失败则降级（文本快照已覆盖人类可读）
                    }
                }
                return new PersistedContent
                {
                    Kind = "functionCall",
                    CallId = call.CallId,
                    Name = call.Name,
                    ArgumentsJson = argsJson
                };

            case FunctionResultContent result:
                string? resultJson = null;
                if (result.Result is not null)
                {
                    resultJson = result.Result switch
                    {
                        string s => s,
                        JsonElement je => je.GetRawText(),
                        _ => result.Result.ToString()
                    };
                }
                return new PersistedContent
                {
                    Kind = "functionResult",
                    CallId = result.CallId,
                    ResultJson = resultJson,
                    ExceptionMessage = result.Exception?.Message
                };

            case McpServerToolCallContent mcpToolCall:
                return new PersistedContent
                {
                    Kind = "toolCall",
                    CallId = mcpToolCall.CallId,
                    Name = mcpToolCall.Name,
                    ServerName = mcpToolCall.ServerName,
                    RawArgumentsJson = SerializeToolArguments(mcpToolCall.Arguments)
                };

            case ToolCallContent toolCall:
                return new PersistedContent
                {
                    Kind = "toolCall",
                    CallId = toolCall.CallId
                };

            case McpServerToolResultContent mcpToolResult:
                return new PersistedContent
                {
                    Kind = "toolResult",
                    CallId = mcpToolResult.CallId,
                    RawOutputsJson = SerializeOutputs(mcpToolResult.Outputs)
                };

            case ToolResultContent toolResult:
                return new PersistedContent
                {
                    Kind = "toolResult",
                    CallId = toolResult.CallId
                };

            case DataContent data:
                // 仅存引用信息，不内联二进制（由 ChatMessageAssets 表维护）
                return new PersistedContent
                {
                    Kind = "data",
                    MediaType = data.MediaType,
                    Uri = data.Uri
                };

            default:
                // 未识别的内容类型：降级为文本
                var text2 = content.ToString();
                if (string.IsNullOrEmpty(text2)) return null;
                return new PersistedContent { Kind = "text", Text = text2 };
        }
    }

    /// <summary>
    /// 将持久化的 PersistedContent 反向映射回运行时 AIContent。
    /// 用于从数据库恢复对话上下文。
    /// </summary>
    /// <param name="p">持久化内容</param>
    /// <returns>运行时 AI 内容，若无法恢复则返回 null</returns>
    private static AIContent? MapFromPersisted(PersistedContent p)
    {
        switch (p.Kind)
        {
            case "text":
                return string.IsNullOrEmpty(p.Text) ? null : new TextContent(p.Text);

            case "reasoning":
                var reasoning = new TextReasoningContent(p.Text)
                {
                    ProtectedData = p.ProtectedData
                };
                return reasoning;

            case "functionCall":
                IDictionary<string, object?>? args = null;
                if (!string.IsNullOrWhiteSpace(p.ArgumentsJson))
                {
                    try
                    {
                        var dict = JsonSerializer.Deserialize(
                            p.ArgumentsJson,
                            PersistedArgumentsJsonContext.Default.DictionaryStringJsonElement);
                        if (dict is not null)
                        {
                            args = new Dictionary<string, object?>(dict.Count, StringComparer.Ordinal);
                            foreach (var kv in dict) args[kv.Key] = kv.Value;
                        }
                    }
                    catch (JsonException) { args = null; }
                }
                return new FunctionCallContent(
                    callId: p.CallId ?? string.Empty,
                    name: p.Name ?? string.Empty,
                    arguments: args);

            case "functionResult":
                object? resultObj = p.ResultJson;
                if (!string.IsNullOrEmpty(p.ExceptionMessage))
                {
                    // 异常场景：用文本形式回放，避免重新抛出历史异常
                    resultObj = $"[Exception] {p.ExceptionMessage}";
                }
                return new FunctionResultContent(
                    callId: p.CallId ?? string.Empty,
                    result: resultObj);

            case "toolCall":
                if (!string.IsNullOrWhiteSpace(p.Name))
                {
                    var mcpCall = new McpServerToolCallContent(
                        callId: p.CallId ?? string.Empty,
                        name: p.Name,
                        serverName: p.ServerName)
                    {
                        Arguments = DeserializeToolArguments(p.RawArgumentsJson)
                    };
                    return mcpCall;
                }
                return string.IsNullOrWhiteSpace(p.CallId) ? null : new ToolCallContent(p.CallId);

            case "toolResult":
                if (!string.IsNullOrWhiteSpace(p.RawOutputsJson))
                {
                    var mcpResult = new McpServerToolResultContent(p.CallId ?? string.Empty)
                    {
                        Outputs = DeserializeOutputs(p.RawOutputsJson)
                    };
                    return mcpResult;
                }
                return string.IsNullOrWhiteSpace(p.CallId) ? null : new ToolResultContent(p.CallId);

            case "data":
                // 历史 Data 内容不回灌到 AI 上下文（由 BuildContentsWithAssets 单独处理图片资源）
                return null;

            default:
                return null;
        }
    }

    /// <summary>
    /// 将任意值规范化为 JsonElement。
    /// 对常见原始类型直接构造 JSON 文本，避免反射开销。
    /// </summary>
    /// <param name="value">待规范化的值</param>
    /// <returns>规范化后的 JsonElement</returns>
    private static JsonElement NormalizeToJsonElement(object? value)
    {
        if (value is JsonElement je) return je;
        if (value is null) return JsonDocument.Parse("null").RootElement;

        // 使用不可信任的 object 序列化路径会走反射；对常见原始类型手动构造 JSON 文本更安全
        string json = value switch
        {
            string s => JsonSerializer.Serialize(s, PersistedArgumentsJsonContext.Default.String),
            bool b => b ? "true" : "false",
            int i => i.ToString(CultureInfo.InvariantCulture),
            long l => l.ToString(CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            decimal m => m.ToString(CultureInfo.InvariantCulture),
            _ => JsonSerializer.Serialize(value.ToString(), PersistedArgumentsJsonContext.Default.String)
        };
        return JsonDocument.Parse(json).RootElement;
    }

    /// <summary>
    /// 将工具调用参数字典序列化为 JSON 字符串。
    /// 参数值统一规范化为 JsonElement 后序列化。
    /// </summary>
    /// <param name="arguments">参数字典</param>
    /// <returns>JSON 字符串，序列化失败时返回 null</returns>
    private static string? SerializeToolArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return null;
        }

        try
        {
            var normalized = new Dictionary<string, JsonElement>(arguments.Count, StringComparer.Ordinal);
            foreach (var pair in arguments)
            {
                normalized[pair.Key] = NormalizeToJsonElement(pair.Value);
            }

            return JsonSerializer.Serialize(
                normalized,
                PersistedArgumentsJsonContext.Default.DictionaryStringJsonElement);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 将 JSON 字符串反序列化为工具调用参数字典。
    /// </summary>
    /// <param name="json">JSON 字符串</param>
    /// <returns>参数字典，反序列化失败或为空时返回 null</returns>
    private static IDictionary<string, object?>? DeserializeToolArguments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize(
                json,
                PersistedArgumentsJsonContext.Default.DictionaryStringJsonElement);

            if (parsed is null || parsed.Count == 0)
            {
                return null;
            }

            var result = new Dictionary<string, object?>(parsed.Count, StringComparer.Ordinal);
            foreach (var pair in parsed)
            {
                result[pair.Key] = pair.Value;
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// 将工具输出列表序列化为换行分隔的文本字符串。
    /// 仅提取各输出的文本表示。
    /// </summary>
    /// <param name="outputs">AI 内容列表</param>
    /// <returns>换行分隔的文本，若无有效内容则返回 null</returns>
    private static string? SerializeOutputs(IList<AIContent>? outputs)
    {
        if (outputs is null || outputs.Count == 0)
        {
            return null;
        }

        var textOutputs = outputs
            .Select(static o => o?.ToString())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .ToArray();

        if (textOutputs.Length == 0)
        {
            return null;
        }

        return string.Join("\n", textOutputs);
    }

    /// <summary>
    /// 将换行分隔的文本反序列化为 AI 内容列表。
    /// 每个文本行转换为一个 TextContent。
    /// </summary>
    /// <param name="json">换行分隔的文本</param>
    /// <returns>AI 内容列表，反序列化失败时返回 null</returns>
    private static IList<AIContent>? DeserializeOutputs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        var values = json.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length == 0)
        {
            return null;
        }

        return values
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Select(static s => (AIContent)new TextContent(s))
            .ToList();
    }
}

/// <summary>
/// AOT 源生成：工具调用参数字典的序列化支持。
/// 值统一规范化为 <see cref="JsonElement"/>，避免 <c>object</c> 多态触发反射。
/// </summary>
[System.Text.Json.Serialization.JsonSerializable(typeof(System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>))]
[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class PersistedArgumentsJsonContext : System.Text.Json.Serialization.JsonSerializerContext
{
}