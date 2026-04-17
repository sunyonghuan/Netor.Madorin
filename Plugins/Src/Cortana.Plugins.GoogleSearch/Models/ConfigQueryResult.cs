using System.Text.Json.Serialization;

namespace Cortana.Plugins.GoogleSearch.Models;

/// <summary>
/// google_search_get_config 工具的返回数据。
/// </summary>
public sealed class ConfigQueryResult
{
    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("search_engine_id")]
    public string? SearchEngineId { get; set; }

    /// <summary>
    /// api_key 脱敏后显示，仅显示前四位和后四位，中间用 *** 填充。
    /// </summary>
    [JsonPropertyName("api_key_masked")]
    public string? ApiKeyMasked { get; set; }

    [JsonPropertyName("default_hl")]
    public string? DefaultHl { get; set; }

    [JsonPropertyName("default_gl")]
    public string? DefaultGl { get; set; }

    [JsonPropertyName("default_safe")]
    public string? DefaultSafe { get; set; }

    /// <summary>
    /// 将 api_key 脱敏后返回。
    /// </summary>
    public static string MaskApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Length <= 8)
            return "***";

        return $"{apiKey[..4]}***{apiKey[^4..]}";
    }
}