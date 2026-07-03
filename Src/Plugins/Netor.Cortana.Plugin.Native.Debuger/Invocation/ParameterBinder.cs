using System.Text;
using System.Text.Json;

using Netor.Cortana.Plugin.Native.Debugger.Discovery;

namespace Netor.Cortana.Plugin.Native.Debugger.Invocation;

/// <summary>
/// 参数绑定器 - 支持位置参数、命名参数(--name value)、JSON、交互式
/// </summary>
public static class ParameterBinder
{
    /// <summary>
    /// 自动检测格式并绑定参数
    /// </summary>
    public static object[] Bind(ToolMetadata tool, string? input)
    {
        if (tool.Parameters.Length == 0)
            return [];

        if (string.IsNullOrWhiteSpace(input))
            return BindDefaults(tool);

        var trimmed = input.TrimStart();
        if (trimmed.StartsWith('{'))
            return BindFromJson(tool, input);

        var tokens = Tokenize(input);
        if (tokens.Count > 0 && tokens[0].StartsWith("--"))
            return BindFromNamed(tool, tokens);

        return BindFromPositional(tool, tokens);
    }

    /// <summary>
    /// 解析命令行参数（支持带引号的字符串）
    /// </summary>
    public static List<string> Tokenize(string input)
    {
        var args = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        for (int i = 0; i < input.Length; i++)
        {
            var c = input[i];
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ' ' && !inQuotes)
            {
                if (current.Length > 0)
                {
                    args.Add(current.ToString());
                    current.Clear();
                }
            }
            else
                current.Append(c);
        }

        if (current.Length > 0)
            args.Add(current.ToString());

        return args;
    }

    /// <summary>
    /// 按位置绑定参数
    /// </summary>
    public static object[] BindFromPositional(ToolMetadata tool, List<string> tokens)
    {
        var parameters = tool.Parameters;
        var result = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            var argValue = i < tokens.Count ? tokens[i] : null;

            if (argValue == null)
            {
                if (param.HasDefaultValue)
                    result[i] = param.DefaultValue!;
                else
                    result[i] = param.ParameterType.IsValueType
                        ? Activator.CreateInstance(param.ParameterType)!
                        : null!;
            }
            else
            {
                result[i] = TypeConverter.Convert(argValue, param.ParameterType);
            }
        }

        return result;
    }

    /// <summary>
    /// 按命名参数绑定 (--name value 格式)
    /// </summary>
    public static object[] BindFromNamed(ToolMetadata tool, List<string> tokens)
    {
        var parameters = tool.Parameters;
        var result = new object[parameters.Length];
        var provided = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].StartsWith("--") && i + 1 < tokens.Count)
            {
                var name = tokens[i][2..];
                provided[name] = tokens[++i];
            }
        }

        for (int i = 0; i < parameters.Length; i++)
        {
            var param = parameters[i];
            if (provided.TryGetValue(param.Name!, out var value))
            {
                result[i] = TypeConverter.Convert(value, param.ParameterType);
            }
            else if (param.HasDefaultValue)
            {
                result[i] = param.DefaultValue!;
            }
            else
            {
                throw new ArgumentException(
                    $"工具 '{tool.ToolName}' 缺少必需参数 '--{param.Name}'。");
            }
        }

        return result;
    }

    /// <summary>
    /// 从 JSON 绑定参数
    /// </summary>
    public static object[] BindFromJson(ToolMetadata tool, string json)
    {
        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var parameters = tool.Parameters;

        if (parameters.Length == 1)
        {
            var deserialized = JsonSerializer.Deserialize(json, parameters[0].ParameterType, options);
            if (deserialized == null && !parameters[0].HasDefaultValue)
                throw new ArgumentException($"参数 '{parameters[0].Name}' 不能为 null。");
            return [deserialized!];
        }

        return BindDefaults(tool);
    }

    /// <summary>
    /// 使用默认值绑定所有参数
    /// </summary>
    public static object[] BindDefaults(ToolMetadata tool)
    {
        var parameters = tool.Parameters;
        var result = new object[parameters.Length];

        for (int i = 0; i < parameters.Length; i++)
        {
            if (parameters[i].HasDefaultValue)
                result[i] = parameters[i].DefaultValue!;
            else
                throw new ArgumentException(
                    $"工具 '{tool.ToolName}' 缺少必需参数 '{parameters[i].Name}'。");
        }

        return result;
    }
}
