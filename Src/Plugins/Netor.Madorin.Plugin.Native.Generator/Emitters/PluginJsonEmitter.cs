using System.Collections.Generic;
using System.Linq;
using System.Text;

using Netor.Madorin.Plugin.Native.Generator.Analysis;

namespace Netor.Madorin.Plugin.Native.Generator.Emitters;

/// <summary>
/// 生成 plugin.json 清单文件的内容。
/// 通过 AdditionalFileOutput 输出到构建目录。
/// </summary>
internal static class PluginJsonEmitter
{
    /// <summary>
    /// 生成 plugin.json 内容。
    /// </summary>
    /// <param name="plugin">插件元数据。</param>
    /// <param name="assemblyName">项目的 AssemblyName（推断 libraryName）。</param>
    public static string Emit(PluginClassInfo plugin, string? assemblyName)
    {
        var libraryName = (assemblyName ?? "Plugin") + ".dll";

        var sb = new StringBuilder();
        sb.AppendLine("//{");
        sb.AppendLine($"//    \"id\": \"{EscapeJson(plugin.Id)}\",");
        sb.AppendLine($"//    \"name\": \"{EscapeJson(plugin.Name)}\",");
        sb.AppendLine($"//    \"version\": \"{EscapeJson(plugin.Version)}\",");
        sb.AppendLine($"//    \"description\": \"{EscapeJson(plugin.Description)}\",");
        if (plugin.Tags.Length > 0)
        {
            var tags = string.Join(", ", plugin.Tags.Select(tag => $"\"{EscapeJson(tag)}\""));
            sb.AppendLine($"//    \"tags\": [{tags}],");
        }

        if (plugin.Capabilities.Length > 0)
        {
            var capabilities = string.Join(", ", plugin.Capabilities.Select(capability => $"\"{EscapeJson(capability)}\""));
            sb.AppendLine($"//    \"capabilities\": [{capabilities}],");
            sb.AppendLine($"//    \"providedCapabilities\": [{capabilities}],");
        }

        if (plugin.RequiredHostCapabilities.Length > 0)
        {
            var requiredHostCapabilities = string.Join(", ", plugin.RequiredHostCapabilities.Select(EmitRequiredHostCapabilityJson));
            sb.AppendLine($"//    \"requiredHostCapabilities\": [{requiredHostCapabilities}],");
        }

        if (plugin.SettingsSchema.Length > 0)
        {
            var settingsSchema = string.Join(", ", plugin.SettingsSchema.Select(EmitPluginSettingJson));
            sb.AppendLine($"//    \"settingsSchema\": [{settingsSchema}],");
        }

        if (plugin.PublishedOps.Length > 0)
        {
            var publishedOps = string.Join(", ", plugin.PublishedOps.Select(op => EmitEventOpJson(op, "description")));
            sb.AppendLine($"//    \"publishedOps\": [{publishedOps}],");
        }

        if (plugin.SubscribedOps.Length > 0)
        {
            var subscribedOps = string.Join(", ", plugin.SubscribedOps.Select(op => EmitEventOpJson(op, "reason")));
            sb.AppendLine($"//    \"subscribedOps\": [{subscribedOps}],");
        }

        sb.AppendLine("//    \"runtime\": \"native\",");
        sb.AppendLine($"//    \"libraryName\": \"{EscapeJson(libraryName)}\",");
        sb.AppendLine("//    \"minHostVersion\": \"1.0.0\"");
        sb.Append("//}");

        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r");
    }

    private static string EmitEventOpJson(EventOpDeclarationInfo op, string textField)
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"op\":\"{EscapeJson(op.Op)}\"");
        if (!string.IsNullOrWhiteSpace(op.Description))
        {
            sb.Append($",\"{textField}\":\"{EscapeJson(op.Description!)}\"");
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static string EmitRequiredHostCapabilityJson(RequiredHostCapabilityInfo capability)
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"id\":\"{EscapeJson(capability.Id)}\"");
        if (!string.IsNullOrWhiteSpace(capability.Purpose))
        {
            sb.Append($",\"purpose\":\"{EscapeJson(capability.Purpose!)}\"");
        }

        sb.Append($",\"required\":{(capability.Required ? "true" : "false")}");
        if (!string.IsNullOrWhiteSpace(capability.Reason))
        {
            sb.Append($",\"reason\":\"{EscapeJson(capability.Reason!)}\"");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string EmitPluginSettingJson(PluginSettingInfo setting)
    {
        var sb = new StringBuilder();
        sb.Append($"{{\"key\":\"{EscapeJson(setting.Key)}\",\"type\":\"{EscapeJson(setting.Type)}\"");

        if (!string.IsNullOrWhiteSpace(setting.Label))
        {
            sb.Append($",\"label\":\"{EscapeJson(setting.Label!)}\"");
        }

        if (!string.IsNullOrWhiteSpace(setting.Description))
        {
            sb.Append($",\"description\":\"{EscapeJson(setting.Description!)}\"");
        }

        if (setting.DefaultValue is not null)
        {
            sb.Append($",\"defaultValue\":{EmitDefaultValue(setting.Type, setting.DefaultValue)}");
        }

        if (setting.Required)
        {
            sb.Append(",\"required\":true");
        }

        if (setting.Sensitive)
        {
            sb.Append(",\"sensitive\":true");
        }

        if (setting.Scope.Length > 0)
        {
            sb.Append($",\"scope\":[{string.Join(", ", setting.Scope.Select(scope => $"\"{EscapeJson(scope)}\""))}]");
        }

        if (setting.Options.Length > 0)
        {
            sb.Append($",\"options\":[{string.Join(", ", setting.Options.Select(option => $"\"{EscapeJson(option)}\""))}]");
        }

        var validation = EmitValidationJson(setting);
        if (validation is not null)
        {
            sb.Append($",\"validation\":{validation}");
        }

        if (setting.RestartRequired)
        {
            sb.Append(",\"restartRequired\":true");
        }

        sb.Append('}');
        return sb.ToString();
    }

    private static string EmitDefaultValue(string type, string value)
    {
        switch (type)
        {
            case "boolean":
                return bool.TryParse(value, out var boolValue) && boolValue ? "true" : "false";
            case "number":
                return double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var numberValue)
                    ? numberValue.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : "0";
            case "json":
                return string.IsNullOrWhiteSpace(value) ? "null" : value;
            default:
                return $"\"{EscapeJson(value)}\"";
        }
    }

    private static string? EmitValidationJson(PluginSettingInfo setting)
    {
        var parts = new List<string>();
        if (setting.ValidationMin is { } min)
        {
            parts.Add($"\"min\":{min.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMax is { } max)
        {
            parts.Add($"\"max\":{max.ToString("R", System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMinLength is { } minLength)
        {
            parts.Add($"\"minLength\":{minLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMaxLength is { } maxLength)
        {
            parts.Add($"\"maxLength\":{maxLength.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(setting.ValidationPattern))
        {
            parts.Add($"\"pattern\":\"{EscapeJson(setting.ValidationPattern!)}\"");
        }

        return parts.Count == 0 ? null : $"{{{string.Join(",", parts)}}}";
    }
}
