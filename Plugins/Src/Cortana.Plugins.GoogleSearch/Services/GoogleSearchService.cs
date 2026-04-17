using Cortana.Plugins.GoogleSearch.Models;
using Microsoft.Extensions.Logging;

namespace Cortana.Plugins.GoogleSearch.Services;

/// <summary>
/// 负责搜索业务逻辑编排。
/// 职责单一：配置校验、参数裁剪、响应归一化和错误映射。
/// 不直接处理 HTTP 和文件 I/O。
/// </summary>
public sealed class GoogleSearchService
{
    private readonly GoogleSearchConfigStore _configStore;
    private readonly GoogleSearchClient _client;
    private readonly ILogger<GoogleSearchService> _logger;

    public GoogleSearchService(
        GoogleSearchConfigStore configStore,
        GoogleSearchClient client,
        ILogger<GoogleSearchService> logger)
    {
        _configStore = configStore;
        _client = client;
        _logger = logger;
    }

    /// <summary>
    /// 查询当前配置状态。
    /// </summary>
    public ConfigQueryResult GetConfig()
    {
        var config = _configStore.Load();

        return new ConfigQueryResult
        {
            Configured = config.IsComplete(),
            SearchEngineId = config.SearchEngineId,
            ApiKeyMasked = config.IsComplete()
                ? ConfigQueryResult.MaskApiKey(config.ApiKey)
                : null,
            DefaultHl = config.DefaultHl,
            DefaultGl = config.DefaultGl,
            DefaultSafe = config.DefaultSafe
        };
    }

    /// <summary>
    /// 初始化或更新配置。
    /// </summary>
    public ConfigUpdateResult SetConfig(
        string apiKey,
        string searchEngineId,
        string? defaultHl,
        string? defaultGl,
        string? defaultSafe)
    {
        var config = new GoogleSearchConfig
        {
            ApiKey = apiKey,
            SearchEngineId = searchEngineId,
            DefaultHl = defaultHl,
            DefaultGl = defaultGl,
            DefaultSafe = defaultSafe
        };

        _configStore.Save(config);

        _logger.LogInformation(
            "配置已更新: searchEngineId={SearchEngineId}, defaultHl={Hl}, defaultGl={Gl}",
            searchEngineId, defaultHl, defaultGl);

        return new ConfigUpdateResult
        {
            Changed = true,
            Configured = config.IsComplete(),
            ConfigFile = _configStore.GetConfigFilePath()
        };
    }

    /// <summary>
    /// 执行网页搜索。
    /// 优先使用已保存配置，忽略缺失的 api_key 和 search_engine_id 时返回未初始化错误。
    /// </summary>
    public async Task<SearchResponse> SearchWebAsync(
        string? apiKey,
        string? searchEngineId,
        string query,
        int? num,
        int? start,
        string? hl,
        string? gl,
        string? safe)
    {
        var (effectiveApiKey, effectiveSearchEngineId, effectiveHl, effectiveGl, effectiveSafe) =
            ResolveConfig(apiKey, searchEngineId, hl, gl, safe);

        if (string.IsNullOrWhiteSpace(effectiveApiKey) ||
            string.IsNullOrWhiteSpace(effectiveSearchEngineId))
        {
            _logger.LogWarning("搜索请求被拒绝：插件尚未初始化配置");
            return new SearchResponse { Kind = "not_initialized" };
        }

        return await _client.SearchAsync(
            effectiveApiKey,
            effectiveSearchEngineId,
            query,
            searchType: null,
            num, start,
            effectiveHl, effectiveGl, effectiveSafe,
            site: null);
    }

    /// <summary>
    /// 执行站内搜索。
    /// </summary>
    public async Task<SearchResponse> SearchSiteAsync(
        string? apiKey,
        string? searchEngineId,
        string query,
        string site,
        int? num,
        int? start,
        string? hl,
        string? gl,
        string? safe)
    {
        var (effectiveApiKey, effectiveSearchEngineId, effectiveHl, effectiveGl, effectiveSafe) =
            ResolveConfig(apiKey, searchEngineId, hl, gl, safe);

        if (string.IsNullOrWhiteSpace(effectiveApiKey) ||
            string.IsNullOrWhiteSpace(effectiveSearchEngineId))
        {
            _logger.LogWarning("站内搜索请求被拒绝：插件尚未初始化配置");
            return new SearchResponse { Kind = "not_initialized" };
        }

        return await _client.SearchAsync(
            effectiveApiKey,
            effectiveSearchEngineId,
            query,
            searchType: null,
            num, start,
            effectiveHl, effectiveGl, effectiveSafe,
            site);
    }

    /// <summary>
    /// 执行图片搜索。
    /// </summary>
    public async Task<SearchResponse> SearchImagesAsync(
        string? apiKey,
        string? searchEngineId,
        string query,
        int? num,
        int? start,
        string? hl,
        string? gl,
        string? safe,
        string? site)
    {
        var (effectiveApiKey, effectiveSearchEngineId, effectiveHl, effectiveGl, effectiveSafe) =
            ResolveConfig(apiKey, searchEngineId, hl, gl, safe);

        if (string.IsNullOrWhiteSpace(effectiveApiKey) ||
            string.IsNullOrWhiteSpace(effectiveSearchEngineId))
        {
            _logger.LogWarning("图片搜索请求被拒绝：插件尚未初始化配置");
            return new SearchResponse { Kind = "not_initialized" };
        }

        return await _client.SearchAsync(
            effectiveApiKey,
            effectiveSearchEngineId,
            query,
            searchType: "image",
            num, start,
            effectiveHl, effectiveGl, effectiveSafe,
            site);
    }

    /// <summary>
    /// 解析出本次请求实际使用的配置。
    /// 优先使用调用方显式传入的参数，否则回退到已保存的配置文件。
    /// </summary>
    private (string? ApiKey, string? SearchEngineId, string? Hl, string? Gl, string? Safe) ResolveConfig(
        string? apiKey,
        string? searchEngineId,
        string? hl,
        string? gl,
        string? safe)
    {
        var config = _configStore.Load();

        return (
            ApiKey: string.IsNullOrWhiteSpace(apiKey) ? config.ApiKey : apiKey,
            SearchEngineId: string.IsNullOrWhiteSpace(searchEngineId) ? config.SearchEngineId : searchEngineId,
            Hl: string.IsNullOrWhiteSpace(hl) ? config.DefaultHl : hl,
            Gl: string.IsNullOrWhiteSpace(gl) ? config.DefaultGl : gl,
            Safe: string.IsNullOrWhiteSpace(safe) ? config.DefaultSafe : safe
        );
    }
}