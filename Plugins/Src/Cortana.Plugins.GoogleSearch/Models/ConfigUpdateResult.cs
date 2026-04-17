using System.Text.Json.Serialization;

namespace Cortana.Plugins.GoogleSearch.Models;

/// <summary>
/// google_search_set_config 工具的返回数据。
/// </summary>
public sealed class ConfigUpdateResult
{
    [JsonPropertyName("changed")]
    public bool Changed { get; set; }

    [JsonPropertyName("configured")]
    public bool Configured { get; set; }

    [JsonPropertyName("config_file")]
    public string ConfigFile { get; set; } = string.Empty;
}