using System.Collections.Generic;
using System.Linq;

using Microsoft.CodeAnalysis;

using Netor.Cortana.Plugin.Native.Generator.Analysis;
using Netor.Cortana.Plugin.Native.Generator.Diagnostics;

namespace Netor.Cortana.Plugin.Native.Generator.Emitters;

/// <summary>
/// 检查自定义返回类型是否需要用户手写 PluginJsonContext。
/// <para>
/// 基础类型和基础类型数组由 Generator 自动处理（手动拼接 JSON），
/// 自定义类型（自定义对象、自定义对象数组等）需要用户手写 PluginJsonContext
/// 并注册 [JsonSerializable] 属性，因为跨 Generator 限制导致无法自动生成。
/// </para>
/// </summary>
internal static class JsonContextEmitter
{
    /// <summary>
    /// 检查是否存在需要 PluginJsonContext 的自定义返回类型。
    /// 如果有自定义类型但编译中没有用户手写的 PluginJsonContext，则报诊断错误。
    /// </summary>
    public static void ValidateCustomReturnTypes(
        Compilation compilation,
        List<ToolClassInfo> toolClasses,
        System.Action<Diagnostic> reportDiagnostic)
    {
        var customTypes = CollectCustomReturnTypes(toolClasses);
        if (customTypes.Count == 0)
            return;

        // 检查编译中是否存在用户手写的 PluginJsonContext
        var hasUserContext = compilation.GetSymbolsWithName("PluginJsonContext", SymbolFilter.Type)
            .OfType<INamedTypeSymbol>()
            .Any(t => t.BaseType?.Name == "JsonSerializerContext");

        if (hasUserContext)
            return;

        // 用户未手写 PluginJsonContext，对每个自定义类型报错
        foreach (var (methodName, typeName, location) in customTypes)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.MissingPluginJsonContext,
                location,
                methodName,
                typeName));
        }
    }

    /// <summary>
    /// 收集所有 [Tool] 方法中非基础类型且非基础类型数组的返回类型。
    /// </summary>
    private static List<(string MethodName, string TypeName, Location? Location)> CollectCustomReturnTypes(
        List<ToolClassInfo> toolClasses)
    {
        var seen = new HashSet<string>();
        var result = new List<(string, string, Location?)>();

        foreach (var toolClass in toolClasses)
        {
            foreach (var method in toolClass.Methods)
            {
                var returnType = method.IsAsync ? method.AsyncInnerType : method.ReturnType;

                if (returnType == null)
                    continue;

                if (TypeMapper.IsVoid(returnType))
                    continue;

                // 跳过基础标量类型
                if (TypeMapper.MapToJsonType(returnType) != null)
                    continue;

                // 跳过基础类型数组（int[], string[] 等 → 已由 Generator 手动拼接处理）
                if (returnType is IArrayTypeSymbol arrayType && TypeMapper.MapToJsonType(arrayType.ElementType) != null)
                    continue;

                var displayName = returnType.ToDisplayString();
                if (seen.Add(displayName))
                    result.Add((method.MethodName, displayName, method.MethodSymbol.Locations.FirstOrDefault()));
            }
        }

        return result;
    }
}
