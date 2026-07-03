namespace Netor.Cortana.Plugin.Native.Debugger.Invocation;

/// <summary>
/// 参数类型转换
/// </summary>
public static class TypeConverter
{
    public static object Convert(string arg, Type targetType)
    {
        if (targetType == typeof(string)) return arg;
        if (targetType == typeof(int)) return int.Parse(arg);
        if (targetType == typeof(long)) return long.Parse(arg);
        if (targetType == typeof(double)) return double.Parse(arg);
        if (targetType == typeof(float)) return float.Parse(arg);
        if (targetType == typeof(bool)) return bool.Parse(arg);
        if (targetType == typeof(DateTime)) return DateTime.Parse(arg);
        if (targetType == typeof(DateTimeOffset)) return DateTimeOffset.Parse(arg);
        if (targetType == typeof(Guid)) return Guid.Parse(arg);
        return System.Convert.ChangeType(arg, targetType);
    }

    public static string GetFriendlyTypeName(Type type)
    {
        if (type == typeof(string)) return "String";
        if (type == typeof(int)) return "Int32";
        if (type == typeof(long)) return "Int64";
        if (type == typeof(double)) return "Double";
        if (type == typeof(float)) return "Single";
        if (type == typeof(bool)) return "Boolean";
        if (type == typeof(DateTime)) return "DateTime";
        if (type == typeof(DateTimeOffset)) return "DateTimeOffset";
        if (type == typeof(Guid)) return "Guid";
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            return GetFriendlyTypeName(Nullable.GetUnderlyingType(type)!) + "?";
        return type.Name;
    }
}
