using System;
using System.Text;

using Microsoft.CodeAnalysis;

namespace Netor.Cortana.Plugin.Process.Generator.Analysis;

/// <summary>
/// 类型映射工具：PascalCase → snake_case，C# 类型 → JSON Schema 类型。
/// 与 Native.Generator 的 TypeMapper 保持一致。
/// </summary>
internal static class TypeMapper
{
    public static string ToSnakeCase(string pascalCase)
    {
        if (string.IsNullOrEmpty(pascalCase))
            return pascalCase;

        var sb = new StringBuilder();
        for (int i = 0; i < pascalCase.Length; i++)
        {
            var c = pascalCase[i];
            if (char.IsUpper(c))
            {
                if (i > 0)
                {
                    var prev = pascalCase[i - 1];
                    if (prev != '_')
                    {
                        bool prevIsLower = char.IsLower(prev);
                        bool nextIsLower = i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]);
                        if (prevIsLower || nextIsLower)
                            sb.Append('_');
                    }
                }
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    public static bool IsSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        foreach (var c in name)
            if (char.IsUpper(c)) return false;
        return name.Contains("_");
    }

    public static string? MapToJsonType(ITypeSymbol typeSymbol)
    {
        var displayName = GetUnderlyingTypeName(typeSymbol);
        switch (displayName)
        {
            case "System.String":
            case "string":
                return "string";
            case "System.Int32":
            case "int":
            case "System.Int64":
            case "long":
                return "integer";
            case "System.Double":
            case "double":
            case "System.Single":
            case "float":
            case "System.Decimal":
            case "decimal":
                return "number";
            case "System.Boolean":
            case "bool":
                return "boolean";
            default:
                return null;
        }
    }

    public static bool IsSupportedParameterType(ITypeSymbol typeSymbol)
        => MapToJsonType(typeSymbol) != null;

    public static string GetJsonParseExpression(ITypeSymbol typeSymbol, string elementExpr)
    {
        var displayName = GetUnderlyingTypeName(typeSymbol);
        switch (displayName)
        {
            case "System.String":
            case "string":
                return $"{elementExpr}.GetString() ?? \"\"";
            case "System.Int32":
            case "int":
                return $"{elementExpr}.GetInt32()";
            case "System.Int64":
            case "long":
                return $"{elementExpr}.GetInt64()";
            case "System.Double":
            case "double":
                return $"{elementExpr}.GetDouble()";
            case "System.Single":
            case "float":
                return $"(float){elementExpr}.GetDouble()";
            case "System.Decimal":
            case "decimal":
                return $"{elementExpr}.GetDecimal()";
            case "System.Boolean":
            case "bool":
                return $"{elementExpr}.GetBoolean()";
            default:
                return $"{elementExpr}.GetString() ?? \"\"";
        }
    }

    public static bool IsNullableValueType(ITypeSymbol typeSymbol)
        => typeSymbol is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T;

    public static bool IsNullableType(ITypeSymbol typeSymbol)
        => IsNullableValueType(typeSymbol)
            || typeSymbol.NullableAnnotation == NullableAnnotation.Annotated;

    public static string GetCSharpDefaultValueLiteral(ITypeSymbol typeSymbol, object? value)
    {
        if (value is null)
        {
            return "null";
        }

        var displayName = GetUnderlyingTypeName(typeSymbol);
        switch (displayName)
        {
            case "System.String":
            case "string":
                return EscapeStringLiteral(value.ToString() ?? string.Empty);
            case "System.Int32":
            case "int":
                return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture);
            case "System.Int64":
            case "long":
                return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture) + "L";
            case "System.Double":
            case "double":
                return Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            case "System.Single":
            case "float":
                return Convert.ToSingle(value, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";
            case "System.Decimal":
            case "decimal":
                return Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture).ToString(System.Globalization.CultureInfo.InvariantCulture) + "m";
            case "System.Boolean":
            case "bool":
                return (bool)value ? "true" : "false";
            default:
                return "null";
        }
    }

    /// <summary>
    /// 把返回值转换为工具协议要求的字符串形式。
    /// Process 返回值原样作为 <c>HostResponse.Data</c>，不做 JSON 包裹。
    /// </summary>
    public static string GetReturnConvertExpression(ITypeSymbol returnType, string resultExpr)
    {
        var displayName = GetUnderlyingTypeName(returnType);
        switch (displayName)
        {
            case "System.String":
            case "string":
                return resultExpr;
            case "System.Int32":
            case "int":
            case "System.Int64":
            case "long":
            case "System.Double":
            case "double":
            case "System.Single":
            case "float":
            case "System.Decimal":
            case "decimal":
            case "System.Boolean":
            case "bool":
                return $"{resultExpr}.ToString()";
            case "System.Void":
            case "void":
                return "\"ok\"";
            default:
                if (returnType is IArrayTypeSymbol arrayType)
                {
                    var elementJsonType = MapToJsonType(arrayType.ElementType);
                    if (elementJsonType != null)
                    {
                        if (elementJsonType == "string")
                            return $"\"[\" + string.Join(\",\", global::System.Linq.Enumerable.Select({resultExpr}, e => \"\\\"\" + e + \"\\\"\")) + \"]\"";
                        if (elementJsonType == "boolean")
                            return $"\"[\" + string.Join(\",\", global::System.Linq.Enumerable.Select({resultExpr}, e => e ? \"true\" : \"false\")) + \"]\"";
                        return $"\"[\" + string.Join(\",\", {resultExpr}) + \"]\"";
                    }
                }
                var contextPropertyName = GetJsonContextPropertyName(returnType);
                return $"global::System.Text.Json.JsonSerializer.Serialize({resultExpr}, PluginJsonContext.Default.{contextPropertyName})";
        }
    }

    public static (bool IsAsync, ITypeSymbol? InnerType) UnwrapAsync(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol namedType)
        {
            var fullName = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            if (fullName == "global::System.Threading.Tasks.Task" && !namedType.IsGenericType)
                return (true, null);
            if (fullName.StartsWith("global::System.Threading.Tasks.Task<") && namedType.IsGenericType)
                return (true, namedType.TypeArguments[0]);
            if (fullName == "global::System.Threading.Tasks.ValueTask" && !namedType.IsGenericType)
                return (true, null);
            if (fullName.StartsWith("global::System.Threading.Tasks.ValueTask<") && namedType.IsGenericType)
                return (true, namedType.TypeArguments[0]);
        }
        return (false, null);
    }

    public static bool IsVoid(ITypeSymbol typeSymbol)
        => typeSymbol.SpecialType == SpecialType.System_Void;

    private static string GetUnderlyingTypeName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            return namedType.TypeArguments[0].ToDisplayString();
        }

        if (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
            && typeSymbol.OriginalDefinition is INamedTypeSymbol original)
        {
            return original.ToDisplayString();
        }

        return typeSymbol.ToDisplayString();
    }

    private static string EscapeStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\r': sb.Append("\\r"); break;
                case '\n': sb.Append("\\n"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20) sb.Append($"\\u{(int)ch:X4}");
                    else sb.Append(ch);
                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static string GetJsonContextPropertyName(ITypeSymbol typeSymbol)
    {
        if (typeSymbol is IArrayTypeSymbol arrayType)
            return GetJsonContextPropertyName(arrayType.ElementType) + "Array";

        if (typeSymbol is INamedTypeSymbol named && named.IsGenericType)
        {
            var sb = new StringBuilder();
            sb.Append(named.Name);
            foreach (var arg in named.TypeArguments)
                sb.Append(GetJsonContextPropertyName(arg));
            return sb.ToString();
        }

        return typeSymbol.MetadataName;
    }
}
