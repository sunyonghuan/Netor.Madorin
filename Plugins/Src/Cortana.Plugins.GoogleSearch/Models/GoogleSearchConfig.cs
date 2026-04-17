using System.Text.Json.Serialization;

namespace Cortana.Plugins.GoogleSearch.Models;

/// <summary>
/// 谷歌搜索插件配置。
/// 存储在插件数据目录的 config.json 文件中。
/// </summary>
public sealed class GoogleSearchConfig
{
    [JsonPropertyName("api_key")]
    public string ApiKey { get; set; } = string.Empty;

    [JsonPropertyName("search_engine_id")]
    public string SearchEngineId { get; set; } = string.Empty;

    [JsonPropertyName("default_hl")]
    public string? DefaultHl { get; set; }

    [JsonPropertyName("default_gl")]
    public string? DefaultGl { get; set; }

    [JsonPropertyName("default_safe")]
    public string? DefaultSafe { get; set; }

    /// <summary>
    /// 检查配置是否完整，即 api_key 和 search_engine_id 均已填写。
    /// </summary>
    public bool IsComplete() =>
        !string.IsNullOrWhiteSpace(ApiKey) &&
        !string.IsNullOrWhiteSpace(SearchEngineId);
}