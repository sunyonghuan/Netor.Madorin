using Cortana.Plugins.GoogleSearch.Models;
using Cortana.Plugins.GoogleSearch.Services;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.GoogleSearch.Tools;

/// <summary>
/// 负责搜索相关工具。
/// 职责单一：参数校验、调用 Service、返回统一结果。
/// </summary>
[Tool]
public sealed class GoogleSearchTools
{
    private readonly GoogleSearchService _service;

    public GoogleSearchTools(GoogleSearchService service)
    {
        _service = service;
    }

    /// <summary>
    /// 执行标准网页搜索。
    /// </summary>
    [Tool(Name = "google_search_web", Description = "执行标准网页搜索。必填参数为 query，可选参数为 api_key、search_engine_id（不传入时使用已保存配置）、num、start、hl、gl、safe、date_restrict。")]
    public async Task<string> SearchWeb(
        [Parameter(Description = "搜索查询词")] string query,
        [Parameter(Description = "本次请求覆盖使用的 Google API Key，可选")] string? apiKey,
        [Parameter(Description = "本次请求覆盖使用的 Search Engine ID，可选")] string? searchEngineId,
        [Parameter(Description = "每页结果数，默认 10")] int? num,
        [Parameter(Description = "起始索引，默认 1")] int? start,
        [Parameter(Description = "界面语言，例如 zh-CN、en")] string? hl,
        [Parameter(Description = "国家地区，例如 CN、US")] string? gl,
        [Parameter(Description = "安全搜索级别，例如 active、off")] string? safe,
        [Parameter(Description = "日期限制，例如 d（一天前）、w（一周前）、m（一个月前）")] string? dateRestrict)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "query 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        var response = await _service.SearchWebAsync(
            apiKey, searchEngineId, query, num, start, hl, gl, safe);

        return WrapSearchResponse(response, query);
    }

    /// <summary>
    /// 执行指定站点内搜索。
    /// </summary>
    [Tool(Name = "google_search_site", Description = "执行指定站点内的网页搜索。必填参数为 query 和 site，可选参数为 api_key、search_engine_id（不传入时使用已保存配置）、num、start、hl、gl、safe。")]
    public async Task<string> SearchSite(
        [Parameter(Description = "搜索查询词")] string query,
        [Parameter(Description = "限定搜索的站点域名，例如 example.com")] string site,
        [Parameter(Description = "本次请求覆盖使用的 Google API Key，可选")] string? apiKey,
        [Parameter(Description = "本次请求覆盖使用的 Search Engine ID，可选")] string? searchEngineId,
        [Parameter(Description = "每页结果数，默认 10")] int? num,
        [Parameter(Description = "起始索引，默认 1")] int? start,
        [Parameter(Description = "界面语言，例如 zh-CN、en")] string? hl,
        [Parameter(Description = "国家地区，例如 CN、US")] string? gl,
        [Parameter(Description = "安全搜索级别，例如 active、off")] string? safe)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "query 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        if (string.IsNullOrWhiteSpace(site))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "site 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        var response = await _service.SearchSiteAsync(
            apiKey, searchEngineId, query, site, num, start, hl, gl, safe);

        return WrapSearchResponse(response, query);
    }

    /// <summary>
    /// 执行图片搜索。
    /// </summary>
    [Tool(Name = "google_search_images", Description = "执行图片搜索。必填参数为 query，可选参数为 api_key、search_engine_id（不传入时使用已保存配置）、num、start、hl、gl、safe、site。")]
    public async Task<string> SearchImages(
        [Parameter(Description = "图片搜索查询词")] string query,
        [Parameter(Description = "本次请求覆盖使用的 Google API Key，可选")] string? apiKey,
        [Parameter(Description = "本次请求覆盖使用的 Search Engine ID，可选")] string? searchEngineId,
        [Parameter(Description = "每页结果数，默认 10")] int? num,
        [Parameter(Description = "起始索引，默认 1")] int? start,
        [Parameter(Description = "界面语言，例如 zh-CN、en")] string? hl,
        [Parameter(Description = "国家地区，例如 CN、US")] string? gl,
        [Parameter(Description = "安全搜索级别，例如 active、off")] string? safe,
        [Parameter(Description = "限定图片来源于指定站点，可选")] string? site)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.InvalidArgument, "query 是必填参数。", null),
                PluginJsonContext.Default.ToolResult);
        }

        var response = await _service.SearchImagesAsync(
            apiKey, searchEngineId, query, num, start, hl, gl, safe, site);

        return WrapSearchResponse(response, query);
    }

    /// <summary>
    /// 统一包装搜索响应结果。
    /// </summary>
    private string WrapSearchResponse(SearchResponse response, string query)
    {
        if (response.Kind == "not_initialized")
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(
                    ErrorCodes.ConfigNotInitialized,
                    "当前谷歌搜索插件尚未初始化配置，请先调用 google_search_set_config 提供 API Key 和 Search Engine ID。",
                    null),
                PluginJsonContext.Default.ToolResult);
        }

        if (response.Kind == "network_error")
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.NetworkError, "无法连接到 Google 搜索服务，请检查网络。", null),
                PluginJsonContext.Default.ToolResult);
        }

        if (response.Kind == "cancelled")
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.Timeout, "搜索请求已取消。", null),
                PluginJsonContext.Default.ToolResult);
        }

        if (response.Kind == "error")
        {
            return System.Text.Json.JsonSerializer.Serialize(
                ToolResult.Fail(ErrorCodes.UpstreamError, "Google API 返回了错误响应。", null),
                PluginJsonContext.Default.ToolResult);
        }

        var data = new SearchResultData
        {
            Query = query,
            Items = response.Items?.Select(item => new SearchResultItem
            {
                Title = item.Title,
                Link = item.Link,
                DisplayLink = item.DisplayLink,
                Snippet = item.Snippet,
                ThumbnailLink = item.Image?.ThumbnailLink,
                Width = item.Image?.Width ?? 0,
                Height = item.Image?.Height ?? 0,
                ContextLink = item.Image?.ContextLink ?? string.Empty
            }).ToList(),
            TotalResults = response.SearchInformation?.TotalResults ?? "0",
            SearchTimeSeconds = response.SearchInformation?.SearchTime ?? 0,
            NextStart = response.Queries?.NextPage?.FirstOrDefault()?.StartIndex
        };

        return System.Text.Json.JsonSerializer.Serialize(
            ToolResult.Ok($"搜索完成，共返回 {data.Items?.Count ?? 0} 条结果。", data),
            PluginJsonContext.Default.ToolResult);
    }
}

/// <summary>
/// 搜索结果的统一返回数据结构。
/// </summary>
public sealed class SearchResultData
{
    public string Query { get; set; } = string.Empty;
    public List<SearchResultItem>? Items { get; set; }
    public string TotalResults { get; set; } = "0";
    public double SearchTimeSeconds { get; set; }
    public int? NextStart { get; set; }
}

/// <summary>
/// 单条搜索结果。
/// </summary>
public sealed class SearchResultItem
{
    public string Title { get; set; } = string.Empty;
    public string Link { get; set; } = string.Empty;
    public string DisplayLink { get; set; } = string.Empty;
    public string Snippet { get; set; } = string.Empty;
    public string? ThumbnailLink { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public string ContextLink { get; set; } = string.Empty;
}