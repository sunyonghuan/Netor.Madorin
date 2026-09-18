using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

using Netor.Madorin.Plugin.Native.Generator.Diagnostics;

namespace Netor.Madorin.Plugin.Native.Generator.Analysis;

/// <summary>
/// 全项目扫描所有标记了 [Tool] 的类，提取工具方法信息。
/// </summary>
internal static class ToolClassAnalyzer
{
    private const string ToolAttributeName = "Netor.Madorin.Plugin.Native.ToolAttribute";
    private const string LegacyToolAttributeName = "Netor.Cortana.Plugin.ToolAttribute";
    private const string ParameterAttributeName = "Netor.Madorin.Plugin.Native.ParameterAttribute";
    private const string LegacyParameterAttributeName = "Netor.Cortana.Plugin.ParameterAttribute";

    /// <summary>
    /// 扫描编译上下文中所有标记了 [Tool] 的类，提取工具类和工具方法信息。
    /// 同时检测含有 [Tool] 方法但类上未标记 [Tool] 的情况（CNPG005）。
    /// </summary>
    public static List<ToolClassInfo> ScanAll(
        Compilation compilation,
        string pluginId,
        Action<Diagnostic> reportDiagnostic)
    {
        var result = new List<ToolClassInfo>();

        foreach (var tree in compilation.SyntaxTrees)
        {
            var semanticModel = compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var classSyntax in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                var classSymbol = semanticModel.GetDeclaredSymbol(classSyntax) as INamedTypeSymbol;
                if (classSymbol == null)
                    continue;

                var hasToolAttrOnClass = classSymbol.GetAttributes().Any(IsToolAttribute);
                var hasToolMethods = classSymbol.GetMembers()
                    .OfType<IMethodSymbol>()
                    .Any(m => m.GetAttributes().Any(IsToolAttribute));

                if (!hasToolAttrOnClass && !hasToolMethods)
                    continue;

                if (!hasToolAttrOnClass && hasToolMethods)
                {
                    reportDiagnostic(Diagnostic.Create(
                        DiagnosticDescriptors.ToolMethodWithoutToolClass,
                        classSyntax.Identifier.GetLocation(),
                        classSymbol.Name));
                    continue;
                }

                var toolClassInfo = AnalyzeToolClass(classSymbol, classSyntax, pluginId, reportDiagnostic);
                if (toolClassInfo != null)
                    result.Add(toolClassInfo);
            }
        }

