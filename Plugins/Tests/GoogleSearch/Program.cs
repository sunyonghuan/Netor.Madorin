using System.Diagnostics;
using System.Text.Json;
using Cortana.Plugins.GoogleSearch;
using Cortana.Plugins.GoogleSearch.Models;
using Cortana.Plugins.GoogleSearch.Services;
using Cortana.Plugins.GoogleSearch.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Netor.Cortana.Plugin.Native.Debugger;

namespace GoogleSearch.Test;

#region 测试框架基础设施

/// <summary>
/// 单个测试用例记录。
/// </summary>
sealed class TestCase
{
    public string Id { get; }
    public string Name { get; }
    public string Description { get; }
    public bool Passed { get; private set; } = true;
    public string Message { get; private set; } = "";
    public List<string> Details { get; } = [];

    public TestCase(string id, string name, string description)
    {
        Id = id;
        Name = name;
        Description = description;
    }

    public void Expect(bool condition, string detail)
    {
        if (!condition)
        {
            Details.Add($"  ❌ {detail}");
            Passed = false;
        }
        else
        {
            Details.Add($"  ✅ {detail}");
        }
    }

    public void Finish(string? conclusion = null)
    {
        if (Passed)
            Message = string.IsNullOrEmpty(conclusion) ? "通过" : conclusion;
        else
            Message = conclusion ?? "失败";
    }
}

/// <summary>
/// 工具返回结果（由插件定义）。
/// 注意：这里的结构与 Cortana.Plugins.GoogleSearch.ToolResult 一致。
/// </summary>
sealed class ToolResult
{
    [System.Text.Json.Serialization.JsonPropertyName("success")]
    public bool Success { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("code")]
    public string Code { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("message")]
    public string Message { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("data")]
    public object? Data { get; set; }
}

/// <summary>
/// 配置查询结果（data 字段）。
/// </summary>
sealed class ConfigQueryResult
{
    [System.Text.Json.Serialization.JsonPropertyName("configured")]
    public bool Configured { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("search_engine_id")]
    public string? SearchEngineId { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("api_key_masked")]
    public string? ApiKeyMasked { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_hl")]
    public string? DefaultHl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_gl")]
    public string? DefaultGl { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("default_safe")]
    public string? DefaultSafe { get; set; }
}

/// <summary>
/// 配置写入结果（data 字段）。
/// </summary>
sealed class ConfigUpdateResult
{
    [System.Text.Json.Serialization.JsonPropertyName("changed")]
    public bool Changed { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("configured")]
    public bool Configured { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("config_file")]
    public string? ConfigFile { get; set; }
}

/// <summary>
/// 搜索响应（来自 GoogleSearchClient 的原始响应）。
/// </summary>
sealed class SearchResponse
{
    [System.Text.Json.Serialization.JsonPropertyName("query")]
    public string? Query { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("items")]
    public List<SearchItem>? Items { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("totalResults")]
    public string? TotalResults { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("searchTimeSeconds")]
    public double SearchTimeSeconds { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("nextStart")]
    public int? NextStart { get; set; }
}

sealed class SearchItem
{
    [System.Text.Json.Serialization.JsonPropertyName("title")]
    public string Title { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("link")]
    public string Link { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("displayLink")]
    public string DisplayLink { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("snippet")]
    public string Snippet { get; set; } = "";
    [System.Text.Json.Serialization.JsonPropertyName("thumbnailLink")]
    public string? ThumbnailLink { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("width")]
    public int Width { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("height")]
    public int Height { get; set; }
    [System.Text.Json.Serialization.JsonPropertyName("contextLink")]
    public string? ContextLink { get; set; }
}

#endregion

public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.InputEncoding = System.Text.Encoding.UTF8;

        if (args.Length > 0 && string.Equals(args[0], "--test", StringComparison.OrdinalIgnoreCase))
        {
            await GoogleSearchTestRunner.RunAsync();
            return;
        }

