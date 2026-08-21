using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

using Netor.Madorin.Plugin.Native.Generator.Analysis;

namespace Netor.Madorin.Plugin.Native.Generator.Emitters;

/// <summary>
/// 生成 cortana_plugin_get_info 导出函数中的 JSON 常量字符串。
/// </summary>
internal static class InfoJsonEmitter
{
    /// <summary>
    /// 生成 get_info 返回的 JSON 常量。
    /// </summary>
    public static string EmitInfoJson(PluginClassInfo plugin, List<ToolClassInfo> toolClasses)
    {
        var sb = new StringBuilder();
        sb.AppendLine("{");
        sb.AppendLine($"    \"id\": \"{EscapeJson(plugin.Id)}\",");
        sb.AppendLine($"    \"name\": \"{EscapeJson(plugin.Name)}\",");
        sb.AppendLine($"    \"version\": \"{EscapeJson(plugin.Version)}\",");
        sb.AppendLine($"    \"description\": \"{EscapeJson(plugin.Description)}\",");

        if (plugin.Instructions != null)
        {
            sb.AppendLine($"    \"instructions\": \"{EscapeJson(plugin.Instructions)}\",");
        }

        // Tags
        sb.Append("    \"tags\": [");
        if (plugin.Tags.Length > 0)
        {
            sb.Append(string.Join(", ", plugin.Tags.Select(t => $"\"{EscapeJson(t)}\"")));
        }
        sb.AppendLine("],");

        // Capabilities
        var capabilities = string.Join(", ", plugin.Capabilities.Select(t => $"\"{EscapeJson(t)}\""));
        sb.AppendLine($"    \"capabilities\": [{capabilities}],");
        sb.AppendLine($"    \"providedCapabilities\": [{capabilities}],");

        // Required host capabilities
        var requiredHostCapabilities = string.Join(", ", plugin.RequiredHostCapabilities.Select(EmitRequiredHostCapabilityJson));
        sb.AppendLine($"    \"requiredHostCapabilities\": [{requiredHostCapabilities}],");

        // Settings schema
        var settingsSchema = string.Join(", ", plugin.SettingsSchema.Select(EmitPluginSettingJson));
        sb.AppendLine($"    \"settingsSchema\": [{settingsSchema}],");

        // Tools
        sb.AppendLine("    \"tools\": [");

        var allMethods = toolClasses.SelectMany(tc => tc.Methods).ToList();

        for (int i = 0; i < allMethods.Count; i++)
        {
            var method = allMethods[i];
            sb.AppendLine("        {");
            sb.AppendLine($"            \"name\": \"{EscapeJson(method.FullToolName)}\",");
            sb.AppendLine($"            \"shortName\": \"{EscapeJson(method.MethodSnakeName)}\",");
            sb.AppendLine($"            \"description\": \"{EscapeJson(method.Description)}\",");
            sb.Append("            \"parameters\": [");

            if (method.Parameters.Count > 0)
            {
                sb.AppendLine();
                for (int j = 0; j < method.Parameters.Count; j++)
                {
                    var param = method.Parameters[j];
                    sb.Append($"                {{ \"name\": \"{EscapeJson(param.JsonName)}\", \"type\": \"{param.JsonType}\", \"description\": \"{EscapeJson(param.Description)}\", \"required\": {(param.Required ? "true" : "false")} }}");
                    if (j < method.Parameters.Count - 1)
                        sb.AppendLine(",");
                    else
                        sb.AppendLine();
                }
                sb.Append("            ");
            }

            sb.AppendLine("]");

            sb.Append("        }");
            if (i < allMethods.Count - 1)
                sb.AppendLine(",");
            else
                sb.AppendLine();
        }

        sb.AppendLine("    ]");
        sb.Append("}");

        return sb.ToString();
    }

    private static string EscapeJson(string s)
    {
        return s
            .Replace("\\", "\\\\")
            .Replace("\"", "\\\"")
            .Replace("\n", "\\n")
            .Replace("\r", "\\r")
            .Replace("\t", "\\t");
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
                return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var numberValue)
                    ? numberValue.ToString("R", CultureInfo.InvariantCulture)
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
            parts.Add($"\"min\":{min.ToString("R", CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMax is { } max)
        {
            parts.Add($"\"max\":{max.ToString("R", CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMinLength is { } minLength)
        {
            parts.Add($"\"minLength\":{minLength.ToString(CultureInfo.InvariantCulture)}");
        }

        if (setting.ValidationMaxLength is { } maxLength)
        {
            parts.Add($"\"maxLength\":{maxLength.ToString(CultureInfo.InvariantCulture)}");
        }

        if (!string.IsNullOrWhiteSpace(setting.ValidationPattern))
        {
            parts.Add($"\"pattern\":\"{EscapeJson(setting.ValidationPattern!)}\"");
        }

        return parts.Count == 0 ? null : $"{{{string.Join(",", parts)}}}";
    }
}
