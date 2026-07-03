using System.Text;

using Microsoft.CodeAnalysis;

namespace Netor.Cortana.Plugin.Native.Generator.Analysis;

/// <summary>
/// 类型映射工具：PascalCase → snake_case，C# 类型 → JSON Schema 类型。
/// </summary>
internal static class TypeMapper
{
    /// <summary>
    /// PascalCase 方法名转换为 snake_case 工具名。
    /// <para>示例：<c>EchoMessage</c> → <c>echo_message</c>，<c>GetSystemInfo</c> → <c>get_system_info</c></para>
    /// </summary>
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
                    // 上一个字符不是下划线，且（上一个字符是小写 或 下一个字符是小写）
                    var prev = pascalCase[i - 1];
                    if (prev != '_')
                    {
                        bool prevIsLower = char.IsLower(prev);
                        bool nextIsLower = i + 1 < pascalCase.Length && char.IsLower(pascalCase[i + 1]);

                        if (prevIsLower || nextIsLower)
                        {
                            sb.Append('_');
                        }
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

    /// <summary>
    /// 判断字符串是否已经是 snake_case 格式。
    /// </summary>
    public static bool IsSnakeCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;

        foreach (var c in name)
        {
            if (char.IsUpper(c))
                return false;
        }

        return name.Contains("_");
    }

    /// <summary>
    /// C# 类型符号映射为 JSON Schema 类型字符串。
    /// 不支持的类型返回 null。
    /// </summary>
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

    /// <summary>
    /// 判断 C# 类型是否为支持的工具参数类型。
    /// </summary>
    public static bool IsSupportedParameterType(ITypeSymbol typeSymbol)
    {
        return MapToJsonType(typeSymbol) != null;
    }

    /// <summary>
    /// 获取从 JSON 解析参数值的 JsonElement 方法调用表达式。
    /// </summary>
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

    /// <summary>
    /// 获取将返回值转换为字符串的表达式。
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
                // 基础类型数组（int[], string[], double[] 等）→ 手动拼接 JSON 数组，AOT 安全
                if (returnType is IArrayTypeSymbol arrayType)
                {
                    var elementJsonType = MapToJsonType(arrayType.ElementType);
                    if (elementJsonType != null)
                    {
                        if (elementJsonType == "string")
                            return $"\"[\" + string.Join(\",\", global::System.Linq.Enumerable.Select({resultExpr}, e => \"\\\"\" + e + \"\\\"\")) + \"]\"";
                        else if (elementJsonType == "boolean")
                            return $"\"[\" + string.Join(\",\", global::System.Linq.Enumerable.Select({resultExpr}, e => e ? \"true\" : \"false\")) + \"]\"";
                        else
                            return $"\"[\" + string.Join(\",\", {resultExpr}) + \"]\"";
                    }
                }

                // 自定义类型（自定义对象、自定义对象数组等）→ 使用用户手写的 PluginJsonContext AOT 安全序列化
                var contextPropertyName = GetJsonContextPropertyName(returnType);
                return $"global::System.Text.Json.JsonSerializer.Serialize({resultExpr}, PluginJsonContext.Default.{contextPropertyName})";
        }
    }

    /// <summary>
    /// 判断返回类型是否为 Task/Task&lt;T&gt;/ValueTask&lt;T&gt;。
    /// 返回 (isAsync, innerType)。
    /// </summary>
    public static (bool IsAsync, ITypeSymbol? InnerType) UnwrapAsync(ITypeSymbol returnType)
    {
        if (returnType is INamedTypeSymbol namedType)
        {
            var fullName = namedType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

            // Task (void)
            if (fullName == "global::System.Threading.Tasks.Task" && !namedType.IsGenericType)
                return (true, null);

            // Task<T>
            if (fullName.StartsWith("global::System.Threading.Tasks.Task<") && namedType.IsGenericType)
                return (true, namedType.TypeArguments[0]);

            // ValueTask (void)
            if (fullName == "global::System.Threading.Tasks.ValueTask" && !namedType.IsGenericType)
                return (true, null);

            // ValueTask<T>
            if (fullName.StartsWith("global::System.Threading.Tasks.ValueTask<") && namedType.IsGenericType)
                return (true, namedType.TypeArguments[0]);
        }

        return (false, null);
    }

    /// <summary>
    /// 判断返回类型是否为 void。
    /// </summary>
    public static bool IsVoid(ITypeSymbol typeSymbol)
    {
        return typeSymbol.SpecialType == SpecialType.System_Void;
    }

    /// <summary>
    /// 获取底层类型名（处理可空类型）。
    /// </summary>
    private static string GetUnderlyingTypeName(ITypeSymbol typeSymbol)
    {
        // 处理 Nullable<T>
        if (typeSymbol is INamedTypeSymbol namedType
            && namedType.IsGenericType
            && namedType.ConstructedFrom.SpecialType == SpecialType.System_Nullable_T)
        {
            return namedType.TypeArguments[0].ToDisplayString();
        }

        // 处理可空引用类型
        if (typeSymbol.NullableAnnotation == NullableAnnotation.Annotated
            && typeSymbol.OriginalDefinition is INamedTypeSymbol original)
        {
            return original.ToDisplayString();
        }

        return typeSymbol.ToDisplayString();
    }

    /// <summary>
    /// 获取 STJ Source Generator 为类型生成的 JsonSerializerContext 属性名。
    /// <para>规则：数组 T[] → {ElementName}Array，泛型 List&lt;T&gt; → ListT，普通类型用 Name。</para>
    /// </summary>
    private static string GetJsonContextPropertyName(ITypeSymbol typeSymbol)
    {
        // 数组类型：int[] → Int32Array, string[] → StringArray
        if (typeSymbol is IArrayTypeSymbol arrayType)
        {
            var elementName = GetJsonContextPropertyName(arrayType.ElementType);
            return elementName + "Array";
        }

        // 泛型类型：List<int> → ListInt32, Dictionary<string, int> → DictionaryStringInt32
        if (typeSymbol is INamedTypeSymbol named && named.IsGenericType)
        {
            var sb = new StringBuilder();
            sb.Append(named.Name);
            foreach (var arg in named.TypeArguments)
            {
                sb.Append(GetJsonContextPropertyName(arg));
            }
            return sb.ToString();
        }

        // 基础类型：用 metadata name（int → Int32, string → String, bool → Boolean）
        return typeSymbol.MetadataName;
    }
}
