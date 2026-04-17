using System.Text.Json;
using Cortana.Plugins.GoogleSearch.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.GoogleSearch.Services;

/// <summary>
/// 负责向 Google Custom Search JSON API 发送 HTTP 请求。
/// 职责单一：只关心请求构造、发送和上游错误解析。
/// </summary>
public sealed class GoogleSearchClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<GoogleSearchClient> _logger;

    public GoogleSearchClient(
        HttpClient httpClient,
        ILogger<GoogleSearchClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    /// <summary>
    /// 执行网页搜索或图片搜索。
    /// </summary>
    /// <param name="apiKey">Google API Key</param>
    /// <param name="searchEngineId">Search Engine ID (cx)</param>
    /// <param name="query">搜索词</param>
    /// <param name="searchType">图片搜索时传 "image"，默认 null 表示网页搜索</param>
    /// <param name="num">每页结果数，默认 10</param>
    /// <param name="start">起始索引，默认 1</param>
    /// <param name="hl">界面语言，可选</param>
    /// <param name="gl">国家地区，可选</param>
    /// <param name="safe">安全搜索级别，可选</param>
    /// <param name="site">站内搜索目标站点，可选</param>
    /// <returns>搜索响应或错误信息</returns>
    public async Task<SearchResponse> SearchAsync(
        string apiKey,
        string searchEngineId,
        string query,
        string? searchType,
        int? num,
        int? start,
        string? hl,
        string? gl,
        string? safe,
        string? site)
    {
        var queryParams = BuildQueryString(apiKey, searchEngineId, query, searchType, num, start, hl, gl, safe, site);

        _logger.LogInformation(
            "发起 Google 搜索请求: query={Query}, searchType={SearchType}, start={Start}",
            query, searchType ?? "web", start ?? 1);

        try
        {
            var response = await _httpClient.GetAsync(
                $"https://www.googleapis.com/customsearch/v1?{queryParams}");

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                _logger.LogWarning(
                    "Google API 返回错误: status={StatusCode}, body={ErrorBody}",
                    response.StatusCode, errorBody);

                return new SearchResponse { Kind = "error" };
            }

            var json = await response.Content.ReadAsStringAsync();
            var result = JsonSerializer.Deserialize(json, PluginJsonContext.Default.SearchResponse);

            _logger.LogInformation(
                "Google 搜索成功: query={Query}, itemsCount={ItemsCount}",
                query, result?.Items?.Count ?? 0);

            return result ?? new SearchResponse();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Google 搜索网络错误: query={Query}", query);
            return new SearchResponse { Kind = "network_error" };
        }
    }

    /// <summary>
    /// 构造 URL 查询参数。
    /// </summary>
    private static string BuildQueryString(
        string apiKey,
        string searchEngineId,
        string query,
        string? searchType,
        int? num,
        int? start,
        string? hl,
        string? gl,
        string? safe,
        string? site)
    {
        var parameters = new List<string>
        {
            $"key={Uri.EscapeDataString(apiKey)}",
            $"cx={Uri.EscapeDataString(searchEngineId)}",
            $"q={Uri.EscapeDataString(query)}"
        };

        if (!string.IsNullOrWhiteSpace(searchType))
            parameters.Add($"searchType={Uri.EscapeDataString(searchType)}");

        if (num.HasValue)
            parameters.Add($"num={num.Value}");

        if (start.HasValue)
            parameters.Add($"start={start.Value}");

        if (!string.IsNullOrWhiteSpace(hl))
            parameters.Add($"hl={Uri.EscapeDataString(hl)}");

        if (!string.IsNullOrWhiteSpace(gl))
            parameters.Add($"gl={Uri.EscapeDataString(gl)}");

        if (!string.IsNullOrWhiteSpace(safe))
            parameters.Add($"safe={Uri.EscapeDataString(safe)}");

        if (!string.IsNullOrWhiteSpace(site))
        {
            parameters.Add($"siteSearch={Uri.EscapeDataString(site)}");
            parameters.Add("siteSearchFilter=i");
        }

        return string.Join("&", parameters);
    }
}