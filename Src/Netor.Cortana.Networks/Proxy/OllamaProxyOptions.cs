using Netor.Cortana.Entitys.Proxy;
using Netor.Cortana.Entitys.Services;

namespace Netor.Cortana.Networks.Proxy;

/// <summary>
/// Ollama 兼容代理配置读取器。
/// </summary>
public sealed class OllamaProxyOptionsReader(SystemSettingsService settingsService)
{
    public const string Prefix = "Proxy.Ollama.";

    /// <summary>
    /// 从系统设置读取代理配置快照。
    /// </summary>
    public AiProxyOptionsSnapshot Read()
    {
        var modeRaw = settingsService.GetValue(Prefix + "Mode", nameof(AiProxyMode.ModelOnly));
        var mode = Enum.TryParse<AiProxyMode>(modeRaw, ignoreCase: true, out var parsedMode)
            ? parsedMode
            : AiProxyMode.ModelOnly;

        var host = settingsService.GetValue(Prefix + "Host", "localhost").Trim();
        if (string.IsNullOrWhiteSpace(host)) host = "localhost";

        var port = settingsService.GetValue<int>(Prefix + "Port", 11434);
        if (port <= 0 || port > 65535) port = 11434;

        var maxConcurrent = settingsService.GetValue<int>(Prefix + "MaxConcurrentRequests", 2);
        if (maxConcurrent <= 0) maxConcurrent = 2;

        var version = settingsService.GetValue(Prefix + "Version", "0.21.2").Trim();
        if (string.IsNullOrWhiteSpace(version)) version = "0.21.2";

        return new AiProxyOptionsSnapshot(
            Enabled: settingsService.GetValue(Prefix + "Enabled", false),
            Host: host,
            Port: port,
            Mode: mode,
            ProviderId: EmptyToNull(settingsService.GetValue(Prefix + "ProviderId", string.Empty)),
            ModelId: EmptyToNull(settingsService.GetValue(Prefix + "ModelId", string.Empty)),
            AgentId: EmptyToNull(settingsService.GetValue(Prefix + "AgentId", string.Empty)),
            ExposeDefaultModel: settingsService.GetValue(Prefix + "ExposeDefaultModel", true),
            AllowLan: settingsService.GetValue(Prefix + "AllowLan", false),
            RequireApiKey: false,
            MaxConcurrentRequests: maxConcurrent,
            Version: version);
    }

    private static string? EmptyToNull(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
