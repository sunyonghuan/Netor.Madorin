namespace Netor.Madorin.Plugin.Voice;

/// <summary>
/// 语音插件能力 token 常量。
/// </summary>
public static class VoicePluginCapabilityIds
{
    public const string Kws = "voice.kws";
    public const string Stt = "voice.stt";
    public const string Tts = "voice.tts";

    public static string GetId(VoicePluginCapability capability)
    {
        return capability switch
        {
            VoicePluginCapability.Kws => Kws,
            VoicePluginCapability.Stt => Stt,
            VoicePluginCapability.Tts => Tts,
            _ => throw new ArgumentOutOfRangeException(nameof(capability), capability, null)
        };
    }

    public static bool TryParse(string value, out VoicePluginCapability capability)
    {
        capability = default;

        if (string.Equals(value, Kws, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "kws", StringComparison.OrdinalIgnoreCase))
        {
            capability = VoicePluginCapability.Kws;
            return true;
        }

        if (string.Equals(value, Stt, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "stt", StringComparison.OrdinalIgnoreCase))
        {
            capability = VoicePluginCapability.Stt;
            return true;
        }

        if (string.Equals(value, Tts, StringComparison.OrdinalIgnoreCase)
            || string.Equals(value, "tts", StringComparison.OrdinalIgnoreCase))
        {
            capability = VoicePluginCapability.Tts;
            return true;
        }

        return false;
    }
}
