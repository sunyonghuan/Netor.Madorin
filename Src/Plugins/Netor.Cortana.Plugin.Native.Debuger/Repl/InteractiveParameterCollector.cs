using System.Reflection;

using Netor.Cortana.Plugin.Native.Debugger.Discovery;
using Netor.Cortana.Plugin.Native.Debugger.Invocation;

namespace Netor.Cortana.Plugin.Native.Debugger.Repl;

/// <summary>
/// 交互式参数收集器 - 逐个引导用户输入参数
/// </summary>
public static class InteractiveParameterCollector
{
    /// <summary>
    /// 逐个引导用户输入参数，返回绑定后的参数数组。返回 null 表示用户取消。
    /// </summary>
    public static object[]? Collect(ToolMetadata tool)
    {
        var parameters = tool.Parameters;
        if (parameters.Length == 0)
            return [];

        var toolAttr = tool.MethodInfo.GetCustomAttribute<ToolAttribute>();
        Console.WriteLine($"\n📖 {tool.ToolName}");
        if (!string.IsNullOrEmpty(toolAttr?.Description))
            Console.WriteLine($"   {toolAttr.Description}");
        Console.WriteLine($"\n  逐个输入参数（直接回车使用默认值，输入 !q 取消）:\n");

        var result = new object[parameters.Length];
        for (int i = 0; i < parameters.Length; i++)
        {
            var value = CollectSingleParameter(parameters[i], i, parameters.Length);
            if (value == null)
            {
                Console.WriteLine("\n⚠️ 已取消。\n");
                return null;
            }
            result[i] = value;
            Console.WriteLine();
        }

        return result;
    }

    /// <summary>
    /// 收集单个参数值，返回 null 表示用户取消
    /// </summary>
    private static object? CollectSingleParameter(ParameterInfo param, int index, int total)
    {
        var paramName = param.Name ?? "unknown";
        var paramType = TypeConverter.GetFriendlyTypeName(param.ParameterType);
        var paramDesc = param.GetCustomAttribute<ParameterAttribute>()?.Description;
        var required = !param.HasDefaultValue;
        var tag = required ? "必填" : "可选";

        Console.Write($"  [{index + 1}/{total}] ");
        Console.Write($"{paramName} ({paramType}, {tag})");
        if (!string.IsNullOrEmpty(paramDesc))
            Console.Write($" - {paramDesc}");
        Console.WriteLine();

        if (param.HasDefaultValue)
            Console.WriteLine($"        默认值: {param.DefaultValue ?? "null"}");

        Console.Write("  > ");
        var input = Console.ReadLine()?.Trim();

        if (input == "!q")
            return null;

        if (string.IsNullOrEmpty(input))
        {
            if (param.HasDefaultValue)
            {
                Console.WriteLine($"        (使用默认值: {param.DefaultValue ?? "null"})");
                return param.DefaultValue!;
            }

            // Required parameter: prompt again
            Console.WriteLine($"        ⚠️ 此参数为必填项，请输入值：");
            Console.Write("  > ");
            input = Console.ReadLine()?.Trim();

            if (string.IsNullOrEmpty(input) || input == "!q")
                return null;
        }

        return TypeConverter.Convert(input, param.ParameterType);
    }
}
