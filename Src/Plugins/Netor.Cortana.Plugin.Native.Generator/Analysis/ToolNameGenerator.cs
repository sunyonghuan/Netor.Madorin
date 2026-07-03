using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

using Netor.Cortana.Plugin.Native.Generator.Diagnostics;

namespace Netor.Cortana.Plugin.Native.Generator.Analysis;

/// <summary>
/// 工具名称生成器：负责方法名到 snake_case 的转换、完整工具名生成和冲突检测。
/// </summary>
internal static class ToolNameGenerator
{
    /// <summary>
    /// 生成完整的工具名：<c>{pluginId}_{methodSnake}</c>（两段式）。
    /// </summary>
    public static string GenerateFullName(string pluginId, string methodSnake)
    {
        var prefix = pluginId + "_";
        return methodSnake.StartsWith(prefix, StringComparison.Ordinal)
            ? methodSnake
            : $"{pluginId}_{methodSnake}";
    }

    /// <summary>
    /// 验证工具名部分是否合法（仅允许小写字母、数字和下划线，非空）。
    /// </summary>
    public static bool IsValidToolNamePart(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
            if (char.IsUpper(c))
                return false;
        }

        return true;
    }

    /// <summary>
    /// 检测工具名冲突，对冲突的工具方法报告 CNPG004 诊断。
    /// </summary>
    public static void CheckConflicts(
        List<ToolClassInfo> toolClasses,
        Action<Diagnostic> reportDiagnostic)
    {
        // 收集所有工具名 → (方法信息, 类名)
        var nameMap = new Dictionary<string, List<(ToolMethodInfo Method, string ClassName)>>();

        foreach (var toolClass in toolClasses)
        {
            foreach (var method in toolClass.Methods)
            {
                if (!nameMap.TryGetValue(method.FullToolName, out var list))
                {
                    list = new List<(ToolMethodInfo, string)>();
                    nameMap[method.FullToolName] = list;
                }
                list.Add((method, toolClass.ClassName));
            }
        }

        foreach (var kvp in nameMap)
        {
            if (kvp.Value.Count <= 1)
                continue;

            var locations = kvp.Value.Select(v => $"{v.ClassName}.{v.Method.MethodName}");
            var first = kvp.Value[0];
            var second = kvp.Value[1];

            foreach (var (method, className) in kvp.Value)
            {
                reportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.DuplicateToolName,
                    method.MethodSymbol.Locations.FirstOrDefault(),
                    kvp.Key,
                    $"{first.ClassName}.{first.Method.MethodName}",
                    $"{second.ClassName}.{second.Method.MethodName}"));
            }
        }
    }
}
