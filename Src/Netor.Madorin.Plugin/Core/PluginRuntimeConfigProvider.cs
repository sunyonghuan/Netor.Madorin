using System.Globalization;
using System.Text;
using System.Text.Json;

using Netor.Cortana.Entitys.Services;

namespace Netor.Madorin.Plugin;

/// <summary>
/// 从系统设置中读取插件配置和授权设置，并生成 init.extensions 使用的 JSON 字符串。
/// </summary>
public sealed class PluginRuntimeConfigProvider(
    SystemSettingsService settingsService,
    IPluginConfigValueProtector valueProtector) : IPluginRuntimeConfigProvider
{
    private const string HostLlmCapability = "host.llm.invoke.v1";
    private const string LlmCapability = "llm";

    /// <inheritdoc />
    public string BuildPluginConfigJson(string pluginId, PluginManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var descriptor in manifest.SettingsSchema ?? [])
            {
                if (descriptor is null || string.IsNullOrWhiteSpace(descriptor.Key))
                {
                    continue;
                }

                var storedValue = settingsService.GetValue(GetPluginConfigKey(pluginId, descriptor.Key));
                if (storedValue is not null)
                {
                    WriteConfigValue(writer, descriptor.Key, descriptor.Type, valueProtector.GetEffectiveValue(storedValue));
                    continue;
                }

                if (descriptor.DefaultValue is { } defaultValue)
                {
                    writer.WritePropertyName(descriptor.Key);
                    defaultValue.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <inheritdoc />
    public string BuildHostCapabilityGrantsJson(string pluginId, PluginManifest manifest)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);
        ArgumentNullException.ThrowIfNull(manifest);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("version", "1.0");
            writer.WritePropertyName("grants");
            writer.WriteStartArray();

            foreach (var capability in manifest.RequiredHostCapabilities ?? [])
            {
                if (capability is null || string.IsNullOrWhiteSpace(capability.Id))
                {
                    continue;
                }

                WriteGrant(writer, pluginId, capability);
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string GetPluginConfigKey(string pluginId, string key) => $"Plugin:{pluginId}:Config:{key}";

    private static void WriteConfigValue(Utf8JsonWriter writer, string key, string? type, string value)
    {
        writer.WritePropertyName(key);

        switch (NormalizeSettingType(type))
        {
            case "boolean":
                if (bool.TryParse(value, out var boolValue))
                {
                    writer.WriteBooleanValue(boolValue);
                }
                else
                {
                    writer.WriteBooleanValue(false);
                }
                break;

            case "number":
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var numberValue))
                {
                    writer.WriteNumberValue(numberValue);
                }
                else
                {
                    writer.WriteNumberValue(0);
                }
                break;

            case "json":
                WriteJsonValue(writer, value);
                break;

            default:
                writer.WriteStringValue(value);
                break;
        }
    }

    private static string NormalizeSettingType(string? type)
    {
        return string.IsNullOrWhiteSpace(type)
            ? "string"
            : type.Trim().ToLowerInvariant();
    }

    private static void WriteJsonValue(Utf8JsonWriter writer, string value)
    {
        try
        {
            using var document = JsonDocument.Parse(value);
            document.RootElement.WriteTo(writer);
        }
        catch (JsonException)
        {
            writer.WriteStringValue(value);
        }
    }

    private void WriteGrant(Utf8JsonWriter writer, string pluginId, RequiredHostCapability capability)
    {
        writer.WriteStartObject();
        writer.WriteString("id", capability.Id);

        if (!string.IsNullOrWhiteSpace(capability.Purpose))
        {
            writer.WriteString("purpose", capability.Purpose);
        }

        if (string.Equals(capability.Id, HostLlmCapability, StringComparison.OrdinalIgnoreCase))
        {
            WriteLlmGrant(writer, pluginId);
        }
        else
        {
            writer.WriteBoolean("enabled", false);
        }

        writer.WriteEndObject();
    }

    private void WriteLlmGrant(Utf8JsonWriter writer, string pluginId)
    {
        var prefix = $"Plugin:{pluginId}:Capability:{LlmCapability}";
        writer.WriteBoolean("enabled", settingsService.GetValue($"{prefix}:Enabled", false));
        writer.WritePropertyName("limits");
        writer.WriteStartObject();
        writer.WriteNumber("maxInputTokens", settingsService.GetValue($"{prefix}:MaxInputTokens", 128000));
        writer.WriteNumber("maxOutputTokens", settingsService.GetValue($"{prefix}:MaxOutputTokens", 128000));
        writer.WriteNumber("timeoutMs", settingsService.GetValue($"{prefix}:TimeoutMs", 30000));
        writer.WriteNumber("maxConcurrency", Math.Max(1, settingsService.GetValue($"{prefix}:MaxConcurrency", 3)));
        writer.WriteBoolean("allowBackground", settingsService.GetValue($"{prefix}:AllowBackground", true));
        writer.WriteEndObject();
    }
}
