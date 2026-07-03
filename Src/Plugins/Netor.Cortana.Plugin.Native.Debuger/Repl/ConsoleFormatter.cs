using Netor.Cortana.Plugin.Native.Debugger.Discovery;
using Netor.Cortana.Plugin.Native.Debugger.Hosting;
using Netor.Cortana.Plugin.Native.Debugger.Invocation;

using System.Reflection;

namespace Netor.Cortana.Plugin.Native.Debugger.Repl;

/// <summary>
/// 控制台格式化输出（紧凑布局 + 颜色高亮）
/// </summary>
public static class ConsoleFormatter
{
    public static void PrintPluginInfo(DebugPluginHost host)
    {
        var meta = host.PluginMetadata;
        var asmName = meta.PluginType.Assembly.GetName();

        Console.WriteLine();
        Write("  🧩 ", ConsoleColor.Cyan);
        Write(meta.Name, ConsoleColor.White);
        Write($"  v{asmName.Version}  ", ConsoleColor.DarkGray);
        Write($"({host.ToolRegistry.Tools.Count} 个工具)\n", ConsoleColor.Gray);

        if (!string.IsNullOrEmpty(meta.Description))
        {
            Write($"  {meta.Description}\n", ConsoleColor.DarkGray);
        }
    }

    public static void PrintToolList(ToolRegistry registry)
    {
        Write($"\n  可用工具 ({registry.Tools.Count}):\n", ConsoleColor.Cyan);

        foreach (var kvp in registry.Tools.OrderBy(x => x.Key))
        {
            var tool = kvp.Value;
            var desc = tool.MethodInfo.GetCustomAttribute<ToolAttribute>()?.Description ?? "";
            var paramCount = tool.Parameters.Length;

            Write($"    {tool.ToolName,-24}", ConsoleColor.Yellow);
            Write($"({paramCount}参) ", ConsoleColor.DarkGray);
            Console.WriteLine(Truncate(desc, 50));
        }

        Console.WriteLine();
        Write("  命令: ", ConsoleColor.DarkGray);
        Write("h", ConsoleColor.Yellow);
        Write("=帮助  ", ConsoleColor.DarkGray);
        Write("<工具名>", ConsoleColor.Yellow);
        Write("=交互引导  ", ConsoleColor.DarkGray);
        Write("<工具名> h", ConsoleColor.Yellow);
        Write("=参数详情  ", ConsoleColor.DarkGray);
        Write("exit", ConsoleColor.Yellow);
        Write("=退出\n\n", ConsoleColor.DarkGray);
    }

    public static void PrintToolParameters(ToolRegistry registry, string toolName)
    {
        if (!registry.Tools.TryGetValue(toolName, out var tool))
        {
            PrintError($"未找到工具: {toolName}");
            return;
        }

        var toolAttr = tool.MethodInfo.GetCustomAttribute<ToolAttribute>();

        // Header
        Console.WriteLine();
        Write($"  📖 {tool.ToolName}", ConsoleColor.Cyan);
        if (!string.IsNullOrEmpty(toolAttr?.Description))
        {
            Write($" - {toolAttr.Description}", ConsoleColor.Gray);
        }
        Console.WriteLine();

        // Parameters - compact table
        if (tool.Parameters.Length > 0)
        {
            Write("\n  参数:\n", ConsoleColor.White);
            Write($"  {"名称",-18} {"类型",-14} {"必填",-6} 说明\n", ConsoleColor.DarkGray);
            Write($"  {new string('─', 18)} {new string('─', 14)} {new string('─', 6)} {new string('─', 30)}\n", ConsoleColor.DarkGray);

            foreach (var param in tool.Parameters)
            {
                var paramName = param.Name ?? "unknown";
                var paramType = TypeConverter.GetFriendlyTypeName(param.ParameterType);
                var paramDesc = param.GetCustomAttribute<ParameterAttribute>()?.Description ?? "";
                var required = param.HasDefaultValue ? "  ×" : "  ✓";
                var reqColor = param.HasDefaultValue ? ConsoleColor.DarkGray : ConsoleColor.Red;

                Write($"  {paramName,-18}", ConsoleColor.Yellow);
                Write($" {paramType,-14}", ConsoleColor.DarkCyan);
                Write($" {required,-6}", reqColor);
                Console.Write(" ");

                // Description + default value inline
                if (param.HasDefaultValue)
                {
                    var defaultVal = param.DefaultValue ?? "null";
                    Console.Write(Truncate(paramDesc, 40));
                    Write($" [默认:{defaultVal}]", ConsoleColor.DarkGray);
                }
                else
                {
                    Console.Write(Truncate(paramDesc, 55));
                }
                Console.WriteLine();
            }
        }
        else
        {
            Write("\n  (无参数)\n", ConsoleColor.DarkGray);
        }

        PrintUsageExamples(tool);
    }

