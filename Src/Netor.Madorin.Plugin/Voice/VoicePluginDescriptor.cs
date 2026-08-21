namespace Netor.Madorin.Plugin.Voice;

/// <summary>
/// 已发现的语音插件描述。
/// </summary>
public sealed record VoicePluginDescriptor(
    VoicePluginCapability Capability,
    string CapabilityId,
    string PluginId,
    string Name,
    Version Version,
    string Description,
    string DirectoryPath,
    string DirectoryName);
