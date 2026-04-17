using Cortana.Plugins.GoogleSearch.Models;
using Cortana.Plugins.GoogleSearch.Services;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.GoogleSearch.Tools;

/// <summary>
/// 负责配置查询与修改工具。
/// 职责单一：参数校验、调用 Service、返回统一结果。
/// </summary>
[Tool]
public sealed class GoogleSearchConfigTools
{
    private readonly GoogleSearchService _service;

    public GoogleSearchConfigTools(GoogleSearchService service)
    {
        _service = service;
    }

    /// <summary>
    /// 查询当前插件配置状态。
    /// </summary>
    [Tool(Name = "google_search_get_config", Description = "查询谷歌搜索插件的当前配置状态，包括 API Key 脱敏显示、Search Engine ID 和默认搜索参数。")]
    public string GetConfig()
    {
        var result = _service.GetConfig();

        if (!result.Configured)
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(
                    ErrorCodes.ConfigNotInitialized,
                    "当前谷歌搜索插件尚未初始化配置，请先调用 google_search_set_config 提供 API Key 和 Search Engine ID。",
                    result),
                PluginJsonContext.Default.ToolResult);
        }

        return System.Text.Json.JsonSerializer.Serialize(
            ToolResult.Ok("已返回当前配置摘要。", result),
            PluginJsonContext.Default.ToolResult);
    }

    /// <summary>
    /// 初始化或更新插件配置。
    /// </summary>
    [Tool(Name = "google_search_set_config", Description = "初始化或更新谷歌搜索插件配置。必填参数为 api_key 和 search_engine_id，可选参数为默认界面语言(default_hl)、默认国家地区(default_gl)和默认安全搜索级别(default_safe)。")]
    public string SetConfig(
        [Parameter(Description = "Google API Key")] string apiKey,
        [Parameter(Description = "Programmable Search Engine 的 cx")] string searchEngineId,
        [Parameter(Description = "默认界面语言，例如 zh-CN、en")] string? defaultHl,
        [Parameter(Description = "默认国家地区，例如 CN、US")] string? defaultGl,
        [Parameter(Description = "默认安全搜索级别，例如 active、off")] string? defaultSafe)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "api_key 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        if (string.IsNullOrWhiteSpace(searchEngineId))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "search_engine_id 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        var result = _service.SetConfig(apiKey, searchEngineId, defaultHl, defaultGl, defaultSafe);

        return System.Text.Json.JsonSerializer.Serialize(
            ToolResult.Ok("配置已保存。", result),
            PluginJsonContext.Default.ToolResult);
    }
}