        await PluginDebugRunner.RunAsync(options =>
        {
            options.WsPort = 12845;
            options.DataDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            options.WorkspaceDirectory = Path.Combine(Directory.GetCurrentDirectory(), "Data");
            options.PluginDirectory = Directory.GetCurrentDirectory();
        });
    }
}

/// <summary>
/// Google 搜索插件测试运行器。
/// </summary>
static class GoogleSearchTestRunner
{
    // 契约定义错误码
    private const string OkCode = "OK";
    private const string InvalidArgumentCode = "INVALID_ARGUMENT";
    private const string ConfigNotInitializedCode = "CONFIG_NOT_INITIALIZED";

    // 集成测试凭据通过环境变量注入，避免将真实密钥提交到仓库。
    private const string TestApiKeyEnvVar = "GOOGLE_SEARCH_TEST_API_KEY";
    private const string TestSearchEngineIdEnvVar = "GOOGLE_SEARCH_TEST_SEARCH_ENGINE_ID";
    private static readonly string TestApiKey = Environment.GetEnvironmentVariable(TestApiKeyEnvVar) ?? string.Empty;
    private static readonly string TestSearchEngineId = Environment.GetEnvironmentVariable(TestSearchEngineIdEnvVar) ?? string.Empty;

    public static async Task RunAsync()
    {
        PrintHeader();

        // 清理并创建测试数据目录（放在 BuildServices 之前，以便 Store 使用）
        string dataDir = Path.Combine(AppContext.BaseDirectory, "TestData");
        if (Directory.Exists(dataDir)) Directory.Delete(dataDir, true);
        Directory.CreateDirectory(dataDir);

        // 构建 DI 容器
        using ServiceProvider provider = BuildServices(dataDir);
        var configStore = provider.GetRequiredService<GoogleSearchConfigStore>();
        var configTools = provider.GetRequiredService<GoogleSearchConfigTools>();
        var searchTools = provider.GetRequiredService<GoogleSearchTools>();

        var results = new List<TestCase>();

        // ========== 测试用例执行 ==========

        // ① 初始状态：未初始化时 get_config 应返回 CONFIG_NOT_INITIALIZED
        RunGetConfigUninitialized(results, configTools);

        if (!HasTestCredentials())
        {
            PrintMissingCredentialsNotice();
            PrintSummary(results);
            return;
        }

        // ② 初始化配置
        RunSetConfig(results, configTools);

        // ③ 初始化后 get_config 应返回配置信息（脱敏 key）
        RunGetConfigInitialized(results, configTools);

        // ④ 网页搜索（已初始化，查询 "Cortana AI"）
        await RunSearchWebAsync(results, searchTools);

        // ⑤ 空 query 应返回 INVALID_ARGUMENT
        RunSearchWebEmptyQuery(results, searchTools);

        // ⑥ 站内搜索（搜索 github.com 内的相关内容）
        await RunSearchSiteAsync(results, searchTools);

        // ⑦ site 参数为空应返回 INVALID_ARGUMENT
        RunSearchSiteEmptySite(results, searchTools);

        // ⑧ 图片搜索（查询 "Cortana logo"）
        await RunSearchImagesAsync(results, searchTools);

        // ⑨ 空 query 应返回 INVALID_ARGUMENT
        RunSearchImagesEmptyQuery(results, searchTools);

        PrintSummary(results);
    }

