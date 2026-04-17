using Cortana.Plugins.GoogleSearch.Services;
using Cortana.Plugins.GoogleSearch.Tools;
using Microsoft.Extensions.DependencyInjection;
using Netor.Cortana.Plugin.Native;

namespace Cortana.Plugins.GoogleSearch;

/// <summary>
/// 谷歌搜索插件入口，负责注册工具运行所需的依赖。
/// </summary>
[Plugin(
    Id = "google_search",
    Name = "谷歌搜索插件",
    Version = "1.0.1",
    Description = "提供 Google 搜索能力，支持网页搜索、站内搜索和图片搜索。",
    Tags = ["搜索", "Google", "网页搜索", "图片搜索"],
    Instructions = """
        初始化流程：插件启动后，首次使用搜索工具时，若尚未配置，工具会返回错误。此时调用一次 google_search_set_config 完成初始化（配置会写入磁盘持久化保存）。后续插件重新启动时，配置从磁盘自动加载，无需再次调用 set_config。
        搜索时会优先使用已保存的配置，也可以通过传参覆盖本次请求的 api_key 和 search_engine_id。
        配置查询工具 google_search_get_config 会返回脱敏后的 API Key，不会显示明文。
        """)]
public static partial class Startup
{
    /// <summary>
    /// 向插件容器注册谷歌搜索插件使用的服务。
    /// </summary>
    public static void Configure(IServiceCollection services)
    {
        services.AddLogging();

        // 配置存储服务
        // 注意：PluginSettings.DataDirectory 由框架自动注入，是插件数据目录的根路径。
        // 我们在其下创建 config.json 保存用户凭据。
        services.AddSingleton<GoogleSearchConfigStore>();

        // HTTP 客户端，由 DI 容器管理生命周期和连接复用
        services.AddHttpClient<GoogleSearchClient>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(15);
        });

        // 搜索业务逻辑层
        services.AddSingleton<GoogleSearchService>();
    }
}