        return result;
    }

    private static ToolClassInfo? AnalyzeToolClass(
        INamedTypeSymbol classSymbol,
        ClassDeclarationSyntax classSyntax,
        string pluginId,
        Action<Diagnostic> reportDiagnostic)
    {
        if (classSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotPublicClass,
                classSyntax.Identifier.GetLocation(),
                classSymbol.Name));
            return null;
        }

        var methods = new List<ToolMethodInfo>();

        foreach (var member in classSymbol.GetMembers().OfType<IMethodSymbol>())
        {
            var toolAttr = member.GetAttributes().FirstOrDefault(IsToolAttribute);
            if (toolAttr == null)
                continue;

            var methodInfo = AnalyzeMethod(member, classSymbol, pluginId, toolAttr, reportDiagnostic);
            if (methodInfo != null)
                methods.Add(methodInfo);
        }

        if (methods.Count == 0)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotPublicClass,
                classSyntax.Identifier.GetLocation(),
                classSymbol.Name));
            return null;
        }

        return new ToolClassInfo(classSymbol, methods);
    }

    private static ToolMethodInfo? AnalyzeMethod(
        IMethodSymbol methodSymbol,
        INamedTypeSymbol classSymbol,
        string pluginId,
        AttributeData toolAttr,
        Action<Diagnostic> reportDiagnostic)
    {
        if (methodSymbol.DeclaredAccessibility != Accessibility.Public)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.NotPublicMethod,
                methodSymbol.Locations.FirstOrDefault(),
                classSymbol.Name,
                methodSymbol.Name));
            return null;
        }

        var namedArgs = toolAttr.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);
        string? customName = null;
        if (namedArgs.TryGetValue("Name", out var nameValue) && nameValue.Value is string n)
            customName = n;

        string description = "";
        if (namedArgs.TryGetValue("Description", out var descValue) && descValue.Value is string d)
            description = d;

        var category = GetNamedArgString(namedArgs, "Category");
        var riskLevel = GetNamedArgEnumName(namedArgs, "RiskLevel");
        var idempotent = GetNamedArgNullableBool(namedArgs, "Idempotent");
        var tags = GetNamedArgOptionalStringArray(namedArgs, "Tags");
        var searchHints = GetNamedArgOptionalStringArray(namedArgs, "SearchHints");

        string methodSnakeName;
        if (!string.IsNullOrEmpty(customName))
        {
            methodSnakeName = customName!;
        }
        else
        {
            if (TypeMapper.IsSnakeCase(methodSymbol.Name))
            {
                reportDiagnostic(Diagnostic.Create(
                    DiagnosticDescriptors.AlreadySnakeCase,
                    methodSymbol.Locations.FirstOrDefault(),
                    methodSymbol.Name));
            }

            methodSnakeName = TypeMapper.ToSnakeCase(methodSymbol.Name);
        }

        var fullToolName = ToolNameGenerator.GenerateFullName(pluginId, methodSnakeName);

        if (!ToolNameGenerator.IsValidToolNamePart(methodSnakeName))
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.InvalidToolName,
                methodSymbol.Locations.FirstOrDefault(),
                methodSymbol.Name,
                methodSnakeName));
            return null;
        }

        var parameters = new List<ToolParamInfo>();
        bool hasInvalidParam = false;

        foreach (var param in methodSymbol.Parameters)
        {
            var paramInfo = AnalyzeParameter(param, classSymbol.Name, methodSymbol.Name, reportDiagnostic);
            if (paramInfo == null)
            {
                hasInvalidParam = true;
                continue;
            }
            parameters.Add(paramInfo);
        }

        if (hasInvalidParam)
            return null;

        var (isAsync, asyncInnerType) = TypeMapper.UnwrapAsync(methodSymbol.ReturnType);
        bool isValueTask = false;
        if (isAsync && methodSymbol.ReturnType is INamedTypeSymbol namedReturnType)
        {
            var fullName = namedReturnType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            isValueTask = fullName.StartsWith("global::System.Threading.Tasks.ValueTask");
        }

        return new ToolMethodInfo(
            className: classSymbol.Name,
            methodName: methodSymbol.Name,
            fullToolName: fullToolName,
            methodSnakeName: methodSnakeName,
            description: description,
            category: category,
            riskLevel: riskLevel,
            idempotent: idempotent,
            tags: tags,
            searchHints: searchHints,
            parameters: parameters,
            returnType: methodSymbol.ReturnType,
            isAsync: isAsync,
            asyncInnerType: asyncInnerType,
            isValueTask: isValueTask,
            methodSymbol: methodSymbol);
    }

    private static ToolParamInfo? AnalyzeParameter(
        IParameterSymbol paramSymbol,
        string className,
        string methodName,
        Action<Diagnostic> reportDiagnostic)
    {
        var jsonType = TypeMapper.MapToJsonType(paramSymbol.Type);
        if (jsonType == null)
        {
            reportDiagnostic(Diagnostic.Create(
                DiagnosticDescriptors.UnsupportedParameterType,
                paramSymbol.Locations.FirstOrDefault(),
                $"{className}.{methodName}",
                paramSymbol.Name,
                paramSymbol.Type.ToDisplayString()));
            return null;
        }

        var paramAttr = paramSymbol.GetAttributes().FirstOrDefault(IsParameterAttribute);

        string paramName = paramSymbol.Name;
        string paramDescription = "";
        bool required = true;

        if (paramAttr != null)
        {
            var namedArgs = paramAttr.NamedArguments.ToDictionary(kv => kv.Key, kv => kv.Value);

            if (namedArgs.TryGetValue("Name", out var nameVal) && nameVal.Value is string n && !string.IsNullOrEmpty(n))
                paramName = n;

            if (namedArgs.TryGetValue("Description", out var descVal) && descVal.Value is string d)
                paramDescription = d;

            if (namedArgs.TryGetValue("Required", out var reqVal) && reqVal.Value is bool r)
                required = r;
        }

        if (paramSymbol.NullableAnnotation == NullableAnnotation.Annotated)
            required = false;

        if (paramSymbol.Type is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            required = false;
        }

        if (paramSymbol.HasExplicitDefaultValue)
            required = false;

        var jsonName = TypeMapper.ToSnakeCase(paramName);

        return new ToolParamInfo(
            paramName: paramName,
            jsonName: jsonName,
            description: paramDescription,
            required: required,
            jsonType: jsonType,
            typeSymbol: paramSymbol.Type,
            codeParamName: paramSymbol.Name,
            defaultValueExpression: GetDefaultValueExpression(paramSymbol));
    }

    private static string GetDefaultValueExpression(IParameterSymbol parameter)
    {
        if (!parameter.HasExplicitDefaultValue || parameter.ExplicitDefaultValue == null)
            return "default";

        var value = parameter.ExplicitDefaultValue;
        if (value is string stringValue)
            return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(stringValue, quote: true);
        if (value is bool boolValue)
            return boolValue ? "true" : "false";
        if (value is int intValue)
            return intValue.ToString(CultureInfo.InvariantCulture);
        if (value is long longValue)
            return longValue.ToString(CultureInfo.InvariantCulture) + "L";
        if (value is float floatValue)
            return floatValue.ToString("R", CultureInfo.InvariantCulture) + "F";
        if (value is double doubleValue)
            return doubleValue.ToString("R", CultureInfo.InvariantCulture) + "D";
        if (value is decimal decimalValue)
            return decimalValue.ToString(CultureInfo.InvariantCulture) + "M";

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "default";
    }

    private static string? GetNamedArgString(Dictionary<string, TypedConstant> args, string key)
    {
        if (args.TryGetValue(key, out var value) && value.Value is string s)
            return s;
        return null;
    }

    private static bool? GetNamedArgNullableBool(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.IsNull || value.Value is not bool b)
            return null;
        return b;
    }

    private static string? GetNamedArgEnumName(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value.IsNull || value.Value is null)
            return null;

        if (value.Type is INamedTypeSymbol enumType)
        {
            foreach (var member in enumType.GetMembers().OfType<IFieldSymbol>())
            {
                if (member.HasConstantValue && Equals(member.ConstantValue, value.Value))
                    return member.Name;
            }
        }

        return value.Value.ToString();
    }

    /// <summary>
    /// 读取可选字符串数组。未写该参数返回 null（继承插件默认值）；显式写空数组返回空数组（覆盖为空）。
    /// </summary>
    private static string[]? GetNamedArgOptionalStringArray(Dictionary<string, TypedConstant> args, string key)
    {
        if (!args.TryGetValue(key, out var value))
            return null;
        if (value.IsNull)
            return Array.Empty<string>();

        return value.Values
            .Where(v => v.Value is string)
            .Select(v => (string)v.Value!)
            .ToArray();
    }

    private static bool IsToolAttribute(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();
        return name == ToolAttributeName || name == LegacyToolAttributeName;
    }

    private static bool IsParameterAttribute(AttributeData attribute)
    {
        var name = attribute.AttributeClass?.ToDisplayString();
        return name == ParameterAttributeName || name == LegacyParameterAttributeName;
    }
}
