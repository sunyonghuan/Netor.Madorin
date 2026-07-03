using System;
using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

using Netor.Cortana.Plugin.Process.Generator.Diagnostics;

namespace Netor.Cortana.Plugin.Process.Generator.Analysis;

internal static class ToolNameGenerator
{
    public static string GenerateFullName(string pluginId, string methodSnake)
    {
        var prefix = pluginId + "_";
        return methodSnake.StartsWith(prefix, StringComparison.Ordinal)
            ? methodSnake
            : $"{pluginId}_{methodSnake}";
    }

    public static bool IsValidToolNamePart(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
            if (char.IsUpper(c)) return false;
        }
        return true;
    }

    public static void CheckConflicts(
        List<ToolClassInfo> toolClasses,
        Action<Diagnostic> reportDiagnostic)
    {
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
            if (kvp.Value.Count <= 1) continue;

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