    public static void PrintUsageExamples(ToolMetadata tool)
    {
        if (tool.Parameters.Length == 0) return;

        Write("\n  示例:\n", ConsoleColor.White);

        // Interactive mode
        Write("    $ ", ConsoleColor.DarkGray);
        Write(tool.ToolName, ConsoleColor.Green);
        Write("                         (交互引导)\n", ConsoleColor.DarkGray);

        // Named params mode
        Write("    $ ", ConsoleColor.DarkGray);
        Write(tool.ToolName, ConsoleColor.Green);
        foreach (var p in tool.Parameters)
        {
            Write($" --{p.Name} ", ConsoleColor.Yellow);
            Write(GetSampleValue(p), ConsoleColor.DarkCyan);
        }
        Console.WriteLine();

        // Positional mode
        Write("    $ ", ConsoleColor.DarkGray);
        Write(tool.ToolName, ConsoleColor.Green);
        foreach (var p in tool.Parameters)
        {
            var sample = GetSampleValue(p);
            var needsQuote = sample.Contains(' ') || p.ParameterType == typeof(string);
            Write(needsQuote ? $" \"{sample}\"" : $" {sample}", ConsoleColor.DarkCyan);
        }
        Write("  (位置参数)\n\n", ConsoleColor.DarkGray);
    }

    public static void PrintResult(string result)
    {
        Write("\n  ✅ ", ConsoleColor.Green);
        Console.WriteLine(result);
        Console.WriteLine();
    }

    public static void PrintError(string message)
    {
        Write($"\n  ❌ {message}\n\n", ConsoleColor.Red);
    }

    public static void PrintException(Exception ex)
    {
        Write($"\n  ❌ {ex.Message}\n", ConsoleColor.Red);
        if (System.Diagnostics.Debugger.IsAttached)
            Write($"  {ex.StackTrace}\n", ConsoleColor.DarkGray);
        Console.WriteLine();
    }

    /// <summary>
    /// 根据参数类型和描述生成示例值
    /// </summary>
    private static string GetSampleValue(ParameterInfo param)
    {
        var desc = param.GetCustomAttribute<ParameterAttribute>()?.Description ?? "";

        // If has default value, show it as sample
        if (param.HasDefaultValue && param.DefaultValue != null)
            return param.DefaultValue.ToString() ?? "null";

        // Extract example from description （匹配 "例如 xxx" 或 "例如 'xxx'"）
        var exampleMatch = System.Text.RegularExpressions.Regex.Match(desc, @"例如\s*[''\""]*([^''\""，。]+)");
        if (exampleMatch.Success)
            return exampleMatch.Groups[1].Value.Trim();

        // Type-based fallback
        var type = param.ParameterType;
        if (type == typeof(string))
        {
            if (desc.Contains("ID", StringComparison.OrdinalIgnoreCase)) return "abc123";
            if (desc.Contains("标签")) return "工作,生活";
            if (desc.Contains("标题") || desc.Contains("名")) return "示例文本";
            return "...";
        }
        if (type == typeof(int)) return "0";
        if (type == typeof(long)) return "0";
        if (type == typeof(bool)) return "true";
        if (type == typeof(double) || type == typeof(float)) return "0.0";
        if (type == typeof(DateTime) || type == typeof(DateTimeOffset)) return DateTimeOffset.Now.AddHours(1).ToString("yyyy-MM-ddTHH:mm:sszzz");
        if (type == typeof(Guid)) return "00000000-0000-0000-0000-000000000000";
        return "...";
    }

    private static void Write(string text, ConsoleColor color)
    {
        var prev = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = prev;
    }

    private static string Truncate(string text, int maxLen)
    {
        return text.Length <= maxLen ? text : text[..(maxLen - 3)] + "...";
    }
}