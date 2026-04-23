using Netor.Cortana.Plugin.Process.Generator.Analysis;

using System.Text;

namespace Netor.Cortana.Plugin.Process.Generator.Emitters;

/// <summary>
/// 生成 plugin.json 清单文件的内容。
/// 通过 <c>AddSource("plugin.json.cs", ...)</c> 落盘，.targets 把 // 注释前缀去掉后写到输出目录。
/// </summary>
internal static class PluginJsonEmitter
{
    /// <summary>
    /// 生成 plugin.json 内容。Process 运行时的 runtime 字段固定为 <c>process</c>，
    /// <c>command</c> 为相对插件目录的可执行文件名。
    /// </summary>
    public static string Emit(PluginClassInfo plugin, string? assemblyName)
    {
        var exeName = (assemblyName ?? "Plugin") + ".exe";

        var sb = new StringBuilder();
        sb.AppendLine("//{");
        sb.AppendLine($"//    \"id\": \"{EscapeJson(plugin.Id)}\",");
        sb.AppendLine($"//    \"name\": \"{EscapeJson(plugin.Name)}\",");
        sb.AppendLine($"//    \"version\": \"{EscapeJson(plugin.Version)}\",");
        sb.AppendLine($"//    \"description\": \"{EscapeJson(plugin.Description)}\",");
        sb.AppendLine("//    \"runtime\": \"process\",");
        sb.AppendLine($"//    \"command\": \"{EscapeJson(exeName)}\",");
        sb.AppendLine("//    \"minHostVersion\": \"1.0.0\"");
        sb.Append("//}");

        return sb.ToString();
    }

    private static string EscapeJson(string s)
        => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r");
}