    private static ServiceProvider BuildServices(string dataDirectory)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.AddSimpleConsole(options => options.SingleLine = true)
            .SetMinimumLevel(LogLevel.Warning));
        services.AddSingleton<GoogleSearchConfigStore>(sp =>
            new GoogleSearchConfigStore(dataDirectory, sp.GetRequiredService<ILogger<GoogleSearchConfigStore>>()));
        services.AddHttpClient<GoogleSearchClient>(client => { client.Timeout = TimeSpan.FromSeconds(15); });
        services.AddSingleton<GoogleSearchService>();
        services.AddSingleton<GoogleSearchConfigTools>();
        services.AddSingleton<GoogleSearchTools>();
        return services.BuildServiceProvider();
    }

    #region 测试方法

    private static void RunGetConfigUninitialized(List<TestCase> results, GoogleSearchConfigTools tools)
    {
        var test = new TestCase("google_search_get_config", "google_search_get_config", "未初始化时应返回 CONFIG_NOT_INITIALIZED");
        results.Add(test);

        // 确保测试前配置为空（使用空数据目录）
        // 通过直接操作 ConfigStore 来重置状态（因测试共享同一个 provider）
        // 实际上测试使用默认 DataDirectory，我们直接测工具行为

        string json = tools.GetConfig();
        ToolResult result = ParseToolResult(json);

        test.Expect(!result.Success, "success 应为 false");
        test.Expect(result.Code == ConfigNotInitializedCode,
            $"code 应为 {ConfigNotInitializedCode}，实际为 {result.Code}");
        test.Expect(result.Message.Contains("初始化"), "错误消息应提示初始化");

        ConfigQueryResult? data = ParseData<ConfigQueryResult>(result);
        test.Expect(data is not null, "data 应可解析");
        test.Expect(data?.Configured == false, "configured 应为 false");

        test.Finish(result.Message);
    }

    private static void RunSetConfig(List<TestCase> results, GoogleSearchConfigTools tools)
    {
        var test = new TestCase("google_search_set_config", "google_search_set_config", "配置写入应返回成功并标记 configured=true");
        results.Add(test);

        string json = tools.SetConfig(TestApiKey, TestSearchEngineId, "zh-CN", "CN", "active");
        ToolResult result = ParseToolResult(json);

        test.Expect(result.Success, "success 应为 true");
        test.Expect(result.Code == OkCode, $"code 应为 {OkCode}，实际为 {result.Code}");

        ConfigUpdateResult? data = ParseData<ConfigUpdateResult>(result);
        test.Expect(data is not null, "data 应可解析");
        test.Expect(data?.Changed == true, "changed 应为 true");
        test.Expect(data?.Configured == true, "configured 应为 true");
        test.Expect(!string.IsNullOrWhiteSpace(data?.ConfigFile), "config_file 应有值");

        test.Finish($"配置文件={data?.ConfigFile}");
    }

    private static void RunGetConfigInitialized(List<TestCase> results, GoogleSearchConfigTools tools)
    {
        var test = new TestCase("google_search_get_config", "google_search_get_config", "已初始化时应返回脱敏后的 API Key");
        results.Add(test);

        string json = tools.GetConfig();
        ToolResult result = ParseToolResult(json);

        test.Expect(result.Success, "success 应为 true");
        test.Expect(result.Code == OkCode, $"code 应为 {OkCode}");

        ConfigQueryResult? data = ParseData<ConfigQueryResult>(result);
        test.Expect(data is not null, "data 应可解析");
        test.Expect(data?.Configured == true, "configured 应为 true");
        test.Expect(!string.IsNullOrWhiteSpace(data?.SearchEngineId), "search_engine_id 应有值");
        test.Expect(!string.IsNullOrWhiteSpace(data?.ApiKeyMasked), "api_key_masked 应有值");
        // 脱敏 key 应包含 "***"
        test.Expect(data?.ApiKeyMasked?.Contains("***") == true,
            $"脱敏 key 应包含 '***'，实际为 {data?.ApiKeyMasked}");
        test.Expect(data?.DefaultHl == "zh-CN", "default_hl 应为 zh-CN");
        test.Expect(data?.DefaultGl == "CN", "default_gl 应为 CN");
        test.Expect(data?.DefaultSafe == "active", "default_safe 应为 active");

        test.Finish($"cx={data?.SearchEngineId}, key={data?.ApiKeyMasked}");
    }

    private static async Task RunSearchWebAsync(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_web", "google_search_web", "已初始化时网页搜索应返回结果");
        results.Add(test);

        Stopwatch sw = Stopwatch.StartNew();
        string json = await tools.SearchWeb("Cortana AI", null, null, 10, null, null, null, null, null);
        sw.Stop();

        ToolResult result = ParseToolResult(json);

        test.Expect(result.Success, "success 应为 true");
        test.Expect(result.Code == OkCode, $"code 应为 {OkCode}，实际为 {result.Code}: {result.Message}");

        SearchResponse? data = ParseSearchResponse(result);
        test.Expect(data is not null, "data 应可解析为 SearchResponse");
        test.Expect(data?.Items != null, "items 不应为 null");
        test.Expect(data?.Items?.Count > 0, $"items 应至少有 1 条，实际: {data?.Items?.Count}");
        test.Expect(!string.IsNullOrWhiteSpace(data?.Items?[0].Title), "首条结果 title 不应为空");
        test.Expect(!string.IsNullOrWhiteSpace(data?.Items?[0].Link), "首条结果 link 不应为空");
        test.Expect(!string.IsNullOrWhiteSpace(data?.Items?[0].Snippet), "首条结果 snippet 不应为空");

        test.Finish($"返回 {data?.Items?.Count} 条结果，耗时 {sw.ElapsedMilliseconds}ms");
    }

    private static void RunSearchWebEmptyQuery(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_web", "google_search_web", "query 为空时应返回 INVALID_ARGUMENT");
        results.Add(test);

        string json = tools.SearchWeb("", null, null, null, null, null, null, null, null).GetAwaiter().GetResult();
        ToolResult result = ParseToolResult(json);

        test.Expect(!result.Success, "success 应为 false");
        test.Expect(result.Code == InvalidArgumentCode,
            $"code 应为 {InvalidArgumentCode}，实际为 {result.Code}");

        test.Finish(result.Message);
    }

    private static async Task RunSearchSiteAsync(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_site", "google_search_site", "已初始化时站内搜索应返回结果");
        results.Add(test);

        Stopwatch sw = Stopwatch.StartNew();
        string json = await tools.SearchSite("Cortana", "github.com", null, null, 10, null, null, null, null);
        sw.Stop();

        ToolResult result = ParseToolResult(json);

        test.Expect(result.Success, "success 应为 true");
        test.Expect(result.Code == OkCode, $"code 应为 {OkCode}，实际为 {result.Code}: {result.Message}");

        SearchResponse? data = ParseSearchResponse(result);
        test.Expect(data?.Items != null, "items 不应为 null");
        test.Expect(data?.Items?.Count > 0, $"items 应至少有 1 条，实际: {data?.Items?.Count}");
        // 站内搜索结果 link 应包含 github.com
        bool hasGithubDomain = data?.Items?.Any(i => i.Link.Contains("github.com")) == true;
        test.Expect(hasGithubDomain, "站内搜索结果 link 应包含 github.com");

        test.Finish($"返回 {data?.Items?.Count} 条结果，耗时 {sw.ElapsedMilliseconds}ms");
    }

    private static void RunSearchSiteEmptySite(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_site", "google_search_site", "site 为空时应返回 INVALID_ARGUMENT");
        results.Add(test);

        string json = tools.SearchSite("Cortana", "", null, null, null, null, null, null, null).GetAwaiter().GetResult();
        ToolResult result = ParseToolResult(json);

        test.Expect(!result.Success, "success 应为 false");
        test.Expect(result.Code == InvalidArgumentCode,
            $"code 应为 {InvalidArgumentCode}，实际为 {result.Code}");

        test.Finish(result.Message);
    }

    private static async Task RunSearchImagesAsync(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_images", "google_search_images", "已初始化时图片搜索应返回结果并包含缩略图字段");
        results.Add(test);

        Stopwatch sw = Stopwatch.StartNew();
        string json = await tools.SearchImages("Cortana logo", null, null, 10, null, null, null, null, null);
        sw.Stop();

        ToolResult result = ParseToolResult(json);

        test.Expect(result.Success, "success 应为 true");
        test.Expect(result.Code == OkCode, $"code 应为 {OkCode}，实际为 {result.Code}: {result.Message}");

        SearchResponse? data = ParseSearchResponse(result);
        test.Expect(data?.Items?.Count > 0, $"items 应至少有 1 条，实际: {data?.Items?.Count}");
        // 图片搜索结果应包含缩略图和宽高
        test.Expect(!string.IsNullOrWhiteSpace(data?.Items?[0].ThumbnailLink),
            "首条结果应包含 thumbnail_link");
        test.Expect(!string.IsNullOrWhiteSpace(data?.Items?[0].ContextLink),
            "首条结果应包含 context_link（图片来源页面）");

        test.Finish($"返回 {data?.Items?.Count} 张图片，耗时 {sw.ElapsedMilliseconds}ms");
    }

    private static void RunSearchImagesEmptyQuery(List<TestCase> results, GoogleSearchTools tools)
    {
        var test = new TestCase("google_search_images", "google_search_images", "query 为空时应返回 INVALID_ARGUMENT");
        results.Add(test);

        string json = tools.SearchImages("", null, null, null, null, null, null, null, null).GetAwaiter().GetResult();
        ToolResult result = ParseToolResult(json);

        test.Expect(!result.Success, "success 应为 false");
        test.Expect(result.Code == InvalidArgumentCode,
            $"code 应为 {InvalidArgumentCode}，实际为 {result.Code}");

        test.Finish(result.Message);
    }

    #endregion

    #region 工具函数

    private static ToolResult ParseToolResult(string json)
    {
        return JsonSerializer.Deserialize<ToolResult>(json)
            ?? throw new InvalidOperationException("工具返回结果无法反序列化为 ToolResult。");
    }

    private static T? ParseData<T>(ToolResult result) where T : class
    {
        if (result.Data == null) return null;
        if (result.Data is JsonElement element)
            return JsonSerializer.Deserialize<T>(element.GetRawText());
        return null;
    }

    private static SearchResponse? ParseSearchResponse(ToolResult result)
    {
        if (result.Data == null) return null;
        if (result.Data is JsonElement element)
            return JsonSerializer.Deserialize<SearchResponse>(element.GetRawText());
        return null;
    }

    private static void PrintHeader()
    {
        Console.WriteLine("============================================================");
        Console.WriteLine("  Google 搜索插件 - 工具测试");
        Console.WriteLine($"  时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  API Key: {MaskCredential(TestApiKey, TestApiKeyEnvVar)}");
        Console.WriteLine($"  Search Engine ID: {MaskCredential(TestSearchEngineId, TestSearchEngineIdEnvVar)}");
        Console.WriteLine("============================================================");
        Console.WriteLine();
    }

    private static bool HasTestCredentials()
    {
        return !string.IsNullOrWhiteSpace(TestApiKey)
            && !string.IsNullOrWhiteSpace(TestSearchEngineId);
    }

    private static string MaskCredential(string value, string envVarName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return $"未设置（环境变量 {envVarName}）";
        }

        if (value.Length <= 8)
        {
            return "已设置";
        }

        return $"{value[..4]}***{value[^4..]}";
    }

    private static void PrintMissingCredentialsNotice()
    {
        Console.WriteLine("  未检测到 Google 搜索集成测试凭据，跳过需要外部 API 的测试用例。");
        Console.WriteLine($"  请设置环境变量 {TestApiKeyEnvVar} 和 {TestSearchEngineIdEnvVar} 后重新运行。\n");
    }

    private static void PrintSummary(List<TestCase> results)
    {
        Console.WriteLine();
        Console.WriteLine("============================================================");
        Console.WriteLine("  测试结果摘要");
        Console.WriteLine("============================================================");

        int passed = results.Count(r => r.Passed);
        int failed = results.Count - passed;

        foreach (var r in results)
        {
            string status = r.Passed ? "✅ PASS" : "❌ FAIL";
            Console.WriteLine($"  {status} [{r.Id}] {r.Name}");
            Console.WriteLine($"         {r.Description}");
            if (!r.Passed)
            {
                foreach (string detail in r.Details.Where(d => d.Contains("❌")))
                    Console.WriteLine(detail);
            }
            Console.WriteLine($"         结论: {r.Message}");
            Console.WriteLine();
        }

        Console.WriteLine($"  总计: {results.Count} | ✅ {passed} | ❌ {failed}");
        Console.WriteLine("============================================================");
    }

    #endregion
}