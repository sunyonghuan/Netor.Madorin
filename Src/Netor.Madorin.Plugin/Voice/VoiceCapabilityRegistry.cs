using Netor.Cortana.Entitys.Services;

namespace Netor.Madorin.Plugin.Voice;

/// <summary>
/// 语音插件能力发现注册表。
/// </summary>
public sealed class VoiceCapabilityRegistry(
    PluginLoader pluginLoader,
    SystemSettingsService settingsService)
{
    public IReadOnlyList<VoicePluginDescriptor> GetAvailablePlugins(VoicePluginCapability capability)
    {
        return GetAvailablePlugins()
            .Where(plugin => plugin.Capability == capability)
            .OrderBy(plugin => plugin.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(plugin => plugin.PluginId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<VoicePluginDescriptor> GetAvailablePlugins()
    {
        var plugins = new List<VoicePluginDescriptor>();

        foreach (var info in pluginLoader.GetLoadedPluginInfos())
        {
            foreach (var capability in GetCapabilities(info))
            {
                plugins.Add(new VoicePluginDescriptor(
                    capability,
                    VoicePluginCapabilityIds.GetId(capability),
                    info.Plugin.Id,
                    info.Plugin.Name,
                    info.Plugin.Version,
                    info.Plugin.Description,
                    info.DirectoryPath,
                    info.DirectoryName));
            }
        }

        return plugins
            .DistinctBy(plugin => (plugin.Capability, plugin.PluginId), VoicePluginDescriptorKeyComparer.Instance)
            .ToList();
    }

    public VoicePluginDescriptor? GetSelectedPlugin(VoicePluginCapability capability)
    {
        var settingKey = GetPluginIdSettingKey(capability);
        var selectedPluginId = settingsService.GetValue(settingKey, string.Empty);
        var availablePlugins = GetAvailablePlugins(capability);

        if (!string.IsNullOrWhiteSpace(selectedPluginId))
        {
            var selected = availablePlugins.FirstOrDefault(
                plugin => string.Equals(plugin.PluginId, selectedPluginId, StringComparison.OrdinalIgnoreCase));

            if (selected is not null)
            {
                return selected;
            }
        }

        return availablePlugins.FirstOrDefault();
    }

    public bool IsEnabled(VoicePluginCapability capability)
    {
        return settingsService.GetValue(GetEnabledSettingKey(capability), false);
    }

    public static string GetEnabledSettingKey(VoicePluginCapability capability)
    {
        return capability switch
        {
            VoicePluginCapability.Kws => "Voice.Kws.Enabled",
            VoicePluginCapability.Stt => "Voice.Stt.Enabled",
            VoicePluginCapability.Tts => "Voice.Tts.Enabled",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    public static string GetPluginIdSettingKey(VoicePluginCapability capability)
    {
        return capability switch
        {
            VoicePluginCapability.Kws => "Voice.Kws.PluginId",
            VoicePluginCapability.Stt => "Voice.Stt.PluginId",
            VoicePluginCapability.Tts => "Voice.Tts.PluginId",
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    private static IEnumerable<VoicePluginCapability> GetCapabilities(LoadedPluginInfo info)
    {
        foreach (var capability in GetDeclaredCapabilities(GetProvidedCapabilities(info.Manifest)))
        {
            yield return capability;
        }

        foreach (var capability in GetTaggedCapabilities(info.Manifest.Tags ?? []))
        {
            yield return capability;
        }

        foreach (var capability in GetTaggedCapabilities(info.Plugin.Tags))
        {
            yield return capability;
        }
    }

    private static IReadOnlyList<string> GetProvidedCapabilities(PluginManifest manifest)
    {
        return manifest.ProvidedCapabilities is { Count: > 0 }
            ? manifest.ProvidedCapabilities
            : manifest.Capabilities ?? [];
    }

    private static IEnumerable<VoicePluginCapability> GetDeclaredCapabilities(IEnumerable<string> values)
    {
        foreach (var value in values)
        {
            if (VoicePluginCapabilityIds.TryParse(value, out var capability))
            {
                yield return capability;
            }
        }
    }

    private static IEnumerable<VoicePluginCapability> GetTaggedCapabilities(IEnumerable<string> tags)
    {
        var normalizedTags = tags
            .Where(static tag => !string.IsNullOrWhiteSpace(tag))
            .Select(static tag => tag.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!normalizedTags.Contains("voice") && !normalizedTags.Contains("语音"))
        {
            yield break;
        }

        foreach (var tag in normalizedTags)
        {
            if (VoicePluginCapabilityIds.TryParse(tag, out var capability))
            {
                yield return capability;
            }
        }
    }

    private sealed class VoicePluginDescriptorKeyComparer : IEqualityComparer<(VoicePluginCapability Capability, string PluginId)>
    {
        public static VoicePluginDescriptorKeyComparer Instance { get; } = new();

        public bool Equals(
            (VoicePluginCapability Capability, string PluginId) x,
            (VoicePluginCapability Capability, string PluginId) y)
        {
            return x.Capability == y.Capability
                && string.Equals(x.PluginId, y.PluginId, StringComparison.OrdinalIgnoreCase);
        }

        public int GetHashCode((VoicePluginCapability Capability, string PluginId) obj)
        {
            return HashCode.Combine(obj.Capability, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.PluginId));
        }
    }
}
